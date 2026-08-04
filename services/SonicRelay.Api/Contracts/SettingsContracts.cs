namespace SonicRelay.Api.Contracts;

public sealed record RelaySettingsResponse(string RelayMode, IReadOnlyList<string> TurnUris, bool HasCustomTurnSecret);

public sealed record UpdateRelaySettingsRequest(string? RelayMode, IReadOnlyList<string>? TurnUris, string? TurnStaticAuthSecret);
