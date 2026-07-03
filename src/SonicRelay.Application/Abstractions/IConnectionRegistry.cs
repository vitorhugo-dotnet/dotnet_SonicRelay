namespace SonicRelay.Application.Abstractions;

public sealed record ConnectionDescriptor(
    string ConnectionId,
    Guid SessionId,
    Guid ParticipantId,
    Guid UserId,
    Guid DeviceId,
    string Role,
    DateTimeOffset ConnectedAt);

public interface IConnectionRegistry
{
    Task RegisterAsync(ConnectionDescriptor connection, CancellationToken ct);
    Task UnregisterAsync(string connectionId, CancellationToken ct);
    Task<ConnectionDescriptor?> FindByParticipantAsync(Guid participantId, CancellationToken ct);
    Task<IReadOnlyList<ConnectionDescriptor>> ListBySessionAsync(Guid sessionId, CancellationToken ct);
}
