namespace SonicRelay.Domain.RelaySettings;

/// <summary>
/// Global relay/coturn override, shared by every device this backend serves (there is no
/// account/owner concept). There is exactly one row, always at <see cref="SingletonId"/>;
/// absent or null fields mean "fall back to appsettings.json".
/// </summary>
public sealed class RelaySettings
{
    public static readonly Guid SingletonId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    public Guid Id { get; set; } = SingletonId;
    public string RelayMode { get; set; } = RelayModes.Automatic;
    public string[]? TurnUris { get; set; }
    public string? TurnStaticAuthSecret { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public static class RelayModes
{
    public const string Automatic = "automatic";
    public const string ForceRelay = "forceRelay";
    public const string DisableFallback = "disableFallback";

    public static bool IsValid(string? value) => value is Automatic or ForceRelay or DisableFallback;
}
