using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text.Json;

namespace SonicRelay.Api.Services;

/// <summary>
/// Headless signaling WebSocket client for the public radio's virtual publisher —
/// same wire protocol every real publisher device speaks against
/// SignalingWebSocketEndpoint (see tools/SonicRelay.SignalingClient for the
/// reference client this is adapted from), just used from inside the API process
/// itself instead of an external device.
/// </summary>
public sealed class PublicRoomSignalingClient(Uri baseUrl, string accessToken) : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ClientWebSocket socket = new();
    private Guid sessionId;
    private bool disposed;

    public Guid PublisherParticipantId { get; private set; }

    public event Func<Guid, string, JsonElement, CancellationToken, Task>? MessageReceived;

    public async Task ConnectAsPublisherAsync(Guid sessionIdToJoin, CancellationToken ct)
    {
        sessionId = sessionIdToJoin;
        socket.Options.SetRequestHeader("Authorization", new AuthenticationHeaderValue("Bearer", accessToken).ToString());
        var scheme = baseUrl.Scheme == Uri.UriSchemeHttps ? "wss" : "ws";
        var uri = new UriBuilder(new Uri(baseUrl, "ws/signaling"))
        {
            Scheme = scheme,
            Query = $"sessionId={sessionId}"
        }.Uri;
        await socket.ConnectAsync(uri, ct).ConfigureAwait(false);

        var envelope = await ReceiveAsync(ct).ConfigureAwait(false);
        if (envelope.GetProperty("type").GetString() != "session.joined")
            throw new InvalidOperationException("Expected session.joined as the first signaling message.");
        PublisherParticipantId = envelope.GetProperty("payload").GetProperty("participantId").GetGuid();
    }

    public async Task SendAsync(Guid toParticipantId, string type, object payload, CancellationToken ct)
    {
        var envelope = new
        {
            type,
            messageId = Guid.NewGuid(),
            to = toParticipantId,
            payload = JsonSerializer.SerializeToElement(payload, JsonOptions)
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, JsonOptions);
        await socket.SendAsync(bytes, WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
    }

    public async Task RunReceiveLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && socket.State == WebSocketState.Open)
        {
            JsonElement envelope;
            try
            {
                envelope = await ReceiveAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (WebSocketException)
            {
                return; // socket closed by the server or the network; caller reconnects
            }

            var type = envelope.GetProperty("type").GetString();
            if (type is null) continue;
            var from = envelope.TryGetProperty("from", out var fromProp) && fromProp.ValueKind == JsonValueKind.String
                ? fromProp.GetGuid()
                : Guid.Empty;
            var payload = envelope.TryGetProperty("payload", out var p) ? p : default;

            var handlers = MessageReceived;
            if (handlers is not null) await handlers.Invoke(from, type, payload, ct).ConfigureAwait(false);
        }
    }

    private async Task<JsonElement> ReceiveAsync(CancellationToken ct)
    {
        using var stream = new MemoryStream();
        var buffer = new byte[8192];
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer, ct).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
                throw new WebSocketException("Signaling socket closed by the server.");
            await stream.WriteAsync(buffer.AsMemory(0, result.Count), ct).ConfigureAwait(false);
        } while (!result.EndOfMessage);

        stream.Position = 0;
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        return document.RootElement.Clone();
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "shutting down", timeout.Token)
                    .ConfigureAwait(false);
            }
            catch
            {
                // Best-effort close; disposal below still releases local resources.
            }
        }
        socket.Dispose();
    }
}
