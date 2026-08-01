namespace SonicRelay.Domain.DeviceIdentities;

public sealed class DevicePairing
{
    public Guid Id { get; set; }
    public Guid PublisherDeviceId { get; set; }
    public Guid ViewerDeviceId { get; set; }
    public string Status { get; set; } = DevicePairingStatuses.Active;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}

public static class DevicePairingStatuses
{
    public const string Active = "active";
    public const string Revoked = "revoked";
}
