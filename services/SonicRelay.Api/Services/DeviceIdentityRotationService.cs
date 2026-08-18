using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SonicRelay.Api.Observability;
using SonicRelay.Domain.DeviceIdentities;
using SonicRelay.Domain.RelaySettings;
using SonicRelay.Infrastructure.Persistence;

namespace SonicRelay.Api.Services;

/// <summary>The outcome of a rotation check at token exchange.</summary>
/// <param name="Device">The identity the caller should be authenticated as from now on.</param>
/// <param name="RotatedCredentialSecret">
/// The plaintext credential of the replacement identity, returned exactly once. Null when no
/// rotation happened.
/// </param>
public readonly record struct DeviceIdentityRotationResult(
    DeviceIdentity Device,
    string? RotatedCredentialSecret)
{
    public bool Rotated => RotatedCredentialSecret is not null;
}

/// <summary>
/// Keeps a device's identifier from outliving the declared retention window (issue #44).
///
/// A <c>deviceId</c> is a collected identifier: it is stored, it is linkable across sessions,
/// and refreshing <c>LastSeenAt</c> or the credential does not make it any younger. So rather
/// than letting one identifier live forever, an identity that reaches
/// <see cref="DataRetentionOptions.DeviceIdentityRotationAfter"/> is replaced during a normal
/// token exchange: a brand-new row with a new id and a new credential is created, everything
/// that referenced the old id is re-pointed at it, and the old row is <em>hard deleted</em> in
/// the same transaction. Nothing records which identity replaced which, so the previous
/// identifier cannot be reconstructed from what remains.
///
/// Re-pointed rows deliberately keep their own <c>CreatedAt</c>: rotation moves a pairing or a
/// session to a new owner, it never restarts that row's retention clock.
/// </summary>
public sealed class DeviceIdentityRotationService(
    AppDbContext db,
    DeviceCredentialService credentials,
    IOptions<DataRetentionOptions> retentionOptions,
    DataRetentionMetrics metrics,
    TimeProvider time,
    ILogger<DeviceIdentityRotationService> logger)
{
    // Serializes rotation per device inside this process so two concurrent token requests from
    // the same device cannot each mint a replacement identity. A second instance would still be
    // caught by the delete-the-old-row concurrency check below.
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> Gates = new();

    public bool IsDue(DeviceIdentity device) =>
        time.GetUtcNow() - device.CreatedAt >= retentionOptions.Value.DeviceIdentityRotationAfter;

    /// <summary>
    /// Replaces <paramref name="device"/> with a fresh identity when it has reached the rotation
    /// deadline. Returns null when a concurrent rotation already replaced it — the caller should
    /// treat that as an unauthenticated request, since the credential it presented no longer
    /// belongs to any stored identity.
    /// </summary>
    public async Task<DeviceIdentityRotationResult?> RotateIfDueAsync(
        DeviceIdentity device, CancellationToken ct)
    {
        if (!IsDue(device)) return new DeviceIdentityRotationResult(device, null);

        var gate = Gates.GetOrAdd(device.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            // Re-read under the gate: the identity may have been rotated (and therefore deleted)
            // while this request waited.
            var current = await db.DeviceIdentities.SingleOrDefaultAsync(x => x.Id == device.Id, ct);
            if (current is null) return null;
            if (!IsDue(current)) return new DeviceIdentityRotationResult(current, null);

            return await RotateAsync(current, ct);
        }
        finally
        {
            gate.Release();
            Gates.TryRemove(device.Id, out _);
        }
    }

    private async Task<DeviceIdentityRotationResult?> RotateAsync(DeviceIdentity current, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var (plaintext, hash) = credentials.GenerateCredential();
        var replacement = new DeviceIdentity
        {
            Id = Guid.NewGuid(),
            Name = current.Name,
            DeviceType = current.DeviceType,
            Platform = current.Platform,
            CredentialSecretHash = hash,
            CredentialVersion = 1,
            Status = DeviceIdentityStatuses.Active,
            CreatedAt = now,
            LastSeenAt = now
        };
        db.DeviceIdentities.Add(replacement);

        // Pairings and sessions follow the device so a rotation is invisible to the user: an
        // established pairing keeps working and a live session is not torn down mid-stream.
        var pairings = await db.DevicePairings
            .Where(x => x.PublisherDeviceId == current.Id || x.ViewerDeviceId == current.Id)
            .ToListAsync(ct);
        foreach (var pairing in pairings)
        {
            if (pairing.PublisherDeviceId == current.Id) pairing.PublisherDeviceId = replacement.Id;
            if (pairing.ViewerDeviceId == current.Id) pairing.ViewerDeviceId = replacement.Id;
        }

        var sessions = await db.StreamSessions.Where(x => x.SourceDeviceId == current.Id).ToListAsync(ct);
        foreach (var session in sessions) session.SourceDeviceId = replacement.Id;

        var participants = await db.SessionParticipants.Where(x => x.DeviceId == current.Id).ToListAsync(ct);
        foreach (var participant in participants) participant.DeviceId = replacement.Id;

        // Relay settings are keyed by device id, so the row is moved rather than updated. Its
        // original CreatedAt travels with it — the settings still expire on their own schedule.
        var settings = await db.RelayDeviceSettings.SingleOrDefaultAsync(x => x.DeviceId == current.Id, ct);
        if (settings is not null)
        {
            db.RelayDeviceSettings.Remove(settings);
            db.RelayDeviceSettings.Add(new RelayDeviceSettings
            {
                DeviceId = replacement.Id,
                RelayMode = settings.RelayMode,
                TurnUris = settings.TurnUris,
                TurnUsername = settings.TurnUsername,
                TurnCredential = settings.TurnCredential,
                CreatedAt = settings.CreatedAt,
                UpdatedAt = settings.UpdatedAt
            });
        }

        // Outstanding pairing challenges are short-lived and carry a hash bound to the old id;
        // they are destroyed rather than moved, and the publisher simply issues a new code.
        var challenges = await db.PairingChallenges
            .Where(x => x.PublisherDeviceId == current.Id)
            .ToListAsync(ct);
        db.PairingChallenges.RemoveRange(challenges);

        db.DeviceIdentities.Remove(current);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Another instance rotated this identity first. Its replacement is authoritative;
            // this request's credential no longer identifies anything.
            db.ChangeTracker.Clear();
            return null;
        }

        metrics.RecordIdentityRotation();
        // Neither identifier is logged: printing them would re-create exactly the old-to-new
        // link that rotation exists to destroy.
        logger.LogInformation(
            "Rotated a device identity that reached the retention rotation deadline "
            + "({PairingCount} pairings, {SessionCount} sessions, {ParticipantCount} participants moved)",
            pairings.Count, sessions.Count, participants.Count);

        return new DeviceIdentityRotationResult(replacement, plaintext);
    }
}
