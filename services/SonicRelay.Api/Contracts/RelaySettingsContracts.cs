namespace SonicRelay.Api.Contracts;

/// <summary>
/// The user's relay preferences as shown to clients. This never carries the provider's own
/// TURN configuration — empty <paramref name="TurnUris"/> means "the backend's relay is in
/// use" without naming it — and the custom credential is write-only
/// (<paramref name="HasTurnCredential"/> only reports that one is stored).
/// </summary>
public sealed record RelaySettingsResponse(
    string RelayMode,
    IReadOnlyList<string> TurnUris,
    string? TurnUsername,
    bool HasTurnCredential,
    DateTimeOffset? UpdatedAt);

public sealed record UpdateRelaySettingsRequest(
    string? RelayMode,
    IReadOnlyList<string>? TurnUris,
    string? TurnUsername,
    string? TurnCredential);
