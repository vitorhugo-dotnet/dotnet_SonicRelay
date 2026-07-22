using Microsoft.EntityFrameworkCore;
using SonicRelay.Domain.DeviceIdentities;
using SonicRelay.Domain.Devices;
using SonicRelay.Infrastructure.Persistence;
using Xunit;

namespace SonicRelay.Api.IntegrationTests;

public sealed class DeviceIdentityPersistenceTests
{
    [Fact]
    public async Task RoundTrips_DeviceIdentity_PairingChallenge_And_DevicePairing()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"device-identity-persistence-{Guid.NewGuid()}")
            .Options;
        await using var db = new AppDbContext(options);

        var publisher = new DeviceIdentity
        {
            Id = Guid.NewGuid(),
            Name = "Living Room PC",
            DeviceType = DeviceTypes.WindowsPublisher,
            Platform = DevicePlatforms.Windows,
            CredentialSecretHash = "hash",
            CredentialVersion = 1,
            Status = DeviceIdentityStatuses.Active,
            CreatedAt = DateTimeOffset.UtcNow
        };
        var viewer = new DeviceIdentity
        {
            Id = Guid.NewGuid(),
            Name = "Phone",
            DeviceType = DeviceTypes.FlutterViewer,
            Platform = DevicePlatforms.Android,
            CredentialSecretHash = "hash2",
            CredentialVersion = 1,
            Status = DeviceIdentityStatuses.Active,
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.DeviceIdentities.AddRange(publisher, viewer);

        var challenge = new PairingChallenge
        {
            Id = Guid.NewGuid(),
            PublisherDeviceId = publisher.Id,
            CodeHash = "code-hash",
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
            MaxAttempts = 5,
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.PairingChallenges.Add(challenge);

        var pairing = new DevicePairing
        {
            Id = Guid.NewGuid(),
            PublisherDeviceId = publisher.Id,
            ViewerDeviceId = viewer.Id,
            Status = DevicePairingStatuses.Active,
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.DevicePairings.Add(pairing);

        await db.SaveChangesAsync();

        Assert.Equal(2, await db.DeviceIdentities.CountAsync());
        Assert.Equal(1, await db.PairingChallenges.CountAsync());
        Assert.Equal(1, await db.DevicePairings.CountAsync());
    }
}
