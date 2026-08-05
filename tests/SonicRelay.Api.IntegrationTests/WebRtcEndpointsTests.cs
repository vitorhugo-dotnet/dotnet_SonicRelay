using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SonicRelay.Domain.DeviceIdentities;
using SonicRelay.Domain.Devices;
using Xunit;

namespace SonicRelay.Api.IntegrationTests;

public sealed class WebRtcEndpointsTests
{
    [Fact]
    public async Task Ice_servers_derives_turn_from_configuration_without_any_database_row()
    {
        await using var factory = new SonicRelayApiFactory(new Dictionary<string, string?>
        {
            ["Turn:StaticAuthSecret"] = "plan-test-secret",
            ["Turn:TurnUris:0"] = "turn:relay.example.com:3478?transport=udp"
        });
        var client = factory.CreateClient();
        await DeviceIdentityTestHelper.BootstrapAndAuthorizeAsync(
            client, DeviceTypes.FlutterViewer, DevicePlatforms.Android);

        var response = await client.GetAsync("/api/webrtc/ice-servers");
        var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var turn = body.RootElement.GetProperty("iceServers").EnumerateArray()
            .Single(item => item.GetProperty("urls")[0].GetString()!.StartsWith("turn:", StringComparison.Ordinal));
        Assert.Equal("turn:relay.example.com:3478?transport=udp", turn.GetProperty("urls")[0].GetString());
        Assert.False(string.IsNullOrWhiteSpace(turn.GetProperty("credential").GetString()));
    }

    [Fact]
    public async Task Relay_settings_endpoint_is_gone()
    {
        await using var factory = new SonicRelayApiFactory();
        var client = factory.CreateClient();
        await DeviceIdentityTestHelper.BootstrapAndAuthorizeAsync(
            client, DeviceTypes.WindowsPublisher, DevicePlatforms.Windows);

        var response = await client.GetAsync("/api/settings/relay");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
