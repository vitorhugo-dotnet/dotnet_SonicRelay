namespace SonicRelay.Api.Contracts;

public sealed record BootstrapDeviceRequest(string? Name, string? DeviceType, string? Platform);

public sealed record BootstrapDeviceResponse(Guid DeviceId, string CredentialSecret, int CredentialVersion);

public sealed record DeviceTokenRequest(Guid DeviceId, string CredentialSecret);

/// <param name="DeviceId">
/// The device this token authenticates. Normally the id the caller sent, but after an identity
/// rotation (see <c>docs/data-retention.md</c>) it is the replacement id, and the client must
/// persist it together with <paramref name="RotatedCredentialSecret"/>.
/// </param>
/// <param name="RotatedCredentialSecret">
/// Present only when the device identity was rotated by this call: the replacement credential,
/// returned exactly once. The previous device id and credential no longer exist.
/// </param>
public sealed record DeviceTokenResponse(
    string AccessToken,
    DateTimeOffset ExpiresAt,
    IReadOnlyList<string> Scopes,
    Guid DeviceId,
    int CredentialVersion,
    string? RotatedCredentialSecret = null);

public sealed record RotateCredentialRequest(string CurrentCredentialSecret);

public sealed record RotateCredentialResponse(string CredentialSecret, int CredentialVersion);
