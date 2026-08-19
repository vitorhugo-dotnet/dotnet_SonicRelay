using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SonicRelay.Api.Services;
using Xunit;

namespace SonicRelay.Api.IntegrationTests;

public sealed class PublicRoomPublisherServiceTests
{
    [Fact]
    public async Task ExecuteAsync_does_nothing_when_disabled()
    {
        var factory = new SonicRelayApiFactory(new Dictionary<string, string?>
        {
            ["PublicRoom:Enabled"] = "false"
        });
        _ = factory.CreateClient(); // forces host startup, including hosted services

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SonicRelay.Infrastructure.Persistence.AppDbContext>();
        // A disabled publisher must never seed the session, matching Task 5's endpoint test.
        Assert.False(await db.StreamSessions.AnyAsync(x => x.Id == PublicRoomSeeder.PublicSessionId));
        await factory.DisposeAsync();
    }

    [Fact]
    public async Task ExecuteAsync_seeds_the_session_when_enabled_even_with_no_tracks()
    {
        var emptyTracksDir = Path.Combine(Path.GetTempPath(), "sonicrelay-public-room-empty-" + Guid.NewGuid());
        Directory.CreateDirectory(emptyTracksDir);
        var factory = new SonicRelayApiFactory(new Dictionary<string, string?>
        {
            ["PublicRoom:Enabled"] = "true",
            ["PublicRoom:TracksPath"] = emptyTracksDir,
        });
        _ = factory.CreateClient();

        // Give the hosted service's startup a moment to run its seeding step.
        await Task.Delay(TimeSpan.FromSeconds(1));

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SonicRelay.Infrastructure.Persistence.AppDbContext>();
        Assert.True(await db.StreamSessions.AnyAsync(x => x.Id == PublicRoomSeeder.PublicSessionId));
        await factory.DisposeAsync();
        Directory.Delete(emptyTracksDir, recursive: true);
    }
}
