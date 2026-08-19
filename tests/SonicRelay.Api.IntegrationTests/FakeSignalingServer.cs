using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace SonicRelay.Api.IntegrationTests;

/// <summary>
/// Minimal stand-in for <c>/ws/signaling</c> for tests that need to observe what
/// <see cref="SonicRelay.Api.Services.PublicRoomSignalingClient"/> actually puts on the wire.
/// A real <see cref="HttpListener"/> WebSocket is used rather than a mock so the client's own
/// framing/serialization is exercised end to end.
/// </summary>
internal sealed class FakeSignalingServer : IDisposable
{
    private readonly HttpListener listener;
    private readonly TaskCompletionSource socketReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<JsonElement> received = [];
    private readonly object sync = new();
    private WebSocket? socket;

    public FakeSignalingServer()
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

    /// <summary>Mimics the server's first frame to a freshly admitted publisher socket.</summary>
    public async Task SendSelfJoinedAsync(Guid participantId)
    {
        var envelope = JsonSerializer.SerializeToUtf8Bytes(new
        {
            type = "session.joined",
            payload = new { participantId, role = "publisher" }
        });
        await socket!.SendAsync(envelope, WebSocketMessageType.Text, true, CancellationToken.None);
    }

    public IReadOnlyList<JsonElement> Received
    {
        get { lock (sync) return [.. received]; }
    }

    public async Task<IReadOnlyList<JsonElement>> WaitForMessagesAsync(int expected, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            lock (sync)
            {
                if (received.Count >= expected) return [.. received];
            }
            await Task.Delay(20);
        }
        return Received;
    }

    private async Task AcceptAsync()
    {
        try
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

                using var document = JsonDocument.Parse(Encoding.UTF8.GetString(ms.ToArray()));
                lock (sync) received.Add(document.RootElement.Clone());
            }
        }
        catch (Exception exception)
        {
            socketReady.TrySetException(exception);
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
