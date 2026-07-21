namespace SonicRelay.Api.Contracts;

public sealed record BootstrapDeviceRequest(string? Name, string? DeviceType, string? Platform);

public sealed record BootstrapDeviceResponse(Guid DeviceId, string CredentialSecret, int CredentialVersion);

public sealed record DeviceTokenRequest(Guid DeviceId, string CredentialSecret);

public sealed record DeviceTokenResponse(string AccessToken, DateTimeOffset ExpiresAt, IReadOnlyList<string> Scopes);
