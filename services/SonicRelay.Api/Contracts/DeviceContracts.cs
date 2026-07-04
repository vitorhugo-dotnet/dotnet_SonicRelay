namespace SonicRelay.Api.Contracts;

public sealed record CreateDeviceRequest(string? Name, string? Type, string? Platform, string? PublicKey);

public sealed record UpdateDeviceRequest(string? Name, string? PublicKey);

public sealed record DeviceResponse(
    Guid Id,
    string Name,
    string Type,
    string Platform,
    string? PublicKey,
    bool Trusted,
    bool Revoked,
    DateTimeOffset? LastSeenAt,
    DateTimeOffset CreatedAt);
