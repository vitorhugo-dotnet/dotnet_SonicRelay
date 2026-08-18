using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SonicRelay.Api.Observability;
using SonicRelay.Application.Abstractions;
using SonicRelay.Infrastructure.Persistence;

namespace SonicRelay.Api.Services;

/// <summary>Aggregate outcome of one retention pass. Counts only — never the rows themselves.</summary>
public sealed class DataRetentionReport
{
    private readonly Dictionary<string, int> _deleted = new(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, int> Deleted => _deleted;

    /// <summary>Entity groups whose sweep threw after exhausting its retries.</summary>
    public List<string> FailedEntities { get; } = [];

    public bool Succeeded => FailedEntities.Count == 0;

    public int TotalDeleted => _deleted.Values.Sum();

    internal void Add(string entity, int count)
    {
        if (count <= 0) return;
        _deleted[entity] = _deleted.TryGetValue(entity, out var existing) ? existing + count : count;
    }
}

/// <summary>
/// Enforces the 90-day retention ceiling SonicRelay declares in its Google Play Data Safety
/// entry (issue #44). Every sweep measures a row's age from the timestamp at which it was
/// <em>collected</em> — <c>CreatedAt</c>/<c>JoinedAt</c> — because the declaration is about how
/// long data may exist, not about how long it has been idle. Deletion is unconditionally hard:
/// a soft-deleted row still stores the identifier it was supposed to erase.
///
/// The sweep runs at <see cref="DataRetentionOptions.EffectiveRetention"/>, one day inside the
/// declared ceiling, so a late tick or a short outage cannot push data past 90 days.
///
/// Each entity group is swept, retried and saved independently: a pass that fails on one table
/// still deletes everything else, and the next tick retries what was left. Every predicate is a
/// pure "older than the cutoff" filter, so re-running a pass — or running two concurrently —
/// converges on the same state rather than double-deleting.
/// </summary>
public sealed class DataRetentionService(
    IServiceScopeFactory scopeFactory,
    IOptions<DataRetentionOptions> options,
    DataRetentionMetrics metrics,
    DataRetentionState state,
    TimeProvider time,
    ILogger<DataRetentionService> logger) : BackgroundService
{
    public async Task<DataRetentionReport> CleanupOnceAsync(CancellationToken ct)
    {
        var settings = options.Value;
        var now = time.GetUtcNow();
        var cutoff = now - settings.EffectiveRetention;
        var challengeCutoff = now - settings.PairingChallengeRetention;
        var report = new DataRetentionReport();

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var codeStore = scope.ServiceProvider.GetRequiredService<ISessionCodeStore>();

        // Order matters. There are no database-level cascades (see docs/data-retention.md): the
        // schema stores relationships as plain id columns so identity rotation can re-point them
        // without a cascade tearing down a live session. Referential integrity during cleanup is
        // therefore the application's job, and children go before their parents.
        await SweepAsync(report, "signaling_event", ct, () => DeleteSignalingEventsAsync(db, cutoff, ct));
        await SweepAsync(report, "session_participant", ct, () => DeleteParticipantsAsync(db, cutoff, ct));
        await SweepAsync(report, "stream_session", ct, () => DeleteSessionsAsync(db, codeStore, report, cutoff, ct));
        await SweepAsync(report, "pairing_challenge", ct,
            () => DeleteChallengesAsync(db, cutoff, challengeCutoff, ct));
        await SweepAsync(report, "device_pairing", ct, () => DeletePairingsAsync(db, cutoff, ct));
        await SweepAsync(report, "relay_device_settings", ct, () => DeleteRelaySettingsAsync(db, cutoff, ct));
        await SweepAsync(report, "device_identity", ct,
            () => DeleteDeviceIdentitiesAsync(db, codeStore, report, cutoff, ct));
        await SweepAsync(report, "orphan", ct, () => DeleteOrphansAsync(db, report, ct));

        metrics.RecordRun();
        foreach (var (entity, count) in report.Deleted)
        {
            metrics.RecordDeleted(entity, count);
        }

        if (report.Succeeded)
        {
            state.MarkSuccess(now);
            metrics.RecordSuccess(now);
        }
        else
        {
            metrics.RecordFailure();
        }

        if (report.TotalDeleted > 0 || !report.Succeeded)
        {
            // Aggregate counts only. Naming a deleted device or session here would leave the
            // identifier sitting in the log after it was erased from the database.
            logger.LogInformation(
                "Data retention pass removed {DeletedCount} records across {EntityCount} entities "
                + "(cutoff {CutoffDays}d); failed entities: {FailedEntityCount}",
                report.TotalDeleted, report.Deleted.Count,
                (int)settings.EffectiveRetention.TotalDays, report.FailedEntities.Count);
        }

        return report;
    }

    /// <summary>
    /// Runs one entity group with bounded retries. A transient database fault costs that group
    /// its slot in this pass and nothing more — the pass continues and the next tick retries.
    /// </summary>
    private async Task SweepAsync(
        DataRetentionReport report, string entity, CancellationToken ct, Func<Task<int>> sweep)
    {
        var attempts = Math.Clamp(options.Value.MaxAttemptsPerEntity, 1, 10);
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            try
            {
                report.Add(entity, await sweep());
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (DbUpdateConcurrencyException)
            {
                // Another pass deleted the same rows first. The end state is the one we wanted,
                // so this is a success, not a failure.
                return;
            }
            catch (Exception exception)
            {
                if (attempt == attempts)
                {
                    report.FailedEntities.Add(entity);
                    logger.LogError(exception,
                        "Data retention sweep for {Entity} failed after {AttemptCount} attempts",
                        entity, attempts);
                    return;
                }
                logger.LogWarning(
                    "Data retention sweep for {Entity} failed on attempt {Attempt}; retrying", entity, attempt);
            }
        }
    }

    private async Task<int> DeleteSignalingEventsAsync(AppDbContext db, DateTimeOffset cutoff, CancellationToken ct)
    {
        var rows = await Batch(db.SignalingEvents.Where(x => x.CreatedAt <= cutoff)).ToListAsync(ct);
        if (rows.Count == 0) return 0;
        db.SignalingEvents.RemoveRange(rows);
        await db.SaveChangesAsync(ct);
        return rows.Count;
    }

    private async Task<int> DeleteParticipantsAsync(AppDbContext db, DateTimeOffset cutoff, CancellationToken ct)
    {
        var rows = await Batch(db.SessionParticipants.Where(x => x.JoinedAt <= cutoff)).ToListAsync(ct);
        if (rows.Count == 0) return 0;
        db.SessionParticipants.RemoveRange(rows);
        await db.SaveChangesAsync(ct);
        return rows.Count;
    }

    private async Task<int> DeleteSessionsAsync(
        AppDbContext db, ISessionCodeStore codeStore, DataRetentionReport report,
        DateTimeOffset cutoff, CancellationToken ct)
    {
        var sessions = await Batch(db.StreamSessions.Where(x => x.CreatedAt <= cutoff)).ToListAsync(ct);
        if (sessions.Count == 0) return 0;

        var sessionIds = sessions.Select(x => x.Id).ToList();
        report.Add("session_participant", await DeleteBySessionAsync(db, sessionIds, ct));
        db.StreamSessions.RemoveRange(sessions);
        await db.SaveChangesAsync(ct);

        // The Redis join-code entry expires on its own TTL, but dropping it here keeps the code
        // from resolving to a session id that no longer exists.
        foreach (var sessionId in sessionIds)
        {
            await codeStore.RemoveAsync(sessionId, ct);
        }
        return sessions.Count;
    }

    private static async Task<int> DeleteBySessionAsync(
        AppDbContext db, IReadOnlyCollection<Guid> sessionIds, CancellationToken ct)
    {
        var events = await db.SignalingEvents.Where(x => sessionIds.Contains(x.SessionId)).ToListAsync(ct);
        db.SignalingEvents.RemoveRange(events);
        var participants = await db.SessionParticipants
            .Where(x => sessionIds.Contains(x.SessionId)).ToListAsync(ct);
        db.SessionParticipants.RemoveRange(participants);
        return participants.Count;
    }

    private async Task<int> DeleteChallengesAsync(
        AppDbContext db, DateTimeOffset cutoff, DateTimeOffset challengeCutoff, CancellationToken ct)
    {
        // A challenge is a short-lived secret: once it has expired or been consumed it is useless
        // and dies on the challenge clock, far inside the global ceiling. The CreatedAt term is
        // the backstop for a row that somehow never expired.
        var rows = await Batch(db.PairingChallenges
                .Where(x => x.CreatedAt <= cutoff
                    || ((x.ConsumedAt != null || x.ExpiresAt <= challengeCutoff)
                        && x.CreatedAt <= challengeCutoff)))
            .ToListAsync(ct);
        if (rows.Count == 0) return 0;
        db.PairingChallenges.RemoveRange(rows);
        await db.SaveChangesAsync(ct);
        return rows.Count;
    }

    private async Task<int> DeletePairingsAsync(AppDbContext db, DateTimeOffset cutoff, CancellationToken ct)
    {
        var rows = await Batch(db.DevicePairings.Where(x => x.CreatedAt <= cutoff)).ToListAsync(ct);
        if (rows.Count == 0) return 0;
        db.DevicePairings.RemoveRange(rows);
        await db.SaveChangesAsync(ct);
        return rows.Count;
    }

    private async Task<int> DeleteRelaySettingsAsync(AppDbContext db, DateTimeOffset cutoff, CancellationToken ct)
    {
        var rows = await Batch(db.RelayDeviceSettings.Where(x => x.CreatedAt <= cutoff)).ToListAsync(ct);
        if (rows.Count == 0) return 0;
        db.RelayDeviceSettings.RemoveRange(rows);
        await db.SaveChangesAsync(ct);
        return rows.Count;
    }

    private async Task<int> DeleteDeviceIdentitiesAsync(
        AppDbContext db, ISessionCodeStore codeStore, DataRetentionReport report,
        DateTimeOffset cutoff, CancellationToken ct)
    {
        var devices = await Batch(db.DeviceIdentities.Where(x => x.CreatedAt <= cutoff)).ToListAsync(ct);
        if (devices.Count == 0) return 0;

        var deviceIds = devices.Select(x => x.Id).ToList();

        var challenges = await db.PairingChallenges
            .Where(x => deviceIds.Contains(x.PublisherDeviceId)).ToListAsync(ct);
        db.PairingChallenges.RemoveRange(challenges);
        report.Add("pairing_challenge", challenges.Count);

        var pairings = await db.DevicePairings
            .Where(x => deviceIds.Contains(x.PublisherDeviceId) || deviceIds.Contains(x.ViewerDeviceId))
            .ToListAsync(ct);
        db.DevicePairings.RemoveRange(pairings);
        report.Add("device_pairing", pairings.Count);

        var settings = await db.RelayDeviceSettings
            .Where(x => deviceIds.Contains(x.DeviceId)).ToListAsync(ct);
        db.RelayDeviceSettings.RemoveRange(settings);
        report.Add("relay_device_settings", settings.Count);

        var ownedSessionIds = await db.StreamSessions
            .Where(x => deviceIds.Contains(x.SourceDeviceId)).Select(x => x.Id).ToListAsync(ct);
        report.Add("session_participant", await DeleteBySessionAsync(db, ownedSessionIds, ct));
        var ownedSessions = await db.StreamSessions
            .Where(x => ownedSessionIds.Contains(x.Id)).ToListAsync(ct);
        db.StreamSessions.RemoveRange(ownedSessions);
        report.Add("stream_session", ownedSessions.Count);

        // A device can also be a viewer in someone else's session; those participant rows carry
        // its id and have to go with it even though the session itself survives.
        var participants = await db.SessionParticipants
            .Where(x => deviceIds.Contains(x.DeviceId)).ToListAsync(ct);
        db.SessionParticipants.RemoveRange(participants);
        report.Add("session_participant", participants.Count);

        db.DeviceIdentities.RemoveRange(devices);
        await db.SaveChangesAsync(ct);

        foreach (var sessionId in ownedSessionIds)
        {
            await codeStore.RemoveAsync(sessionId, ct);
        }
        return devices.Count;
    }

    /// <summary>
    /// Removes rows that point at a parent that no longer exists. Ordered deletion is what
    /// prevents orphans; this is the safety net that also repairs anything an earlier partial
    /// failure — or any other code path — left dangling, so a stale identifier cannot survive by
    /// hiding in a child table.
    /// </summary>
    private async Task<int> DeleteOrphansAsync(AppDbContext db, DataRetentionReport report, CancellationToken ct)
    {
        var removed = 0;

        var orphanEvents = await Batch(db.SignalingEvents
                .Where(x => !db.StreamSessions.Any(s => s.Id == x.SessionId)))
            .ToListAsync(ct);
        db.SignalingEvents.RemoveRange(orphanEvents);
        report.Add("signaling_event", orphanEvents.Count);
        removed += orphanEvents.Count;

        var orphanParticipants = await Batch(db.SessionParticipants
                .Where(x => !db.StreamSessions.Any(s => s.Id == x.SessionId)
                    || !db.DeviceIdentities.Any(d => d.Id == x.DeviceId)))
            .ToListAsync(ct);
        db.SessionParticipants.RemoveRange(orphanParticipants);
        report.Add("session_participant", orphanParticipants.Count);
        removed += orphanParticipants.Count;

        var orphanSessions = await Batch(db.StreamSessions
                .Where(x => !db.DeviceIdentities.Any(d => d.Id == x.SourceDeviceId)))
            .ToListAsync(ct);
        db.StreamSessions.RemoveRange(orphanSessions);
        report.Add("stream_session", orphanSessions.Count);
        removed += orphanSessions.Count;

        var orphanChallenges = await Batch(db.PairingChallenges
                .Where(x => !db.DeviceIdentities.Any(d => d.Id == x.PublisherDeviceId)))
            .ToListAsync(ct);
        db.PairingChallenges.RemoveRange(orphanChallenges);
        report.Add("pairing_challenge", orphanChallenges.Count);
        removed += orphanChallenges.Count;

        var orphanPairings = await Batch(db.DevicePairings
                .Where(x => !db.DeviceIdentities.Any(d => d.Id == x.PublisherDeviceId)
                    || !db.DeviceIdentities.Any(d => d.Id == x.ViewerDeviceId)))
            .ToListAsync(ct);
        db.DevicePairings.RemoveRange(orphanPairings);
        report.Add("device_pairing", orphanPairings.Count);
        removed += orphanPairings.Count;

        var orphanSettings = await Batch(db.RelayDeviceSettings
                .Where(x => !db.DeviceIdentities.Any(d => d.Id == x.DeviceId)))
            .ToListAsync(ct);
        db.RelayDeviceSettings.RemoveRange(orphanSettings);
        report.Add("relay_device_settings", orphanSettings.Count);
        removed += orphanSettings.Count;

        if (removed > 0) await db.SaveChangesAsync(ct);
        // Counted per entity above; the "orphan" group itself contributes no separate total.
        return 0;
    }

    private IQueryable<T> Batch<T>(IQueryable<T> query) where T : class =>
        query.Take(Math.Clamp(options.Value.BatchSize, 1, 10_000));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
        {
            logger.LogWarning(
                "Data retention cleanup is disabled; SonicRelay's 90-day deletion guarantee is not being enforced");
            return;
        }

        // Run once at startup so a deployment that was down over a scheduled tick catches up
        // immediately instead of waiting a full interval.
        using var timer = new PeriodicTimer(options.Value.CleanupInterval);
        do
        {
            try
            {
                await CleanupOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                // A failed pass must never take the loop down with it — the next tick retries.
                metrics.RecordFailure();
                logger.LogError(exception, "Data retention pass failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
