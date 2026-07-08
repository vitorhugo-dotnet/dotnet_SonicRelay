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
    public async Task Ice_servers_returns_no_servers_when_turn_is_not_configured_and_fallback_disabled()
    {
        var (client, _) = await CreateUserAsync("ice-none", _factory);

        var body = await GetIceServersAsync(client);

        Assert.Empty(body.GetProperty("iceServers").EnumerateArray());
        Assert.Equal("all", body.GetProperty("iceTransportPolicy").GetString());
        Assert.True(body.TryGetProperty("expiresAt", out _));
    }

    [Fact]
    public async Task Ice_servers_falls_back_to_google_stun_only_when_explicitly_enabled()
    {
        await using var factory = new SonicRelayApiFactory(new Dictionary<string, string?>
        {
            ["Turn:EnableGoogleStunFallback"] = "true"
        });
        var (client, _) = await CreateUserAsync("ice-fallback", factory);

        var body = await GetIceServersAsync(client);

        var entry = Assert.Single(body.GetProperty("iceServers").EnumerateArray());
        Assert.Equal("stun:stun1.google.com:19302", entry.GetProperty("urls")[0].GetString());
    }

    [Fact]
    public async Task Ice_servers_returns_stun_and_turn_entries_with_coturn_rest_credentials()
    {
        const string secret = "integration-turn-secret";
        await using var factory = new SonicRelayApiFactory(new Dictionary<string, string?>
        {
            ["Turn:Host"] = "sonicrelay-turn.hugodotnet.dev",
            ["Turn:Realm"] = "sonicrelay-turn.hugodotnet.dev",
            ["Turn:Secret"] = secret,
            ["Turn:TtlSeconds"] = "600"
        });
        var (client, userId) = await CreateUserAsync("ice-turn", factory);
        var before = DateTimeOffset.UtcNow;

        var body = await GetIceServersAsync(client);

        Assert.Equal("all", body.GetProperty("iceTransportPolicy").GetString());
        var expiresAt = body.GetProperty("expiresAt").GetDateTimeOffset();
        Assert.InRange(expiresAt, before.AddSeconds(600).AddSeconds(-30), before.AddSeconds(600).AddSeconds(30));

        var servers = body.GetProperty("iceServers").EnumerateArray().ToList();
        Assert.Equal(2, servers.Count);

        var stun = servers.Single(item => item.GetProperty("urls")[0].GetString()!.StartsWith("stun:", StringComparison.Ordinal));
        Assert.Equal("stun:sonicrelay-turn.hugodotnet.dev:3478", stun.GetProperty("urls")[0].GetString());
        Assert.False(TryGetNonNull(stun, "username", out _));
        Assert.False(TryGetNonNull(stun, "credential", out _));

        var turn = servers.Single(item => item.GetProperty("urls")[0].GetString()!.StartsWith("turn:", StringComparison.Ordinal));
        var turnUrls = turn.GetProperty("urls").EnumerateArray().Select(u => u.GetString()).ToList();
        Assert.Equal(
        [
            "turn:sonicrelay-turn.hugodotnet.dev:3478?transport=udp",
            "turn:sonicrelay-turn.hugodotnet.dev:3478?transport=tcp",
            "turns:sonicrelay-turn.hugodotnet.dev:5349?transport=tcp"
        ], turnUrls);

        var username = turn.GetProperty("username").GetString()!;
        var parts = username.Split(':', 2);
        var expiry = DateTimeOffset.FromUnixTimeSeconds(long.Parse(parts[0]));
        Assert.Equal(userId.ToString("D"), parts[1]);
        Assert.Equal(expiresAt.ToUnixTimeSeconds(), expiry.ToUnixTimeSeconds());

        var expected = Convert.ToBase64String(HMACSHA1.HashData(
            Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(username)));
        Assert.Equal(expected, turn.GetProperty("credential").GetString());
    }

    [Fact]
    public async Task Ice_servers_accepts_flat_environment_style_configuration()
    {
        await using var factory = new SonicRelayApiFactory(new Dictionary<string, string?>
        {
            ["WEBRTC_TURN_HOST"] = "sonicrelay-turn.hugodotnet.dev",
            ["WEBRTC_TURN_REALM"] = "sonicrelay-turn.hugodotnet.dev",
            ["WEBRTC_TURN_SECRET"] = "flat-env-secret",
            ["WEBRTC_TURN_TTL_SECONDS"] = "1200"
        });
        var (client, _) = await CreateUserAsync("ice-env", factory);

        var body = await GetIceServersAsync(client);

        var turn = body.GetProperty("iceServers").EnumerateArray()
            .Single(item => item.GetProperty("urls")[0].GetString()!.StartsWith("turn:", StringComparison.Ordinal));
        Assert.Equal(3, turn.GetProperty("urls").GetArrayLength());
        Assert.True(TryGetNonNull(turn, "credential", out _));
    }

    [Fact]
    public async Task Ice_servers_response_never_exposes_the_static_secret()
    {
        const string secret = "must-not-leak-secret";
        await using var factory = new SonicRelayApiFactory(new Dictionary<string, string?>
        {
            ["Turn:Host"] = "sonicrelay-turn.hugodotnet.dev",
            ["Turn:Secret"] = secret
        });
        var (client, _) = await CreateUserAsync("ice-secret", factory);

        var response = await client.GetAsync("/api/webrtc/ice-servers");
        var raw = await response.Content.ReadAsStringAsync();

        Assert.DoesNotContain(secret, raw, StringComparison.Ordinal);
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
