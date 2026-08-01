namespace SonicRelay.Application.Abstractions;

public sealed record ConnectionDescriptor(
    string ConnectionId,
    Guid SessionId,
    Guid ParticipantId,
    Guid DeviceId,
    string Role,
    DateTimeOffset ConnectedAt,
    Func<ReadOnlyMemory<byte>, CancellationToken, Task> SendAsync);

public interface IConnectionRegistry
{
    Task RegisterAsync(ConnectionDescriptor connection, CancellationToken ct);
    Task UnregisterAsync(string connectionId, CancellationToken ct);
    Task<ConnectionDescriptor?> FindByParticipantAsync(Guid participantId, CancellationToken ct);
    Task<IReadOnlyList<ConnectionDescriptor>> ListBySessionAsync(Guid sessionId, CancellationToken ct);
    Task<bool> SendToParticipantAsync(Guid sessionId, Guid participantId, ReadOnlyMemory<byte> message, CancellationToken ct);
}
