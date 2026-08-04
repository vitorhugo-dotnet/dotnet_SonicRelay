using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SonicRelay.Api.Contracts;
using SonicRelay.Domain.Devices;
using SonicRelay.Domain.RelaySettings;
using SonicRelay.Infrastructure.Persistence;
using Xunit;

namespace SonicRelay.Api.IntegrationTests;

// Note: RelaySettings is a global singleton row (Task 1), not a per-test-unique resource like a
// device or session code, so the SonicRelayApiFactory shared via IClassFixture across this
// class's tests would otherwise leak mutations between test methods (order-dependent flakiness).
// IAsyncLifetime.InitializeAsync clears the row before each test for isolation; it runs once per
// test-method instance (xUnit constructs a new test class instance per method) while still
// reusing the same factory/database the brief's tests expect.
public sealed class SettingsEndpointsTests : IClassFixture<SonicRelayApiFactory>, IAsyncLifetime
{
    private readonly SonicRelayApiFactory _factory;

    public SettingsEndpointsTests(SonicRelayApiFactory factory) => _factory = factory;

    public async Task InitializeAsync()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var existing = await db.RelaySettings.Where(x => x.Id == RelaySettings.SingletonId).ToListAsync();
        if (existing.Count > 0)
        {
            db.RelaySettings.RemoveRange(existing);
            await db.SaveChangesAsync();
        }
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Get_relay_settings_requires_authentication()
    {
        var response = await _factory.CreateClient().GetAsync("/api/settings/relay");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Get_relay_settings_defaults_to_automatic_with_no_override()
    {
        var client = await BootstrapAsync();

        var response = await client.GetAsync("/api/settings/relay");

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<RelaySettingsResponse>();
        Assert.Equal("automatic", body!.RelayMode);
        Assert.Empty(body.TurnUris);
        Assert.False(body.HasCustomTurnSecret);
    }

    [Fact]
    public async Task Put_relay_settings_rejects_an_unknown_mode()
    {
        var client = await BootstrapAsync();

        var response = await client.PutAsJsonAsync("/api/settings/relay",
            new UpdateRelaySettingsRequest("bogus", null, null));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Put_then_get_round_trips_relay_mode_without_echoing_the_secret()
    {
        var client = await BootstrapAsync();

        var putResponse = await client.PutAsJsonAsync("/api/settings/relay",
            new UpdateRelaySettingsRequest("disableFallback", ["turn:mine.example.com:3478"], "my-secret"));
        putResponse.EnsureSuccessStatusCode();
        var putBody = await putResponse.Content.ReadFromJsonAsync<RelaySettingsResponse>();
        Assert.Equal("disableFallback", putBody!.RelayMode);
        Assert.True(putBody.HasCustomTurnSecret);

        var getResponse = await client.GetAsync("/api/settings/relay");
        var getBody = await getResponse.Content.ReadFromJsonAsync<RelaySettingsResponse>();
        Assert.Equal("disableFallback", getBody!.RelayMode);
        Assert.Equal(["turn:mine.example.com:3478"], getBody.TurnUris);
        Assert.True(getBody.HasCustomTurnSecret);
        Assert.DoesNotContain("my-secret", await getResponse.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Put_with_only_relay_mode_leaves_previously_stored_turn_uris_untouched()
    {
        var client = await BootstrapAsync();
        await client.PutAsJsonAsync("/api/settings/relay",
            new UpdateRelaySettingsRequest("automatic", ["turn:kept.example.com:3478"], null));

        var response = await client.PutAsJsonAsync("/api/settings/relay",
            new UpdateRelaySettingsRequest("forceRelay", null, null));

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<RelaySettingsResponse>();
        Assert.Equal("forceRelay", body!.RelayMode);
        Assert.Equal(["turn:kept.example.com:3478"], body.TurnUris);
    }

    private async Task<HttpClient> BootstrapAsync()
    {
        var client = _factory.CreateClient();
        await DeviceIdentityTestHelper.BootstrapAndAuthorizeAsync(
            client, DeviceTypes.WindowsPublisher, DevicePlatforms.Windows);
        return client;
    }
}
