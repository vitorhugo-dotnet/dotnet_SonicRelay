using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

namespace SonicRelay.Api.IntegrationTests;

public sealed class WebRtcEndpointsTests : IClassFixture<SonicRelayApiFactory>
{
    private const string Password = "Valid1!Password";
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
        var (client, _) = await CreateUserAsync("ice-stun", _factory);

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
        var (client, userId) = await CreateUserAsync("ice-turn", factory);
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
        Assert.Equal(userId.ToString("D"), parts[1]);
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
        var (client, _) = await CreateUserAsync("ice-env", factory);

        var body = await GetIceServersAsync(client);

        Assert.Equal(1200, body.GetProperty("ttlSeconds").GetInt32());
        var turn = body.GetProperty("iceServers").EnumerateArray()
            .Single(item => item.GetProperty("urls")[0].GetString()!.StartsWith("turn:", StringComparison.Ordinal));
        Assert.Equal(2, turn.GetProperty("urls").GetArrayLength());
        Assert.Equal("turn:relay.example.com:3478?transport=tcp", turn.GetProperty("urls")[1].GetString());
        Assert.True(TryGetNonNull(turn, "credential", out _));
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

    private static async Task<(HttpClient Client, Guid UserId)> CreateUserAsync(string prefix, SonicRelayApiFactory factory)
    {
        var client = factory.CreateClient();
        var email = $"{prefix}-{Guid.NewGuid():N}@example.com";
        var register = await client.PostAsJsonAsync("/auth/register", new { email, password = Password });
        Assert.Equal(HttpStatusCode.OK, register.StatusCode);
        var login = await client.PostAsJsonAsync("/auth/login", new { email, password = Password });
        var document = await JsonDocument.ParseAsync(await login.Content.ReadAsStreamAsync());
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", document.RootElement.GetProperty("accessToken").GetString());
        var profile = await client.GetFromJsonAsync<JsonElement>("/auth/me");
        return (client, profile.GetProperty("id").GetGuid());
    }
}
