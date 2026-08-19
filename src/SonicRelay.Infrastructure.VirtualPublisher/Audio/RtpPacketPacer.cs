using System.Diagnostics;

namespace SonicRelay.Infrastructure.VirtualPublisher.Audio;

/// <summary>
/// Paces encoded audio packets onto the wire: one packet per frame deadline on a
/// monotonic schedule, so a burst of decoded frames does not leave as an RTP burst.
/// The backlog is bounded by a latency budget; past it the oldest packets are
/// dropped instead of growing latency. <see cref="Enqueue"/> never blocks.
/// </summary>
public sealed class RtpPacketPacer : IAsyncDisposable
{
    private readonly TimeSpan frameDuration;
    private readonly long frameTimestampTicks;
    private readonly int capacity;
    private readonly Action<byte[]> send;
    private readonly Queue<byte[]> queue = new();
    private readonly object sync = new();
    private readonly SemaphoreSlim signal = new(0);
    private readonly CancellationTokenSource cancellation = new();
    private readonly Task pump;
    private long packetsSent;
    private long packetsDropped;
    private long sendFailures;
    private bool disposed;

    public RtpPacketPacer(TimeSpan frameDuration, TimeSpan latencyBudget, Action<byte[]> send)
    {
        if (frameDuration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(frameDuration));
        if (latencyBudget < frameDuration)
            throw new ArgumentOutOfRangeException(
                nameof(latencyBudget), "The latency budget must hold at least one frame.");
        this.frameDuration = frameDuration;
        this.send = send ?? throw new ArgumentNullException(nameof(send));
        frameTimestampTicks = (long)(frameDuration.TotalSeconds * Stopwatch.Frequency);
        capacity = (int)(latencyBudget.Ticks / frameDuration.Ticks);
        pump = Task.Run(PumpAsync);
    }

    public long PacketsSent => Interlocked.Read(ref packetsSent);
    public long PacketsDropped => Interlocked.Read(ref packetsDropped);
    public long SendFailures => Interlocked.Read(ref sendFailures);

    public int Backlog
    {
        get { lock (sync) return queue.Count; }
    }

    public TimeSpan BacklogDuration => frameDuration * Backlog;

    public void Enqueue(byte[] packet)
    {
        ArgumentNullException.ThrowIfNull(packet);
        if (packet.Length == 0) return;
        lock (sync)
        {
            if (disposed) return;
            queue.Enqueue(packet);
            while (queue.Count > capacity)
            {
                queue.Dequeue();
                Interlocked.Increment(ref packetsDropped);
            }
            signal.Release();
        }
    }

    public void Clear()
    {
        lock (sync) queue.Clear();
    }

    private async Task PumpAsync()
    {
        var token = cancellation.Token;
        long nextDeadline = 0;
        var anchored = false;
        try
        {
            while (true)
            {
                await signal.WaitAsync(token).ConfigureAwait(false);
                byte[]? packet = null;
                lock (sync)
                {
                    if (queue.Count > 0) packet = queue.Dequeue();
                }
                if (packet is null) continue;

                var now = Stopwatch.GetTimestamp();
                if (!anchored || now - nextDeadline > frameTimestampTicks)
                {
                    nextDeadline = now;
                    anchored = true;
                }

                var wait = nextDeadline - now;
                if (wait > 0)
                {
                    await Task.Delay(TimeSpan.FromSeconds(wait / (double)Stopwatch.Frequency), token)
                        .ConfigureAwait(false);
                }

                try
                {
                    send(packet);
                    Interlocked.Increment(ref packetsSent);
                }
                catch (Exception)
                {
                    Interlocked.Increment(ref sendFailures);
                }
                nextDeadline += frameTimestampTicks;
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (sync)
        {
            if (disposed) return;
            disposed = true;
            queue.Clear();
        }
        await cancellation.CancelAsync().ConfigureAwait(false);
        try
        {
            await pump.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        cancellation.Dispose();
        signal.Dispose();
    }
}
