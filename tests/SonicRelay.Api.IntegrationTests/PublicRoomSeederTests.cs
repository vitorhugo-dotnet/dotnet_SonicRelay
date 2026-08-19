using Microsoft.EntityFrameworkCore;
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
}
