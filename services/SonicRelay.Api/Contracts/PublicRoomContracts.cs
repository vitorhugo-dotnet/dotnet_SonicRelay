namespace SonicRelay.Api.Contracts;

public sealed record PublicRoomResponse(bool Enabled, Guid? SessionId, int MaxViewers);
