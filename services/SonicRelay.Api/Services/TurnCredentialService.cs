using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace SonicRelay.Api.Services;

/// <summary>
/// ICE server configuration handed to WebRTC clients. TURN entries carry
/// time-limited credentials computed with coturn's REST-API convention
/// (`--use-auth-secret`): username is "&lt;unix expiry&gt;:&lt;device id&gt;" and the
/// credential is Base64(HMAC-SHA1(static secret, username)).
/// </summary>
public sealed class TurnOptions
{
    public string? StaticAuthSecret { get; set; }
    public string[] TurnUris { get; set; } = [];
    public string[] StunUris { get; set; } = ["stun:stun.l.google.com:19302"];
    public int CredentialTtlSeconds { get; set; } = 3600;
}

public sealed record IceServerEntry(IReadOnlyList<string> Urls, string? Username = null, string? Credential = null);

public sealed record IceServersResponse(IReadOnlyList<IceServerEntry> IceServers, int TtlSeconds);

public sealed class TurnCredentialService(IOptions<TurnOptions> options, TimeProvider time)
{
    public IceServersResponse Build(string deviceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        var settings = options.Value;
        var servers = new List<IceServerEntry>();
        if (settings.StunUris.Length > 0)
        {
            servers.Add(new IceServerEntry(settings.StunUris));
        }

        if (!string.IsNullOrWhiteSpace(settings.StaticAuthSecret) && settings.TurnUris.Length > 0)
        {
            var expiry = time.GetUtcNow().ToUnixTimeSeconds() + settings.CredentialTtlSeconds;
            var username = FormattableString.Invariant($"{expiry}:{deviceId}");
            var credential = Convert.ToBase64String(HMACSHA1.HashData(
                Encoding.UTF8.GetBytes(settings.StaticAuthSecret),
                Encoding.UTF8.GetBytes(username)));
            servers.Add(new IceServerEntry(settings.TurnUris, username, credential));
        }

        return new IceServersResponse(servers, settings.CredentialTtlSeconds);
    }
}
