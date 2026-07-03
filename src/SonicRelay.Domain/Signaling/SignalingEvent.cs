namespace SonicRelay.Domain.Signaling;

public sealed class SignalingEvent
{
    public Guid Id { get; set; }
    public Guid SessionId { get; set; }
    public Guid? FromParticipantId { get; set; }
    public Guid? ToParticipantId { get; set; }
    public string EventType { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
}
