using SonicRelay.Infrastructure.Signaling;
using Xunit;

namespace SonicRelay.Api.IntegrationTests;

public sealed class ParticipantReconnectTrackerTests
{
    [Fact]
    public async Task Cancelling_before_expiry_prevents_the_callback_from_firing()
    {
        var tracker = new InMemoryParticipantReconnectTracker();
        var participantId = Guid.NewGuid();
        var fired = false;
        tracker.BeginGracePeriod(participantId, TimeSpan.FromMilliseconds(200), () =>
        {
            fired = true;
            return Task.CompletedTask;
        });

        var cancelled = tracker.TryCancelGracePeriod(participantId);

        await Task.Delay(400);
        Assert.True(cancelled);
        Assert.False(fired);
    }

    [Fact]
    public async Task Expiring_without_cancellation_invokes_the_callback_once()
    {
        var tracker = new InMemoryParticipantReconnectTracker();
        var participantId = Guid.NewGuid();
        var fireCount = 0;
        var signal = new TaskCompletionSource();
        tracker.BeginGracePeriod(participantId, TimeSpan.FromMilliseconds(50), () =>
        {
            Interlocked.Increment(ref fireCount);
            signal.TrySetResult();
            return Task.CompletedTask;
        });

        await signal.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, fireCount);
        Assert.False(tracker.TryCancelGracePeriod(participantId));
    }

    [Fact]
    public void Cancelling_with_no_pending_grace_period_returns_false()
    {
        var tracker = new InMemoryParticipantReconnectTracker();
        Assert.False(tracker.TryCancelGracePeriod(Guid.NewGuid()));
    }

    [Fact]
    public async Task Starting_a_new_grace_period_replaces_and_cancels_the_previous_one()
    {
        var tracker = new InMemoryParticipantReconnectTracker();
        var participantId = Guid.NewGuid();
        var firstFired = false;
        tracker.BeginGracePeriod(participantId, TimeSpan.FromMilliseconds(50), () =>
        {
            firstFired = true;
            return Task.CompletedTask;
        });

        var secondSignal = new TaskCompletionSource();
        tracker.BeginGracePeriod(participantId, TimeSpan.FromMilliseconds(50), () =>
        {
            secondSignal.TrySetResult();
            return Task.CompletedTask;
        });

        await secondSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(firstFired);
    }
}
