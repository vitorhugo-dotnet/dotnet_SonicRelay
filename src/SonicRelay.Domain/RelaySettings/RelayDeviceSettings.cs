namespace SonicRelay.Domain.RelaySettings;

/// <summary>
/// Per-device relay (coturn) preferences. Each device stores its own row; the effective
/// settings for a device are the most recently updated row across the device and its
/// actively paired peers, so a change made on one device follows the user to the others.
/// The provider's own TURN configuration never lives here — an empty <see cref="TurnUris"/>
/// means "use whatever relay the backend provides" without ever naming it.
/// </summary>
public sealed class RelayDeviceSettings
{
    public Guid DeviceId { get; set; }
    public string RelayMode { get; set; } = RelayModes.Automatic;
    public string[] TurnUris { get; set; } = [];
    public string? TurnUsername { get; set; }
    public string? TurnCredential { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public static class RelayModes
{
    public const string Automatic = "automatic";
    public const string ForceRelay = "forceRelay";
    public const string DisableFallback = "disableFallback";

    public static bool IsValid(string mode) => mode is Automatic or ForceRelay or DisableFallback;
}
