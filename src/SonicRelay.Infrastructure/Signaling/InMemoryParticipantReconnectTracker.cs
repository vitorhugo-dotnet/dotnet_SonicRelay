using System.Collections.Concurrent;
using SonicRelay.Application.Abstractions;

namespace SonicRelay.Infrastructure.Signaling;

public sealed class InMemoryParticipantReconnectTracker : IParticipantReconnectTracker
{
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _pending = new();

    public void BeginGracePeriod(Guid participantId, TimeSpan graceDuration, Func<Task> onExpiredAsync)
    {
        var cts = new CancellationTokenSource();
        if (_pending.TryRemove(participantId, out var previous))
        {
            previous.Cancel();
            previous.Dispose();
        }
        _pending[participantId] = cts;
        _ = RunAsync(participantId, graceDuration, onExpiredAsync, cts);
    }

    public bool TryCancelGracePeriod(Guid participantId)
    {
        if (!_pending.TryRemove(participantId, out var cts)) return false;
        cts.Cancel();
        cts.Dispose();
        return true;
    }

    private async Task RunAsync(Guid participantId, TimeSpan graceDuration, Func<Task> onExpiredAsync,
        CancellationTokenSource cts)
    {
        try
        {
            await Task.Delay(graceDuration, cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        // Only fire if this is still the current pending timer for the participant; a
        // concurrent TryCancelGracePeriod or a newer BeginGracePeriod call already removed it.
        if (_pending.TryRemove(new KeyValuePair<Guid, CancellationTokenSource>(participantId, cts)))
        {
            cts.Dispose();
            await onExpiredAsync();
        }
    }
}
