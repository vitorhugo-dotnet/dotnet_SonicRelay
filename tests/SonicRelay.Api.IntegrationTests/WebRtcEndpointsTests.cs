using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SonicRelay.Domain.Devices;
using Xunit;

namespace SonicRelay.Api.IntegrationTests;

public sealed class WebRtcEndpointsTests : IClassFixture<SonicRelayApiFactory>
{
    private readonly SonicRelayApiFactory _factory;

    public WebRtcEndpointsTests(SonicRelayApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Ice_servers_requires_authentication()
    {
        var response = await _factory.CreateClient().GetAsync("/api/webrtc/ice-servers");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Ice_servers_returns_stun_only_when_turn_is_not_configured()
    {
        var (client, _) = await BootstrapAsync(_factory);

        var body = await GetIceServersAsync(client);

        var servers = body.GetProperty("iceServers").EnumerateArray().ToList();
        var entry = Assert.Single(servers);
        Assert.Equal("stun:stun.l.google.com:19302", entry.GetProperty("urls")[0].GetString());
        Assert.False(TryGetNonNull(entry, "username", out _));
        Assert.False(TryGetNonNull(entry, "credential", out _));
    }

    [Fact]
    public async Task Ice_servers_returns_turn_entry_with_coturn_rest_credentials()
    {
        const string secret = "integration-turn-secret";
        await using var factory = new SonicRelayApiFactory(new Dictionary<string, string?>
        {
            ["Turn:StaticAuthSecret"] = secret,
            ["Turn:TurnUris:0"] = "turn:relay.example.com:3478?transport=udp",
            ["Turn:TurnUris:1"] = "turns:relay.example.com:5349?transport=tcp",
            ["Turn:CredentialTtlSeconds"] = "600"
        });
        var (client, deviceId) = await BootstrapAsync(factory);
        var before = DateTimeOffset.UtcNow;

        var body = await GetIceServersAsync(client);

        Assert.Equal(600, body.GetProperty("ttlSeconds").GetInt32());
        var servers = body.GetProperty("iceServers").EnumerateArray().ToList();
        Assert.Equal(2, servers.Count);
        var turn = servers.Single(item => item.GetProperty("urls")[0].GetString()!.StartsWith("turn:", StringComparison.Ordinal));
        Assert.Equal("turns:relay.example.com:5349?transport=tcp", turn.GetProperty("urls")[1].GetString());

        var username = turn.GetProperty("username").GetString()!;
        var parts = username.Split(':', 2);
        var expiry = DateTimeOffset.FromUnixTimeSeconds(long.Parse(parts[0]));
        Assert.Equal(deviceId.ToString("D"), parts[1]);
        Assert.InRange(expiry, before.AddSeconds(600).AddSeconds(-30), before.AddSeconds(600).AddSeconds(30));

        var expected = Convert.ToBase64String(HMACSHA1.HashData(
            Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(username)));
        Assert.Equal(expected, turn.GetProperty("credential").GetString());
    }

    [Fact]
    public async Task Ice_servers_accepts_flat_environment_style_configuration()
    {
        await using var factory = new SonicRelayApiFactory(new Dictionary<string, string?>
        {
            ["TURN_STATIC_AUTH_SECRET"] = "flat-env-secret",
            ["TURN_URIS"] = "turn:relay.example.com:3478?transport=udp, turn:relay.example.com:3478?transport=tcp",
            ["TURN_CREDENTIAL_TTL_SECONDS"] = "1200"
        });
        var (client, _) = await BootstrapAsync(factory);

        var body = await GetIceServersAsync(client);

        Assert.Equal(1200, body.GetProperty("ttlSeconds").GetInt32());
        var turn = body.GetProperty("iceServers").EnumerateArray()
            .Single(item => item.GetProperty("urls")[0].GetString()!.StartsWith("turn:", StringComparison.Ordinal));
        Assert.Equal(2, turn.GetProperty("urls").GetArrayLength());
        Assert.Equal("turn:relay.example.com:3478?transport=tcp", turn.GetProperty("urls")[1].GetString());
        Assert.True(TryGetNonNull(turn, "credential", out _));
    }

    [Fact]
    public async Task Ice_servers_derives_turn_and_stun_uris_from_the_public_host()
    {
        await using var factory = new SonicRelayApiFactory(new Dictionary<string, string?>
        {
            ["TURN_STATIC_AUTH_SECRET"] = "derived-host-secret",
            ["TURN_PUBLIC_HOST"] = "turn.example.com"
        });
        var (client, _) = await BootstrapAsync(factory);

        var body = await GetIceServersAsync(client);

        var servers = body.GetProperty("iceServers").EnumerateArray().ToList();
        Assert.Equal(2, servers.Count);
        Assert.Equal("stun:turn.example.com:3478", servers[0].GetProperty("urls")[0].GetString());
        var turn = servers[1];
        Assert.Equal("turn:turn.example.com:3478?transport=udp", turn.GetProperty("urls")[0].GetString());
        Assert.Equal("turn:turn.example.com:3478?transport=tcp", turn.GetProperty("urls")[1].GetString());
        Assert.True(TryGetNonNull(turn, "username", out _));
        Assert.True(TryGetNonNull(turn, "credential", out _));
    }

    [Fact]
    public async Task Ice_servers_prefers_explicit_turn_uris_over_the_derived_ones()
    {
        await using var factory = new SonicRelayApiFactory(new Dictionary<string, string?>
        {
            ["TURN_STATIC_AUTH_SECRET"] = "explicit-over-derived",
            ["TURN_PUBLIC_HOST"] = "turn.example.com",
            ["TURN_URIS"] = "turns:turn.example.com:5349?transport=tcp",
            ["STUN_URIS"] = "stun:stun.example.com:3478"
        });
        var (client, _) = await BootstrapAsync(factory);

        var body = await GetIceServersAsync(client);

        var servers = body.GetProperty("iceServers").EnumerateArray().ToList();
        Assert.Equal("stun:stun.example.com:3478", servers[0].GetProperty("urls")[0].GetString());
        var turn = servers[1];
        Assert.Equal(1, turn.GetProperty("urls").GetArrayLength());
        Assert.Equal("turns:turn.example.com:5349?transport=tcp", turn.GetProperty("urls")[0].GetString());
    }

    private static async Task<JsonElement> GetIceServersAsync(HttpClient client)
    {
        var response = await client.GetAsync("/api/webrtc/ice-servers");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        return document.RootElement.Clone();
    }

    private static bool TryGetNonNull(JsonElement element, string property, out JsonElement value)
    {
        value = default;
        if (!element.TryGetProperty(property, out var found) || found.ValueKind == JsonValueKind.Null) return false;
        value = found;
        return true;
    }

    private static async Task<(HttpClient Client, Guid DeviceId)> BootstrapAsync(SonicRelayApiFactory factory)
    {
        var client = factory.CreateClient();
        var session = await DeviceIdentityTestHelper.BootstrapAndAuthorizeAsync(
            client, DeviceTypes.WindowsPublisher, DevicePlatforms.Windows);
        return (client, session.DeviceId);
    }
}
