using SonicRelay.Infrastructure.VirtualPublisher.Audio;
using Xunit;

namespace SonicRelay.VirtualPublisher.Tests;

public sealed class RtpPacketPacerTests
{
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
}
