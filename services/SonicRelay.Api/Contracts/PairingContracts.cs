namespace SonicRelay.Api.Contracts;

public sealed record CreateChallengeResponse(Guid ChallengeId, string Code, string QrPayload, DateTimeOffset ExpiresAt);

public sealed record CompletePairingRequest(Guid ChallengeId, string Code);

public sealed record PairingResponse(
    Guid PairingId, Guid PublisherDeviceId, Guid ViewerDeviceId, string Status,
    DateTimeOffset CreatedAt, DateTimeOffset? LastUsedAt);
