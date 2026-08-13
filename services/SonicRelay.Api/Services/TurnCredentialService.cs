using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using SonicRelay.Domain.RelaySettings;

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
    public Task<IceServersResponse> BuildAsync(string deviceId, CancellationToken cancellationToken) =>
        BuildAsync(deviceId, relayOverride: null, cancellationToken);

    /// <summary>
    /// Builds the ICE server list for a device. When the device (or a paired peer) configured a
    /// custom relay, that relay replaces the provider's TURN entry entirely, so the provider's
    /// coturn URIs never reach a client that opted out of them. A relay mode of
    /// <see cref="RelayModes.DisableFallback"/> drops the TURN entry altogether.
    /// </summary>
    public Task<IceServersResponse> BuildAsync(string deviceId, RelayDeviceSettings? relayOverride,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        var settings = options.Value;

        var servers = new List<IceServerEntry>();
        if (settings.StunUris.Length > 0)
        {
            servers.Add(new IceServerEntry(settings.StunUris));
        }

        var disableRelay = relayOverride?.RelayMode == RelayModes.DisableFallback;
        var customTurnUris = relayOverride?.TurnUris ?? [];
        if (!disableRelay && customTurnUris.Length > 0)
        {
            var (username, credential) = !string.IsNullOrWhiteSpace(relayOverride!.TurnUsername)
                ? (relayOverride.TurnUsername, relayOverride.TurnCredential)
                // Without explicit credentials the custom relay is assumed to share the
                // provider's static auth secret, so the standard REST credentials still work.
                : BuildRestCredentials(settings, deviceId);
            servers.Add(new IceServerEntry(customTurnUris, username, credential));
        }
        else if (!disableRelay && !string.IsNullOrWhiteSpace(settings.StaticAuthSecret) && settings.TurnUris.Length > 0)
        {
            var (username, credential) = BuildRestCredentials(settings, deviceId);
            servers.Add(new IceServerEntry(settings.TurnUris, username, credential));
        }

        return Task.FromResult(new IceServersResponse(servers, settings.CredentialTtlSeconds));
    }

    private (string? Username, string? Credential) BuildRestCredentials(TurnOptions settings, string deviceId)
    {
        if (string.IsNullOrWhiteSpace(settings.StaticAuthSecret)) return (null, null);
        var expiry = time.GetUtcNow().ToUnixTimeSeconds() + settings.CredentialTtlSeconds;
        var username = FormattableString.Invariant($"{expiry}:{deviceId}");
        var credential = Convert.ToBase64String(HMACSHA1.HashData(
            Encoding.UTF8.GetBytes(settings.StaticAuthSecret), Encoding.UTF8.GetBytes(username)));
        return (username, credential);
    }
}
