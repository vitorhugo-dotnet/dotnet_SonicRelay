namespace SonicRelay.Domain.Sessions;

public sealed class StreamSession
{
    public Guid Id { get; set; }
    public Guid OwnerUserId { get; set; }
    public Guid SourceDeviceId { get; set; }
    public string Status { get; set; } = SessionStatuses.Waiting;
    public int MaxViewers { get; set; }
    public DateTimeOffset CodeExpiresAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class SessionParticipant
{
    public Guid Id { get; set; }
    public Guid SessionId { get; set; }
    public Guid UserId { get; set; }
    public Guid DeviceId { get; set; }
    public string Role { get; set; } = ParticipantRoles.Viewer;
    public string? ConnectionId { get; set; }
    public string Status { get; set; } = ParticipantStatuses.Connected;
    public DateTimeOffset JoinedAt { get; set; }
    public DateTimeOffset? LeftAt { get; set; }
}

public static class SessionStatuses
{
    public const string Waiting = "waiting";
    public const string Active = "active";
    public const string Ended = "ended";
    public const string Expired = "expired";
}

public static class ParticipantRoles
{
    public const string Publisher = "publisher";
    public const string Viewer = "viewer";
}

public static class ParticipantStatuses
{
    public const string Connected = "connected";
    public const string Disconnected = "disconnected";
}
