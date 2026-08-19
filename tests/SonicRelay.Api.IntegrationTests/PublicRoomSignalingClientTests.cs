using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using SonicRelay.Api.Services;
using Xunit;

namespace SonicRelay.Api.IntegrationTests;

/// <summary>
/// Covers the review finding that <see cref="PublicRoomSignalingClient.SendAsync"/> must
/// serialize outbound socket writes, since once a viewer is added, sends can genuinely race (the
/// receive-loop's message handler vs. a peer's fire-and-forget ICE-candidate callback), and some
/// <see cref="ClientWebSocket"/> implementations throw <see cref="InvalidOperationException"/> on
/// overlapping <c>SendAsync</c> calls. Note: on this codebase's actual runtime (.NET 10, Linux,
/// the managed cross-platform <c>ClientWebSocket</c>), overlapping sends were empirically found
/// to already serialize internally without throwing or corrupting data — so this test cannot
/// demonstrate the fix turning a failure into a pass. It is kept as regression coverage for the
/// SendAsync concurrency contract itself (many concurrent sends all arrive, uncorrupted) against
/// a minimal <see cref="HttpListener"/>-based WebSocket server, since that contract matters
/// regardless of which platform's WebSocket implementation ends up handling it.
/// </summary>
public sealed class PublicRoomSignalingClientTests
{
    [Fact]
    public async Task SendAsync_serializes_concurrent_sends_without_throwing()
    {
        // Without this, the thread pool's gradual ramp-up (new worker threads are injected at
        // most a couple per second) makes the Barrier-released burst below trickle out instead
        // of actually overlapping, turning this into a slow test of the thread pool rather than
        // a fast test of SendAsync's locking.
        ThreadPool.GetMinThreads(out _, out var completionPortThreads);
        ThreadPool.SetMinThreads(32, completionPortThreads);

        using var server = new TestSignalingServer();
        await using var client = new PublicRoomSignalingClient(new Uri(server.HttpBaseUrl), "test-token");

        var connectTask = client.ConnectAsPublisherAsync(Guid.NewGuid(), CancellationToken.None);
        await server.WaitForConnectionAsync();
        await server.SendJoinedAsync(Guid.NewGuid());
        await connectTask;

        var toParticipant = Guid.NewGuid();
        const int concurrentSends = 20;
        // A large-ish payload plus a genuine thread-pool hop (Task.Run, not just Select-then-
        // await) is what actually produces overlapping ClientWebSocket.SendAsync calls: without
        // real concurrency the sends tend to complete synchronously one at a time and never race.
        var candidatePayload = new string('a', 4000);
        using var start = new Barrier(concurrentSends + 1);
        var sendTasks = Enumerable.Range(0, concurrentSends)
            .Select(i => Task.Run(async () =>
            {
                start.SignalAndWait();
                await client.SendAsync(toParticipant, "webrtc.ice_candidate",
                    new { candidate = $"{candidatePayload}-{i}" }, CancellationToken.None);
            }))
            .ToArray();
        start.SignalAndWait();

        // If SendAsync's lock ever regressed and the underlying WebSocket implementation throws
        // on overlapping sends, Task.WhenAll surfaces that failure here.
        await Task.WhenAll(sendTasks);

        var receivedCount = await server.WaitForMessageCountAsync(concurrentSends, TimeSpan.FromSeconds(5));
        Assert.Equal(concurrentSends, receivedCount);
    }

    private sealed class TestSignalingServer : IDisposable
    {
        private readonly HttpListener listener;
        private readonly TaskCompletionSource socketReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly List<string> receivedMessages = [];
        private readonly object sync = new();
        private WebSocket? socket;

        public TestSignalingServer()
        {
            var port = GetFreeTcpPort();
            HttpBaseUrl = $"http://127.0.0.1:{port}/";
            listener = new HttpListener();
            listener.Prefixes.Add(HttpBaseUrl);
            listener.Start();
            _ = AcceptAsync();
        }

        public string HttpBaseUrl { get; }

        public Task WaitForConnectionAsync() => socketReady.Task;

        public async Task SendJoinedAsync(Guid participantId)
        {
            var envelope = JsonSerializer.SerializeToUtf8Bytes(new
            {
                type = "session.joined",
                payload = new { participantId }
            });
            await socket!.SendAsync(envelope, WebSocketMessageType.Text, true, CancellationToken.None);
        }

        public async Task<int> WaitForMessageCountAsync(int expected, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                lock (sync)
                {
                    if (receivedMessages.Count >= expected) return receivedMessages.Count;
                }
                await Task.Delay(20);
            }
            lock (sync) return receivedMessages.Count;
        }

        private async Task AcceptAsync()
        {
            var context = await listener.GetContextAsync();
            var wsContext = await context.AcceptWebSocketAsync(null);
            socket = wsContext.WebSocket;
            socketReady.SetResult();

            var buffer = new byte[8192];
            while (socket.State == WebSocketState.Open)
            {
                using var ms = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(buffer, CancellationToken.None);
                    if (result.MessageType == WebSocketMessageType.Close) return;
                    ms.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);

                lock (sync) receivedMessages.Add(Encoding.UTF8.GetString(ms.ToArray()));
            }
        }

        private static int GetFreeTcpPort()
        {
            var tcpListener = new TcpListener(IPAddress.Loopback, 0);
            tcpListener.Start();
            var port = ((IPEndPoint)tcpListener.LocalEndpoint).Port;
            tcpListener.Stop();
            return port;
        }

        public void Dispose()
        {
            try
            {
                listener.Stop();
            }
            catch
            {
                // Best-effort cleanup only.
            }
            listener.Close();
        }
    }
}
