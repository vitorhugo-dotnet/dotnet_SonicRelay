using System.Collections.Concurrent;
using SonicRelay.Application.Abstractions;

namespace SonicRelay.Infrastructure.Sessions;

/// <summary>
/// In-process <see cref="IParticipantAdmissionLock"/>. Entries are reference-counted and
/// removed once the last waiter releases them, so a long-lived API instance does not
/// accumulate one semaphore per session/device pair it has ever seen.
/// </summary>
public sealed class InMemoryParticipantAdmissionLock : IParticipantAdmissionLock
{
    private readonly ConcurrentDictionary<(Guid SessionId, Guid DeviceId), Entry> _entries = new();

    public async Task<IDisposable> AcquireAsync(Guid sessionId, Guid deviceId,
        CancellationToken cancellationToken)
    {
        var key = (sessionId, deviceId);
        Entry entry;
        // Retry until the entry we incremented is still the one published under the key: a
        // concurrent release can remove an entry between our lookup and our increment.
        while (true)
        {
            entry = _entries.GetOrAdd(key, _ => new Entry());
            lock (entry)
            {
                if (!entry.Abandoned)
                {
                    entry.Waiters++;
                    break;
                }
            }
        }

        try
        {
            await entry.Semaphore.WaitAsync(cancellationToken);
        }
        catch
        {
            Release(key, entry, acquired: false);
            throw;
        }
        return new Handle(this, key, entry);
    }

    private void Release((Guid SessionId, Guid DeviceId) key, Entry entry, bool acquired)
    {
        if (acquired) entry.Semaphore.Release();
        lock (entry)
        {
            entry.Waiters--;
            if (entry.Waiters > 0) return;
            entry.Abandoned = true;
        }
        _entries.TryRemove(new KeyValuePair<(Guid, Guid), Entry>(key, entry));
        entry.Semaphore.Dispose();
    }

    private sealed class Entry
    {
        public readonly SemaphoreSlim Semaphore = new(1, 1);
        public int Waiters;

        /// <summary>Set once the entry has been retired, so a late waiter re-adds a fresh one.</summary>
        public bool Abandoned;
    }

    private sealed class Handle(
        InMemoryParticipantAdmissionLock owner,
        (Guid SessionId, Guid DeviceId) key,
        Entry entry) : IDisposable
    {
        private bool _released;

        public void Dispose()
        {
            if (_released) return;
            _released = true;
            owner.Release(key, entry, acquired: true);
        }
    }
}
