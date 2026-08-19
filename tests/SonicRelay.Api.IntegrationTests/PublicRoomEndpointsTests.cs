using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SonicRelay.Api.Contracts;
using SonicRelay.Api.Services;
using SonicRelay.Domain.DeviceIdentities;
using SonicRelay.Domain.Devices;
using SonicRelay.Infrastructure.Persistence;
using Xunit;

namespace SonicRelay.Api.IntegrationTests;

public sealed class PublicRoomEndpointsTests : IClassFixture<SonicRelayApiFactory>
{
    private readonly SonicRelayApiFactory _disabledFactory = new();

    [Fact]
    public async Task Returns_disabled_and_touches_nothing_when_the_feature_is_off()
    {
        var client = _disabledFactory.CreateClient();
        var session = await DeviceIdentityTestHelper.BootstrapAndAuthorizeAsync(
            client, DeviceTypes.FlutterViewer, DevicePlatforms.Android);

        var response = await client.GetAsync("/api/public-room");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<PublicRoomResponse>();
        Assert.False(body!.Enabled);
        Assert.Null(body.SessionId);

        using var scope = _disabledFactory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await db.StreamSessions.AnyAsync(x => x.Id == PublicRoomSeeder.PublicSessionId));
    }

    [Fact]
    public async Task Requires_authentication()
    {
        var response = await _disabledFactory.CreateClient().GetAsync("/api/public-room");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Returns_session_and_auto_pairs_when_enabled()
    {
        await using var factory = new SonicRelayApiFactory(new Dictionary<string, string?>
        {
            ["PublicRoom:Enabled"] = "true",
            ["PublicRoom:MaxViewers"] = "20",
        });
        var client = factory.CreateClient();
        var session = await DeviceIdentityTestHelper.BootstrapAndAuthorizeAsync(
            client, DeviceTypes.FlutterViewer, DevicePlatforms.Android);

        var response = await client.GetAsync("/api/public-room");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<PublicRoomResponse>();
        Assert.True(body!.Enabled);
        Assert.Equal(PublicRoomSeeder.PublicSessionId, body.SessionId);
        Assert.Equal(20, body.MaxViewers);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var pairing = await db.DevicePairings.SingleAsync(x =>
            x.PublisherDeviceId == PublicRoomSeeder.VirtualPublisherDeviceId && x.ViewerDeviceId == session.DeviceId);
        Assert.Equal(DevicePairingStatuses.Active, pairing.Status);
    }

    [Fact]
    public async Task Join_after_auto_pair_succeeds()
    {
        await using var factory = new SonicRelayApiFactory(new Dictionary<string, string?>
        {
            ["PublicRoom:Enabled"] = "true",
            ["PublicRoom:MaxViewers"] = "20",
        });
        var client = factory.CreateClient();
        await DeviceIdentityTestHelper.BootstrapAndAuthorizeAsync(
            client, DeviceTypes.FlutterViewer, DevicePlatforms.Android);

        var publicRoomResponse = await client.GetAsync("/api/public-room");
        var publicRoom = await publicRoomResponse.Content.ReadFromJsonAsync<PublicRoomResponse>();

        var joinResponse = await client.PostAsync($"/api/sessions/{publicRoom!.SessionId}/join", null);

        var body = await joinResponse.Content.ReadAsStringAsync();
        Assert.True(joinResponse.IsSuccessStatusCode, $"Status {joinResponse.StatusCode}: {body}");
    }

    // Regression coverage for issue #49: a viewer must be able to join the public radio room
    // even when it never called GET /api/public-room (so no auto-pair ever ran) and has no
    // DevicePairing row at all — the public room is meant to be pairing-free, and joining must
    // not silently depend on that GET's side effect having already run.
    [Fact]
    public async Task Join_succeeds_with_no_pairing_at_all()
    {
        await using var factory = new SonicRelayApiFactory(new Dictionary<string, string?>
        {
            ["PublicRoom:Enabled"] = "true",
        });

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var seeder = scope.ServiceProvider.GetRequiredService<PublicRoomSeeder>();
            var time = scope.ServiceProvider.GetRequiredService<TimeProvider>();
            await seeder.EnsureSeededAsync(db, time, CancellationToken.None);
        }

        var client = factory.CreateClient();
        await DeviceIdentityTestHelper.BootstrapAndAuthorizeAsync(
            client, DeviceTypes.FlutterViewer, DevicePlatforms.Android);

        var joinResponse = await client.PostAsync($"/api/sessions/{PublicRoomSeeder.PublicSessionId}/join", null);

        var body = await joinResponse.Content.ReadAsStringAsync();
        Assert.True(joinResponse.IsSuccessStatusCode, $"Status {joinResponse.StatusCode}: {body}");

        using var assertScope = factory.Services.CreateScope();
        var assertDb = assertScope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await assertDb.DevicePairings.AnyAsync(x =>
            x.PublisherDeviceId == PublicRoomSeeder.VirtualPublisherDeviceId));
    }

    [Fact]
    public async Task Second_call_reuses_the_same_pairing_instead_of_duplicating_it()
    {
        await using var factory = new SonicRelayApiFactory(new Dictionary<string, string?>
        {
            ["PublicRoom:Enabled"] = "true",
        });
        var client = factory.CreateClient();
        var session = await DeviceIdentityTestHelper.BootstrapAndAuthorizeAsync(
            client, DeviceTypes.FlutterViewer, DevicePlatforms.Android);

        await client.GetAsync("/api/public-room");
        await client.GetAsync("/api/public-room");

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(1, await db.DevicePairings.CountAsync(x =>
            x.PublisherDeviceId == PublicRoomSeeder.VirtualPublisherDeviceId && x.ViewerDeviceId == session.DeviceId));
    }
}
