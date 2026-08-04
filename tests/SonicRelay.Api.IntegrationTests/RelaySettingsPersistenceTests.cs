using Microsoft.EntityFrameworkCore;
using SonicRelay.Domain.RelaySettings;
using SonicRelay.Infrastructure.Persistence;
using Xunit;

namespace SonicRelay.Api.IntegrationTests;

public sealed class RelaySettingsPersistenceTests
{
    [Fact]
    public async Task RoundTrips_a_singleton_row()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"relay-settings-persistence-{Guid.NewGuid()}")
            .Options;
        await using var db = new AppDbContext(options);

        db.RelaySettings.Add(new RelaySettings
        {
            Id = RelaySettings.SingletonId,
            RelayMode = RelayModes.ForceRelay,
            TurnUris = ["turn:relay.example.com:3478?transport=udp"],
            TurnStaticAuthSecret = "shared-secret",
            UpdatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();

        var reloaded = await db.RelaySettings.SingleAsync(x => x.Id == RelaySettings.SingletonId);
        Assert.Equal(RelayModes.ForceRelay, reloaded.RelayMode);
        Assert.Equal(["turn:relay.example.com:3478?transport=udp"], reloaded.TurnUris);
        Assert.Equal("shared-secret", reloaded.TurnStaticAuthSecret);
    }

    [Theory]
    [InlineData(RelayModes.Automatic, true)]
    [InlineData(RelayModes.ForceRelay, true)]
    [InlineData(RelayModes.DisableFallback, true)]
    [InlineData("bogus", false)]
    [InlineData(null, false)]
    public void IsValid_accepts_only_the_three_known_modes(string? mode, bool expected) =>
        Assert.Equal(expected, RelayModes.IsValid(mode));
}
