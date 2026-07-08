using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace SonicRelay.Api.Services;

/// <summary>
/// ICE server configuration handed to WebRTC clients. TURN entries carry
/// time-limited credentials computed with coturn's REST-API convention
/// (`--use-auth-secret`): username is "&lt;unix expiry&gt;:&lt;user id&gt;" and the
/// credential is Base64(HMAC-SHA1(static secret, username)).
/// </summary>
public sealed class TurnOptions
{
    public string? Host { get; set; }
    public string? Realm { get; set; }
    public string? Secret { get; set; }
    public int TtlSeconds { get; set; } = 3600;
    public bool EnableGoogleStunFallback { get; set; }
}

public sealed record IceServerEntry(IReadOnlyList<string> Urls, string? Username = null, string? Credential = null);

public sealed record IceServersResponse(
    IReadOnlyList<IceServerEntry> IceServers,
    string IceTransportPolicy,
    DateTimeOffset ExpiresAt);

public sealed class TurnCredentialService(IOptions<TurnOptions> options, TimeProvider time)
{
    private const string GoogleStunFallbackUri = "stun:stun1.google.com:19302";

    public IceServersResponse Build(string userId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        var settings = options.Value;
        var expiresAt = time.GetUtcNow().AddSeconds(settings.TtlSeconds);
        var servers = new List<IceServerEntry>();

        if (!string.IsNullOrWhiteSpace(settings.Host) && !string.IsNullOrWhiteSpace(settings.Secret))
        {
            servers.Add(new IceServerEntry([$"stun:{settings.Host}:3478"]));

            var username = FormattableString.Invariant($"{expiresAt.ToUnixTimeSeconds()}:{userId}");
            var credential = Convert.ToBase64String(HMACSHA1.HashData(
                Encoding.UTF8.GetBytes(settings.Secret),
                Encoding.UTF8.GetBytes(username)));

            servers.Add(new IceServerEntry(
                [
                    $"turn:{settings.Host}:3478?transport=udp",
                    $"turn:{settings.Host}:3478?transport=tcp",
                    $"turns:{settings.Host}:5349?transport=tcp"
                ],
                username,
                credential));
        }
        else if (settings.EnableGoogleStunFallback)
        {
            servers.Add(new IceServerEntry([GoogleStunFallbackUri]));
        }

        return new IceServersResponse(servers, "all", expiresAt);
    }
}
