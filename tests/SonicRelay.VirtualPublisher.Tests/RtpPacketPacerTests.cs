using System.Diagnostics;
using SonicRelay.Infrastructure.VirtualPublisher.Audio;
using Xunit;

namespace SonicRelay.VirtualPublisher.Tests;

public sealed class RtpPacketPacerTests
{
    private static readonly TimeSpan Frame20Ms = TimeSpan.FromMilliseconds(20);

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(stopwatch.Elapsed < timeout, "Timed out waiting for the pacer.");
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task Enqueue_sends_a_packet_through_the_callback()
    {
        var sent = new TaskCompletionSource<byte[]>();
        await using var pacer = new RtpPacketPacer(
            TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(200),
            packet => sent.TrySetResult(packet));

        pacer.Enqueue([1, 2, 3]);

        var received = await sent.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(new byte[] { 1, 2, 3 }, received);

        // PacketsSent increments right after send() returns inside PumpAsync, which can
        // race with the awaited continuation above (both are triggered from the same
        // send(packet) call) — poll briefly instead of asserting immediately.
        var deadline = DateTime.UtcNow.AddSeconds(1);
        while (pacer.PacketsSent == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }
        Assert.Equal(1, pacer.PacketsSent);
    }

    [Fact]
    public void Enqueue_drops_oldest_packets_beyond_the_latency_budget()
    {
        // frameDuration=20ms, latencyBudget=40ms -> capacity for 2 queued frames.
        // The pump task is not given time to run (no await), so packets pile up
        // synchronously and the third Enqueue must drop the first.
        var pacer = new RtpPacketPacer(
            TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(40), _ => { });

        pacer.Enqueue([1]);
        pacer.Enqueue([2]);
        pacer.Enqueue([3]);

        Assert.True(pacer.PacketsDropped >= 1);
    }

    [Fact]
    public void Constructor_rejects_a_latency_budget_shorter_than_one_frame()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new RtpPacketPacer(TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(10), _ => { }));
    }

    [Fact]
    public async Task Clear_discards_queued_packets_without_counting_them_as_dropped()
    {
        await using var pacer = new RtpPacketPacer(
            TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(200), _ => { });
        pacer.Enqueue([1]);

        pacer.Clear();

        Assert.Equal(0, pacer.Backlog);
        Assert.Equal(0, pacer.PacketsDropped);
    }

    // --- Ported from the desktop suite (SonicRelay.Windows.WebRtc.Tests.RtpPacketPacerTests) ---

    [Fact]
    public async Task BurstInputIsSpreadAcrossFrameDeadlines()
    {
        // Issue #31 suggested test: five 20 ms frames fed at once must leave over
        // roughly 100 ms, not as an immediate burst. Lower bound is the four gaps
        // between five sends minus scheduler slack; upper bound is generous for CI.
        var sendTimestamps = new List<long>();
        await using var pacer = new RtpPacketPacer(Frame20Ms, TimeSpan.FromMilliseconds(200), _ =>
        {
            lock (sendTimestamps) sendTimestamps.Add(Stopwatch.GetTimestamp());
        });

        for (var i = 0; i < 5; i++) pacer.Enqueue(new byte[10]);
        await WaitUntilAsync(() => pacer.PacketsSent == 5, TimeSpan.FromSeconds(5));

        long first, last;
        lock (sendTimestamps)
        {
            first = sendTimestamps[0];
            last = sendTimestamps[^1];
        }
        var elapsedMs = (last - first) * 1000.0 / Stopwatch.Frequency;
        Assert.True(elapsedMs >= 60, $"Five packets left in {elapsedMs:F1} ms — still a burst.");
        Assert.True(elapsedMs <= 500, $"Five packets took {elapsedMs:F1} ms — pacing is stalling.");
        Assert.Equal(0, pacer.PacketsDropped);
    }

    [Fact]
    public async Task PacingFollowsMonotonicDeadlinesWithoutCumulativeDrift()
    {
        // Deadlines are absolute (previous deadline + frame), so Task.Delay lateness
        // must not accumulate: 20 packets at 20 ms nominally span 380 ms of gaps.
        var sendTimestamps = new List<long>();
        await using var pacer = new RtpPacketPacer(Frame20Ms, TimeSpan.FromMilliseconds(500), _ =>
        {
            lock (sendTimestamps) sendTimestamps.Add(Stopwatch.GetTimestamp());
        });

        for (var i = 0; i < 20; i++) pacer.Enqueue(new byte[10]);
        await WaitUntilAsync(() => pacer.PacketsSent == 20, TimeSpan.FromSeconds(10));

        long first, last;
        lock (sendTimestamps)
        {
            first = sendTimestamps[0];
            last = sendTimestamps[^1];
        }
        var elapsedMs = (last - first) * 1000.0 / Stopwatch.Frequency;
        Assert.True(elapsedMs >= 0.7 * 380, $"20 packets spanned {elapsedMs:F1} ms — sent too fast.");
        // Upper bound is generous for CI: shared Linux runners routinely add a few ms
        // of Task.Delay scheduling latency per frame (observed up to ~630 ms here), which
        // is runner jitter, not drift, since deadlines are absolute and don't compound it.
        Assert.True(elapsedMs <= 2.5 * 380, $"20 packets spanned {elapsedMs:F1} ms — per-frame delay error is accumulating.");
    }

    [Fact]
    public async Task DisposeStopsThePumpPromptlyWithBacklogPending()
    {
        var pacer = new RtpPacketPacer(TimeSpan.FromMilliseconds(40), TimeSpan.FromMilliseconds(400), _ => { });
        for (var i = 0; i < 10; i++) pacer.Enqueue(new byte[10]);

        var dispose = pacer.DisposeAsync().AsTask();
        var finished = await Task.WhenAny(dispose, Task.Delay(TimeSpan.FromSeconds(5)));

        Assert.Same(dispose, finished);
        await dispose;
    }

    [Fact]
    public async Task EnqueueAfterDisposeIsIgnored()
    {
        var pacer = new RtpPacketPacer(Frame20Ms, TimeSpan.FromMilliseconds(200), _ => { });
        await pacer.DisposeAsync();

        pacer.Enqueue(new byte[10]);

        Assert.Equal(0, pacer.Backlog);
        Assert.Equal(0, pacer.PacketsSent);
    }

    [Fact]
    public async Task SendFailuresAreCountedAndDoNotStopThePump()
    {
        var attempts = 0;
        await using var pacer = new RtpPacketPacer(Frame20Ms, TimeSpan.FromMilliseconds(200), _ =>
        {
            if (Interlocked.Increment(ref attempts) == 1)
                throw new InvalidOperationException("transport closed");
        });

        pacer.Enqueue(new byte[10]);
        pacer.Enqueue(new byte[10]);
        await WaitUntilAsync(() => pacer.PacketsSent == 1 && pacer.SendFailures == 1, TimeSpan.FromSeconds(5));
    }
}
