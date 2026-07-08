using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using SonicRelay.Api.Services;
using Xunit;

namespace SonicRelay.Api.IntegrationTests;

public sealed class TurnCredentialServiceTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 7, 8, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Build_returns_no_servers_when_turn_is_unconfigured_and_fallback_disabled()
    {
        var service = CreateService(new TurnOptions());

        var result = service.Build("user-1");

        Assert.Empty(result.IceServers);
        Assert.Equal("all", result.IceTransportPolicy);
        Assert.Equal(FixedNow.AddSeconds(3600), result.ExpiresAt);
    }

    [Fact]
    public void Build_falls_back_to_google_stun_only_when_explicitly_enabled()
    {
        var service = CreateService(new TurnOptions { EnableGoogleStunFallback = true });

        var result = service.Build("user-1");

        var entry = Assert.Single(result.IceServers);
        Assert.Equal(["stun:stun1.google.com:19302"], entry.Urls);
        Assert.Null(entry.Username);
        Assert.Null(entry.Credential);
    }

    [Fact]
    public void Build_ignores_google_stun_fallback_when_turn_host_and_secret_are_configured()
    {
        var service = CreateService(new TurnOptions
        {
            Host = "sonicrelay-turn.hugodotnet.dev",
            Secret = "s3cr3t",
            EnableGoogleStunFallback = true
        });

        var result = service.Build("user-1");

        Assert.DoesNotContain(result.IceServers, entry => entry.Urls.Contains("stun:stun1.google.com:19302"));
    }

    [Fact]
    public void Build_returns_stun_and_turn_entries_derived_from_host()
    {
        var service = CreateService(new TurnOptions
        {
            Host = "sonicrelay-turn.hugodotnet.dev",
            Secret = "s3cr3t",
            TtlSeconds = 3600
        });

        var result = service.Build("user-42");

        Assert.Equal(2, result.IceServers.Count);
        var stun = result.IceServers[0];
        Assert.Equal(["stun:sonicrelay-turn.hugodotnet.dev:3478"], stun.Urls);
        Assert.Null(stun.Username);

        var turn = result.IceServers[1];
        Assert.Equal(
        [
            "turn:sonicrelay-turn.hugodotnet.dev:3478?transport=udp",
            "turn:sonicrelay-turn.hugodotnet.dev:3478?transport=tcp",
            "turns:sonicrelay-turn.hugodotnet.dev:5349?transport=tcp"
        ], turn.Urls);
        Assert.NotNull(turn.Username);
        Assert.NotNull(turn.Credential);
    }

    [Fact]
    public void Build_computes_username_as_unix_expiry_colon_user_id()
    {
        var service = CreateService(new TurnOptions
        {
            Host = "sonicrelay-turn.hugodotnet.dev",
            Secret = "s3cr3t",
            TtlSeconds = 900
        });

        var result = service.Build("user-42");
        var turn = result.IceServers.Single(e => e.Username is not null);

        var expectedExpiry = FixedNow.AddSeconds(900).ToUnixTimeSeconds();
        Assert.Equal($"{expectedExpiry}:user-42", turn.Username);
    }

    [Fact]
    public void Build_computes_credential_as_base64_hmac_sha1_of_username()
    {
        const string secret = "s3cr3t";
        var service = CreateService(new TurnOptions
        {
            Host = "sonicrelay-turn.hugodotnet.dev",
            Secret = secret,
            TtlSeconds = 900
        });

        var result = service.Build("user-42");
        var turn = result.IceServers.Single(e => e.Username is not null);

        var expectedCredential = Convert.ToBase64String(HMACSHA1.HashData(
            Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(turn.Username!)));
        Assert.Equal(expectedCredential, turn.Credential);
    }

    [Fact]
    public void Build_does_not_expose_the_static_secret_anywhere_in_the_response()
    {
        const string secret = "must-not-leak-secret";
        var service = CreateService(new TurnOptions
        {
            Host = "sonicrelay-turn.hugodotnet.dev",
            Secret = secret
        });

        var result = service.Build("user-1");

        foreach (var entry in result.IceServers)
        {
            Assert.DoesNotContain(secret, entry.Urls);
            Assert.NotEqual(secret, entry.Username);
            Assert.NotEqual(secret, entry.Credential);
        }
    }

    [Fact]
    public void Build_throws_for_missing_user_id()
    {
        var service = CreateService(new TurnOptions());

        Assert.Throws<ArgumentException>(() => service.Build(string.Empty));
    }

    private static TurnCredentialService CreateService(TurnOptions options) =>
        new(Options.Create(options), new FixedTimeProvider(FixedNow));

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
