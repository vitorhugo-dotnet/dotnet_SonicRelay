namespace SonicRelay.Domain.DeviceIdentities;

public sealed class PairingChallenge
{
    public Guid Id { get; set; }
    public Guid PublisherDeviceId { get; set; }
    public string CodeHash { get; set; } = string.Empty;
    public DateTimeOffset ExpiresAt { get; set; }
    public int MaxAttempts { get; set; }
    public int AttemptCount { get; set; }
    public DateTimeOffset? ConsumedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
