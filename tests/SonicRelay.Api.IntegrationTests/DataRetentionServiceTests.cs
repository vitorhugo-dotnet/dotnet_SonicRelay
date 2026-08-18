using Microsoft.EntityFrameworkCore;
using SonicRelay.Domain.DeviceIdentities;
using SonicRelay.Domain.Devices;
using SonicRelay.Domain.RelaySettings;
using SonicRelay.Domain.Sessions;
using SonicRelay.Domain.Signaling;
using SonicRelay.Infrastructure.Persistence;
using Xunit;

namespace SonicRelay.Api.IntegrationTests;

/// <summary>
/// Guards the promise behind SonicRelay's Google Play Data Safety declaration (issue #44):
/// everything collected is destroyed automatically within 90 days of collection. The boundary
/// cases matter most — a policy that only deletes "clearly ancient" rows would still let data
/// sit past the declared ceiling.
/// </summary>
public sealed class DataRetentionServiceTests
{
    // Offsets in days relative to the policy's own cutoff, so the tests keep testing the
    // boundary if the configured ceiling, safety margin or backup window ever changes.
    private const double JustInside = -1;
    private const double AtTheLimit = 0;
    private const double WellPast = 30;


    [Fact]
    public async Task Data_collected_within_the_retention_window_is_kept()
    {
        await using var harness = DataRetentionTestHarness.Create();
        var deviceId = Guid.NewGuid();
        await harness.SeedAsync(db => SeedGraph(db, deviceId, harness.CutoffOffset(JustInside)));

        var report = await harness.RunAsync();

        Assert.True(report.Succeeded);
        // Only the spent pairing challenge goes: it is a short-lived secret on its own clock.
        Assert.Equal(1, report.TotalDeleted);
        Assert.Equal("pairing_challenge", Assert.Single(report.Deleted).Key);
        Assert.Equal(1, await harness.QueryAsync(db => db.DeviceIdentities.CountAsync()));
        Assert.Equal(1, await harness.QueryAsync(db => db.StreamSessions.CountAsync()));
        Assert.Equal(1, await harness.QueryAsync(db => db.SessionParticipants.CountAsync()));
        Assert.Equal(1, await harness.QueryAsync(db => db.DevicePairings.CountAsync()));
    }

    [Theory]
    [InlineData(AtTheLimit)]
    [InlineData(WellPast)]
    public async Task Data_that_reached_the_retention_limit_is_hard_deleted(double ageDays)
    {
        await using var harness = DataRetentionTestHarness.Create();
        var deviceId = Guid.NewGuid();
        await harness.SeedAsync(db => SeedGraph(db, deviceId, harness.CutoffOffset(ageDays)));

        var report = await harness.RunAsync();

        Assert.True(report.Succeeded);
        // Hard delete, not a status flag: nothing identifiable may remain in any table.
        Assert.Empty(await harness.QueryAsync(db => db.DeviceIdentities.ToListAsync()));
        Assert.Empty(await harness.QueryAsync(db => db.StreamSessions.ToListAsync()));
        Assert.Empty(await harness.QueryAsync(db => db.SessionParticipants.ToListAsync()));
        Assert.Empty(await harness.QueryAsync(db => db.SignalingEvents.ToListAsync()));
        Assert.Empty(await harness.QueryAsync(db => db.DevicePairings.ToListAsync()));
        Assert.Empty(await harness.QueryAsync(db => db.PairingChallenges.ToListAsync()));
        Assert.Empty(await harness.QueryAsync(db => db.RelayDeviceSettings.ToListAsync()));
    }

    [Fact]
    public async Task A_row_crosses_the_limit_as_the_clock_advances()
    {
        await using var harness = DataRetentionTestHarness.Create();
        var deviceId = Guid.NewGuid();
        await harness.SeedAsync(db => SeedGraph(db, deviceId, harness.CutoffOffset(JustInside)));

        await harness.RunAsync();
        Assert.Equal(1, await harness.QueryAsync(db => db.DeviceIdentities.CountAsync()));

        // One more day is all it takes; retention is measured from collection, and the row has
        // not been touched since.
        harness.Clock.Advance(TimeSpan.FromDays(1));
        await harness.RunAsync();

        Assert.Equal(0, await harness.QueryAsync(db => db.DeviceIdentities.CountAsync()));
    }

    [Fact]
    public async Task Activity_does_not_extend_retention_beyond_the_collection_date()
    {
        await using var harness = DataRetentionTestHarness.Create();
        var deviceId = Guid.NewGuid();
        await harness.SeedAsync(db =>
        {
            // A device that has been in use every day since. LastSeenAt is irrelevant: the
            // declaration is about how long data may exist, not how long it has been idle.
            SeedGraph(db, deviceId, harness.CutoffOffset(WellPast)).LastSeenAt = harness.Now;
        });

        await harness.RunAsync();

        Assert.Empty(await harness.QueryAsync(db => db.DeviceIdentities.ToListAsync()));
    }

    [Fact]
    public async Task Deleting_a_device_takes_its_whole_graph_and_leaves_no_orphans()
    {
        await using var harness = DataRetentionTestHarness.Create();
        var expiredDevice = Guid.NewGuid();
        var freshDevice = Guid.NewGuid();
        var expiredSession = Guid.NewGuid();

        await harness.SeedAsync(db =>
        {
            // The device is past the limit but everything hanging off it was collected
            // yesterday, so only the ordered graph delete can remove it.
            db.DeviceIdentities.Add(Device(expiredDevice, harness.CutoffOffset(WellPast)));
            db.DeviceIdentities.Add(Device(freshDevice, harness.DaysAgo(1), DeviceTypes.FlutterViewer));
            db.StreamSessions.Add(new StreamSession
            {
                Id = expiredSession,
                SourceDeviceId = expiredDevice,
                Status = SessionStatuses.Active,
                MaxViewers = 4,
                CodeExpiresAt = harness.Now.AddMinutes(10),
                CreatedAt = harness.DaysAgo(1)
            });
            db.SessionParticipants.Add(Participant(expiredSession, expiredDevice, ParticipantRoles.Publisher, harness.DaysAgo(1)));
            db.SessionParticipants.Add(Participant(expiredSession, freshDevice, ParticipantRoles.Viewer, harness.DaysAgo(1)));
            db.SignalingEvents.Add(new SignalingEvent
            {
                Id = Guid.NewGuid(), SessionId = expiredSession, EventType = "offer", CreatedAt = harness.DaysAgo(1)
            });
            db.DevicePairings.Add(new DevicePairing
            {
                Id = Guid.NewGuid(),
                PublisherDeviceId = expiredDevice,
                ViewerDeviceId = freshDevice,
                Status = DevicePairingStatuses.Active,
                CreatedAt = harness.DaysAgo(1)
            });
            db.RelayDeviceSettings.Add(new RelayDeviceSettings
            {
                DeviceId = expiredDevice, RelayMode = RelayModes.Automatic,
                CreatedAt = harness.DaysAgo(1), UpdatedAt = harness.DaysAgo(1)
            });
        });

        await harness.RunAsync();

        Assert.Empty(await harness.QueryAsync(db => db.DeviceIdentities.Where(x => x.Id == expiredDevice).ToListAsync()));
        Assert.Empty(await harness.QueryAsync(db => db.StreamSessions.ToListAsync()));
        Assert.Empty(await harness.QueryAsync(db => db.SessionParticipants.ToListAsync()));
        Assert.Empty(await harness.QueryAsync(db => db.SignalingEvents.ToListAsync()));
        Assert.Empty(await harness.QueryAsync(db => db.DevicePairings.ToListAsync()));
        Assert.Empty(await harness.QueryAsync(db => db.RelayDeviceSettings.ToListAsync()));
        // The unrelated device is untouched, and the session's join code is dropped from Redis.
        Assert.Equal(1, await harness.QueryAsync(db => db.DeviceIdentities.CountAsync()));
        Assert.Contains(expiredSession, harness.CodeStore.Removed);
    }

    [Fact]
    public async Task Rows_left_dangling_by_anything_else_are_swept_as_orphans()
    {
        await using var harness = DataRetentionTestHarness.Create();
        var missingDevice = Guid.NewGuid();
        var missingSession = Guid.NewGuid();

        await harness.SeedAsync(db =>
        {
            // Freshly collected, but their parents are already gone. Ordered deletion is what
            // prevents orphans; this sweep is the net that catches them anyway.
            db.SessionParticipants.Add(Participant(missingSession, missingDevice, ParticipantRoles.Viewer, harness.DaysAgo(1)));
            db.SignalingEvents.Add(new SignalingEvent
            {
                Id = Guid.NewGuid(), SessionId = missingSession, EventType = "answer", CreatedAt = harness.DaysAgo(1)
            });
            db.DevicePairings.Add(new DevicePairing
            {
                Id = Guid.NewGuid(), PublisherDeviceId = missingDevice, ViewerDeviceId = Guid.NewGuid(),
                Status = DevicePairingStatuses.Active, CreatedAt = harness.DaysAgo(1)
            });
            db.RelayDeviceSettings.Add(new RelayDeviceSettings
            {
                DeviceId = missingDevice, RelayMode = RelayModes.Automatic,
                CreatedAt = harness.DaysAgo(1), UpdatedAt = harness.DaysAgo(1)
            });
            db.PairingChallenges.Add(Challenge(missingDevice, harness.Now, harness.Now.AddMinutes(5)));
        });

        await harness.RunAsync();

        Assert.Empty(await harness.QueryAsync(db => db.SessionParticipants.ToListAsync()));
        Assert.Empty(await harness.QueryAsync(db => db.SignalingEvents.ToListAsync()));
        Assert.Empty(await harness.QueryAsync(db => db.DevicePairings.ToListAsync()));
        Assert.Empty(await harness.QueryAsync(db => db.RelayDeviceSettings.ToListAsync()));
        Assert.Empty(await harness.QueryAsync(db => db.PairingChallenges.ToListAsync()));
    }

    [Fact]
    public async Task Spent_pairing_challenges_die_on_their_own_short_clock()
    {
        await using var harness = DataRetentionTestHarness.Create();
        var deviceId = Guid.NewGuid();
        await harness.SeedAsync(db =>
        {
            db.DeviceIdentities.Add(Device(deviceId, harness.DaysAgo(1)));
            // Live: issued a minute ago, still valid.
            db.PairingChallenges.Add(Challenge(deviceId, harness.Now.AddMinutes(-1), harness.Now.AddMinutes(4)));
            // Expired two days ago — a code hash with no remaining purpose.
            db.PairingChallenges.Add(Challenge(deviceId, harness.DaysAgo(2), harness.DaysAgo(2).AddMinutes(5)));
        });

        await harness.RunAsync();

        var remaining = await harness.QueryAsync(db => db.PairingChallenges.ToListAsync());
        Assert.Single(remaining);
        Assert.True(remaining[0].ExpiresAt > harness.Now);
    }

    [Fact]
    public async Task Running_the_same_pass_twice_changes_nothing_the_second_time()
    {
        await using var harness = DataRetentionTestHarness.Create();
        var expired = Guid.NewGuid();
        var live = Guid.NewGuid();
        await harness.SeedAsync(db =>
        {
            SeedGraph(db, expired, harness.CutoffOffset(WellPast));
            SeedGraph(db, live, harness.DaysAgo(1));
        });

        var first = await harness.RunAsync();
        var second = await harness.RunAsync();

        Assert.True(first.TotalDeleted > 0);
        Assert.Equal(0, second.TotalDeleted);
        Assert.True(second.Succeeded);
        // The live graph is intact after both passes.
        Assert.Equal(1, await harness.QueryAsync(db => db.DeviceIdentities.CountAsync()));
        Assert.Equal(1, await harness.QueryAsync(db => db.StreamSessions.CountAsync()));
        Assert.Equal(1, await harness.QueryAsync(db => db.SessionParticipants.CountAsync()));
    }

    [Fact]
    public async Task Concurrent_passes_do_not_fail_each_other()
    {
        await using var harness = DataRetentionTestHarness.Create();
        await harness.SeedAsync(db =>
        {
            for (var i = 0; i < 5; i++) SeedGraph(db, Guid.NewGuid(), harness.CutoffOffset(WellPast));
        });

        var reports = await Task.WhenAll(harness.RunAsync(), harness.RunAsync(), harness.RunAsync());

        Assert.All(reports, report => Assert.True(report.Succeeded));
        Assert.Empty(await harness.QueryAsync(db => db.DeviceIdentities.ToListAsync()));
        Assert.Empty(await harness.QueryAsync(db => db.StreamSessions.ToListAsync()));
    }

    [Fact]
    public async Task A_failing_sweep_neither_aborts_the_pass_nor_blocks_the_next_one()
    {
        await using var harness = DataRetentionTestHarness.Create(options => options.MaxAttemptsPerEntity = 1);
        await harness.SeedAsync(db => SeedGraph(db, Guid.NewGuid(), harness.CutoffOffset(WellPast)));
        harness.CodeStore.FailOnRemove = true;

        var failed = await harness.RunAsync();

        Assert.False(failed.Succeeded);
        Assert.Contains("stream_session", failed.FailedEntities);
        // Everything swept after the failing group still went, and no success was recorded.
        Assert.Empty(await harness.QueryAsync(db => db.DevicePairings.ToListAsync()));
        Assert.Null(harness.State.LastSuccessAt);

        harness.CodeStore.FailOnRemove = false;
        var recovered = await harness.RunAsync();

        Assert.True(recovered.Succeeded);
        Assert.Equal(harness.Now, harness.State.LastSuccessAt);
        Assert.Empty(await harness.QueryAsync(db => db.DeviceIdentities.ToListAsync()));
    }

    [Fact]
    public async Task A_backlog_larger_than_one_batch_is_cleared_over_successive_passes()
    {
        await using var harness = DataRetentionTestHarness.Create(options => options.BatchSize = 2);
        await harness.SeedAsync(db =>
        {
            for (var i = 0; i < 5; i++) db.DeviceIdentities.Add(Device(Guid.NewGuid(), harness.CutoffOffset(WellPast)));
        });

        Assert.Equal(2, (await harness.RunAsync()).Deleted["device_identity"]);
        for (var pass = 0; pass < 3; pass++) await harness.RunAsync();

        Assert.Empty(await harness.QueryAsync(db => db.DeviceIdentities.ToListAsync()));
    }

    [Fact]
    public async Task Cleanup_logs_counts_and_never_the_data_it_erased()
    {
        await using var harness = DataRetentionTestHarness.Create();
        var deviceId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        const string codeHash = "F00DBABE0000000000000000000000000000000000000000000000000000CAFE";
        await harness.SeedAsync(db =>
        {
            SeedGraph(db, deviceId, harness.CutoffOffset(WellPast), sessionId, codeHash)
                .CredentialSecretHash = codeHash;
        });

        await harness.RunAsync();

        var logged = string.Join('\n', harness.Logs.Messages);
        Assert.Contains("Data retention pass removed", logged);
        // A log line naming what was deleted would keep the identifier alive after the row is
        // gone, which is exactly what the retention promise rules out.
        Assert.DoesNotContain(deviceId.ToString(), logged);
        Assert.DoesNotContain(sessionId.ToString(), logged);
        Assert.DoesNotContain(codeHash, logged, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Seeds one device plus every row that hangs off it, all collected at the same instant.</summary>
    private static DeviceIdentity SeedGraph(
        AppDbContext db, Guid deviceId, DateTimeOffset collectedAt,
        Guid? sessionId = null, string codeHash = "AA")
    {
        var session = sessionId ?? Guid.NewGuid();
        var device = Device(deviceId, collectedAt);
        db.DeviceIdentities.Add(device);
        db.StreamSessions.Add(new StreamSession
        {
            Id = session,
            SourceDeviceId = deviceId,
            Status = SessionStatuses.Ended,
            MaxViewers = 4,
            CodeExpiresAt = collectedAt.AddMinutes(10),
            CreatedAt = collectedAt
        });
        db.SessionParticipants.Add(Participant(session, deviceId, ParticipantRoles.Publisher, collectedAt));
        db.SignalingEvents.Add(new SignalingEvent
        {
            Id = Guid.NewGuid(), SessionId = session, EventType = "offer", CreatedAt = collectedAt
        });
        db.DevicePairings.Add(new DevicePairing
        {
            Id = Guid.NewGuid(),
            PublisherDeviceId = deviceId,
            ViewerDeviceId = deviceId,
            Status = DevicePairingStatuses.Active,
            CreatedAt = collectedAt
        });
        db.PairingChallenges.Add(Challenge(deviceId, collectedAt, collectedAt.AddMinutes(5), codeHash));
        db.RelayDeviceSettings.Add(new RelayDeviceSettings
        {
            DeviceId = deviceId, RelayMode = RelayModes.Automatic,
            CreatedAt = collectedAt, UpdatedAt = collectedAt
        });
        return device;
    }

    private static DeviceIdentity Device(Guid id, DateTimeOffset createdAt,
        string deviceType = DeviceTypes.WindowsPublisher) => new()
    {
        Id = id,
        Name = "Retention fixture",
        DeviceType = deviceType,
        Platform = deviceType == DeviceTypes.WindowsPublisher
            ? DevicePlatforms.Windows
            : DevicePlatforms.Android,
        CredentialSecretHash = "AA",
        CredentialVersion = 1,
        Status = DeviceIdentityStatuses.Active,
        CreatedAt = createdAt
    };

    private static SessionParticipant Participant(Guid sessionId, Guid deviceId, string role,
        DateTimeOffset joinedAt) => new()
    {
        Id = Guid.NewGuid(),
        SessionId = sessionId,
        DeviceId = deviceId,
        Role = role,
        Status = ParticipantStatuses.Disconnected,
        JoinedAt = joinedAt,
        LeftAt = joinedAt
    };

    private static PairingChallenge Challenge(Guid publisherDeviceId, DateTimeOffset createdAt,
        DateTimeOffset expiresAt, string codeHash = "AA") => new()
    {
        Id = Guid.NewGuid(),
        PublisherDeviceId = publisherDeviceId,
        CodeHash = codeHash,
        ExpiresAt = expiresAt,
        MaxAttempts = 5,
        AttemptCount = 0,
        CreatedAt = createdAt
    };
}
