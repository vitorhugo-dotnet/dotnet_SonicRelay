namespace SonicRelay.Domain.DeviceIdentities;

public sealed class DeviceIdentity
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string DeviceType { get; set; } = Devices.DeviceTypes.FlutterViewer;
    public string Platform { get; set; } = Devices.DevicePlatforms.Android;
    public string CredentialSecretHash { get; set; } = string.Empty;
    public int CredentialVersion { get; set; } = 1;
    public string Status { get; set; } = DeviceIdentityStatuses.Active;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastSeenAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}

public static class DeviceIdentityStatuses
{
    public const string Active = "active";
    public const string Revoked = "revoked";
}
