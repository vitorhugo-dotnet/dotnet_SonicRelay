using System.Collections.Concurrent;
using SonicRelay.Application.Abstractions;

namespace SonicRelay.Infrastructure.Signaling;

public sealed class InMemoryConnectionRegistry : IConnectionRegistry
{
    private readonly ConcurrentDictionary<string, ConnectionDescriptor> _connections = new();

    public Task RegisterAsync(ConnectionDescriptor connection, CancellationToken ct)
    {
        _connections[connection.ConnectionId] = connection;
        return Task.CompletedTask;
    }

    public Task UnregisterAsync(string connectionId, CancellationToken ct)
    {
        _connections.TryRemove(connectionId, out _);
        return Task.CompletedTask;
    }

    public Task<ConnectionDescriptor?> FindByParticipantAsync(Guid participantId, CancellationToken ct)
    {
        var connection = _connections.Values.FirstOrDefault(x => x.ParticipantId == participantId);
        return Task.FromResult(connection);
    }

    public Task<IReadOnlyList<ConnectionDescriptor>> ListBySessionAsync(Guid sessionId, CancellationToken ct)
    {
        var connections = _connections.Values.Where(x => x.SessionId == sessionId).ToArray();
        return Task.FromResult<IReadOnlyList<ConnectionDescriptor>>(connections);
    }
}
