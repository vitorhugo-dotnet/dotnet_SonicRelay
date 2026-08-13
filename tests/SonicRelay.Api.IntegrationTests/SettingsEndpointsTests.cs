using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using SonicRelay.Api.Contracts;
using SonicRelay.Domain.Devices;
using Xunit;

namespace SonicRelay.Api.IntegrationTests;

public sealed class SettingsEndpointsTests : IClassFixture<SonicRelayApiFactory>
{
    private readonly SonicRelayApiFactory _factory;

    public SettingsEndpointsTests(SonicRelayApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Relay_settings_require_authentication()
    {
        var response = await _factory.CreateClient().GetAsync("/api/settings/relay");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Relay_settings_default_to_automatic_with_no_custom_relay()
    {
        var session = await DeviceIdentityTestHelper.BootstrapAndAuthorizeAsync(
            _factory.CreateClient(), DeviceTypes.WindowsPublisher, DevicePlatforms.Windows);

        var settings = await session.Client.GetFromJsonAsync<JsonElement>("/api/settings/relay");

        Assert.Equal("automatic", settings.GetProperty("relayMode").GetString());
        Assert.Equal(0, settings.GetProperty("turnUris").GetArrayLength());
        Assert.False(settings.GetProperty("hasTurnCredential").GetBoolean());
    }

    [Fact]
    public async Task Relay_settings_round_trip_without_echoing_the_credential()
    {
        var session = await DeviceIdentityTestHelper.BootstrapAndAuthorizeAsync(
            _factory.CreateClient(), DeviceTypes.WindowsPublisher, DevicePlatforms.Windows);

        var update = await session.Client.PutAsJsonAsync("/api/settings/relay", new
        {
            relayMode = "forceRelay",
            turnUris = new[] { "turns:my-relay.example.net:5349" },
            turnUsername = "me",
            turnCredential = "super-secret"
        });
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);

        var settings = await session.Client.GetFromJsonAsync<JsonElement>("/api/settings/relay");
        Assert.Equal("forceRelay", settings.GetProperty("relayMode").GetString());
        Assert.Equal("turns:my-relay.example.net:5349", settings.GetProperty("turnUris")[0].GetString());
        Assert.Equal("me", settings.GetProperty("turnUsername").GetString());
        Assert.True(settings.GetProperty("hasTurnCredential").GetBoolean());
        Assert.DoesNotContain("super-secret", await (await session.Client.GetAsync("/api/settings/relay"))
            .Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("sideways")]
    [InlineData("")]
    public async Task Relay_settings_reject_an_unknown_relay_mode(string mode)
    {
        var session = await DeviceIdentityTestHelper.BootstrapAndAuthorizeAsync(
            _factory.CreateClient(), DeviceTypes.WindowsPublisher, DevicePlatforms.Windows);

        var update = await session.Client.PutAsJsonAsync("/api/settings/relay", new { relayMode = mode });

        Assert.Equal(HttpStatusCode.BadRequest, update.StatusCode);
    }

    [Fact]
    public async Task Relay_settings_reject_non_turn_uris()
    {
        var session = await DeviceIdentityTestHelper.BootstrapAndAuthorizeAsync(
            _factory.CreateClient(), DeviceTypes.WindowsPublisher, DevicePlatforms.Windows);

        var update = await session.Client.PutAsJsonAsync("/api/settings/relay", new
        {
            turnUris = new[] { "https://not-a-turn-server.example.com" }
        });

        Assert.Equal(HttpStatusCode.BadRequest, update.StatusCode);
    }

    [Fact]
    public async Task Relay_settings_written_on_one_device_are_visible_on_its_paired_peer()
    {
        var publisher = await DeviceIdentityTestHelper.BootstrapAndAuthorizeAsync(
            _factory.CreateClient(), DeviceTypes.WindowsPublisher, DevicePlatforms.Windows, "Desktop");
        var viewer = await DeviceIdentityTestHelper.BootstrapAndAuthorizeAsync(
            _factory.CreateClient(), DeviceTypes.FlutterViewer, DevicePlatforms.Android, "Phone");
        await PairAsync(publisher, viewer);

        // The phone points at its own coturn…
        var update = await viewer.Client.PutAsJsonAsync("/api/settings/relay", new
        {
            turnUris = new[] { "turn:my-own.example.net:3478" }
        });
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);

        // …and the paired desktop sees the same effective settings.
        var desktopSettings = await publisher.Client.GetFromJsonAsync<JsonElement>("/api/settings/relay");
        Assert.Equal("turn:my-own.example.net:3478", desktopSettings.GetProperty("turnUris")[0].GetString());

        // The latest write wins in the other direction too.
        var revert = await publisher.Client.PutAsJsonAsync("/api/settings/relay", new
        {
            turnUris = Array.Empty<string>()
        });
        Assert.Equal(HttpStatusCode.OK, revert.StatusCode);
        var phoneSettings = await viewer.Client.GetFromJsonAsync<JsonElement>("/api/settings/relay");
        Assert.Equal(0, phoneSettings.GetProperty("turnUris").GetArrayLength());
    }

    [Fact]
    public async Task Relay_settings_do_not_leak_to_unpaired_devices()
    {
        var deviceA = await DeviceIdentityTestHelper.BootstrapAndAuthorizeAsync(
            _factory.CreateClient(), DeviceTypes.WindowsPublisher, DevicePlatforms.Windows, "A");
        var deviceB = await DeviceIdentityTestHelper.BootstrapAndAuthorizeAsync(
            _factory.CreateClient(), DeviceTypes.WindowsPublisher, DevicePlatforms.Windows, "B");

        var update = await deviceA.Client.PutAsJsonAsync("/api/settings/relay", new
        {
            turnUris = new[] { "turn:private.example.net:3478" }
        });
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);

        var otherSettings = await deviceB.Client.GetFromJsonAsync<JsonElement>("/api/settings/relay");
        Assert.Equal(0, otherSettings.GetProperty("turnUris").GetArrayLength());
    }

    private async Task PairAsync(DeviceIdentitySession publisher, DeviceIdentitySession viewer)
    {
        var challengeResponse = await publisher.Client.PostAsync("/api/pairings/challenges", null);
        challengeResponse.EnsureSuccessStatusCode();
        var challenge = await challengeResponse.Content.ReadFromJsonAsync<CreateChallengeResponse>();
        var completeResponse = await viewer.Client.PostAsJsonAsync("/api/pairings/complete",
            new CompletePairingRequest(challenge!.ChallengeId, challenge.Code));
        completeResponse.EnsureSuccessStatusCode();
    }
}
