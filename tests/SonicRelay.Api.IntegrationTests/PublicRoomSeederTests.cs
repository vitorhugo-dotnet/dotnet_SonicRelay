using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SonicRelay.Api.Services;
using SonicRelay.Domain.DeviceIdentities;
using SonicRelay.Domain.Sessions;
using SonicRelay.Infrastructure.Persistence;
using Xunit;

namespace SonicRelay.Api.IntegrationTests;

public sealed class PublicRoomSeederTests
{
    private static AppDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"public-room-seeder-{Guid.NewGuid()}").Options);

    [Fact]
    public async Task EnsureSeededAsync_creates_device_session_and_publisher_participant()
    {
        await using var db = CreateDb();

        var session = await new PublicRoomSeeder().EnsureSeededAsync(db, TimeProvider.System, CancellationToken.None);

        Assert.Equal(PublicRoomSeeder.PublicSessionId, session.Id);
        Assert.Equal(PublicRoomSeeder.VirtualPublisherDeviceId, session.SourceDeviceId);

        var device = await db.DeviceIdentities.SingleAsync(x => x.Id == PublicRoomSeeder.VirtualPublisherDeviceId);
        Assert.Equal(DeviceIdentityStatuses.Active, device.Status);

        var participant = await db.SessionParticipants.SingleAsync(x =>
            x.SessionId == session.Id && x.DeviceId == device.Id);
        Assert.Equal(ParticipantRoles.Publisher, participant.Role);
    }

    [Fact]
    public async Task EnsureSeededAsync_is_idempotent_across_repeated_calls()
    {
        await using var db = CreateDb();
        var seeder = new PublicRoomSeeder();

        await seeder.EnsureSeededAsync(db, TimeProvider.System, CancellationToken.None);
        var second = await seeder.EnsureSeededAsync(db, TimeProvider.System, CancellationToken.None);

        Assert.Equal(1, await db.DeviceIdentities.CountAsync(x => x.Id == PublicRoomSeeder.VirtualPublisherDeviceId));
        Assert.Equal(1, await db.StreamSessions.CountAsync(x => x.Id == PublicRoomSeeder.PublicSessionId));
        Assert.Equal(1, await db.SessionParticipants.CountAsync(x =>
            x.SessionId == PublicRoomSeeder.PublicSessionId && x.Role == ParticipantRoles.Publisher));
        Assert.Equal(PublicRoomSeeder.PublicSessionId, second.Id);
    }

    [Fact]
    public async Task EnsureSeededAsync_reactivates_a_revoked_device()
    {
        await using var db = CreateDb();
        var seeder = new PublicRoomSeeder();
        await seeder.EnsureSeededAsync(db, TimeProvider.System, CancellationToken.None);
        var device = await db.DeviceIdentities.SingleAsync(x => x.Id == PublicRoomSeeder.VirtualPublisherDeviceId);
        device.Status = DeviceIdentityStatuses.Revoked;
        await db.SaveChangesAsync();

        await seeder.EnsureSeededAsync(db, TimeProvider.System, CancellationToken.None);

        var reloaded = await db.DeviceIdentities.SingleAsync(x => x.Id == PublicRoomSeeder.VirtualPublisherDeviceId);
        Assert.Equal(DeviceIdentityStatuses.Active, reloaded.Status);
    }

    /// <summary>
    /// The public session's id is a fixed GUID, so nothing ever re-creates it: once
    /// SessionCleanupService (or an expiry sweep) marks it terminal, the signaling upgrade rejects
    /// every connection with 410 and the room is dead forever unless the seeder revives the row.
    /// </summary>
    [Theory]
    [InlineData(SessionStatuses.Ended)]
    [InlineData(SessionStatuses.Expired)]
    public async Task EnsureSeededAsync_revives_a_terminal_session(string terminalStatus)
    {
        await using var db = CreateDb();
        var seeder = new PublicRoomSeeder();
        await seeder.EnsureSeededAsync(db, TimeProvider.System, CancellationToken.None);
        var session = await db.StreamSessions.SingleAsync(x => x.Id == PublicRoomSeeder.PublicSessionId);
        session.Status = terminalStatus;
        session.EndedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();

        var revived = await seeder.EnsureSeededAsync(db, TimeProvider.System, CancellationToken.None);

        Assert.Equal(SessionStatuses.Active, revived.Status);
        Assert.Null(revived.EndedAt);
        Assert.NotNull(revived.StartedAt);
        var reloaded = await db.StreamSessions.SingleAsync(x => x.Id == PublicRoomSeeder.PublicSessionId);
        Assert.Equal(SessionStatuses.Active, reloaded.Status);
        Assert.Null(reloaded.EndedAt);
    }

    [Fact]
    public async Task IssuePublisherTokenAsync_mints_a_token_for_the_seeded_device()
    {
        await using var db = CreateDb();
        var seeder = new PublicRoomSeeder();
        await seeder.EnsureSeededAsync(db, TimeProvider.System, CancellationToken.None);
        var options = Options.Create(new DeviceIdentityOptions
        {
            TokenSigningKey = "integration-test-device-token-signing-key-32bytes-min",
            Issuer = "sonicrelay-tests",
            Audience = "sonicrelay-tests",
            AccessTokenMinutes = 60
        });
        var credentials = new DeviceCredentialService(options, TimeProvider.System);

        var (token, expiresAt) = await seeder.IssuePublisherTokenAsync(
            db, credentials, TimeProvider.System, CancellationToken.None);

        Assert.False(string.IsNullOrWhiteSpace(token));
        Assert.True(expiresAt > DateTimeOffset.UtcNow);
    }
}
