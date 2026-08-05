using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SonicRelay.Domain.RelaySettings;
using SonicRelay.Infrastructure.Persistence;

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

public sealed class TurnCredentialService(IOptions<TurnOptions> options, AppDbContext db, TimeProvider time)
{
    public async Task<IceServersResponse> BuildAsync(string deviceId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        var settings = options.Value;
        var overrideRow = await db.RelaySettings.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == RelaySettings.SingletonId, cancellationToken);

        var relayMode = overrideRow?.RelayMode ?? RelayModes.Automatic;
        var turnUris = overrideRow?.TurnUris is { Length: > 0 } overriddenUris ? overriddenUris : settings.TurnUris;
        var secret = string.IsNullOrWhiteSpace(overrideRow?.TurnStaticAuthSecret)
            ? settings.StaticAuthSecret
            : overrideRow.TurnStaticAuthSecret;

        var servers = new List<IceServerEntry>();
        if (settings.StunUris.Length > 0)
        {
            servers.Add(new IceServerEntry(settings.StunUris));
        }

        if (relayMode != RelayModes.DisableFallback && !string.IsNullOrWhiteSpace(secret) && turnUris.Length > 0)
        {
            var expiry = time.GetUtcNow().ToUnixTimeSeconds() + settings.CredentialTtlSeconds;
            var username = FormattableString.Invariant($"{expiry}:{deviceId}");
            var credential = Convert.ToBase64String(HMACSHA1.HashData(
                Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(username)));
            servers.Add(new IceServerEntry(turnUris, username, credential));
        }

        return new IceServersResponse(servers, settings.CredentialTtlSeconds);
    }
}
