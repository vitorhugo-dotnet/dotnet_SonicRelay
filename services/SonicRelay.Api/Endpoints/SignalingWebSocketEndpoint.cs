using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using SonicRelay.Application.Abstractions;
using SonicRelay.Api.Services;
using SonicRelay.Domain.Devices;
using SonicRelay.Domain.Sessions;
using SonicRelay.Domain.Users;
using SonicRelay.Infrastructure.Persistence;

namespace SonicRelay.Api.Endpoints;

public static class SignalingWebSocketEndpoint
{
    private const int MaxMessageBytes = 64 * 1024;
    private static readonly HashSet<string> RoutedMessageTypes =
    [
        "publisher.ready", "viewer.ready", "webrtc.offer", "webrtc.answer",
        "webrtc.ice_candidate", "pong"
    ];

    public static IEndpointRouteBuilder MapSignalingWebSocketEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet("/ws/signaling", HandleAsync)
            .RequireAuthorization("AuthenticatedUser");
        return app;
    }

    private static async Task HandleAsync(HttpContext context, UserManager<ApplicationUser> userManager,
        AppDbContext db, IConnectionRegistry registry, ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("SonicRelay.Signaling");
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        if (!Guid.TryParse(context.Request.Query["sessionId"], out var sessionId)
            || !Guid.TryParse(context.Request.Query["deviceId"], out var deviceId))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var user = await userManager.GetUserAsync(context.User);
        if (user is null)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        var session = await db.StreamSessions.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == sessionId, context.RequestAborted);
        if (session is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        if (session.Status is SessionStatuses.Ended or SessionStatuses.Expired
            || session.CodeExpiresAt <= DateTimeOffset.UtcNow)
        {
            logger.LogInformation("Rejected signaling connection to terminal session {SessionId} with status {SessionStatus}",
                sessionId, session.Status);
            context.Response.StatusCode = StatusCodes.Status410Gone;
            return;
        }

        var participant = await db.SessionParticipants.SingleOrDefaultAsync(x =>
            x.SessionId == sessionId && x.UserId == user.Id && x.DeviceId == deviceId,
            context.RequestAborted);
        if (participant is null)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        var expectedType = participant.Role == ParticipantRoles.Publisher
            ? DeviceTypes.WindowsPublisher
            : DeviceTypes.FlutterViewer;
        var eligibility = await DeviceAccess.CheckAsync(db, deviceId, user.Id, expectedType, context.RequestAborted);
        if (eligibility == DeviceEligibility.Missing)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        if (eligibility == DeviceEligibility.Ineligible)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        using var socketSendLock = new SemaphoreSlim(1, 1);
        async Task SendFrameAsync(ReadOnlyMemory<byte> message, CancellationToken ct)
        {
            await socketSendLock.WaitAsync(ct);
            try
            {
                await socket.SendAsync(message, WebSocketMessageType.Text, true, ct);
            }
            finally
            {
                socketSendLock.Release();
            }
        }
        var connectionId = Guid.NewGuid().ToString("N");
        await registry.RegisterAsync(new ConnectionDescriptor(
            connectionId, sessionId, participant.Id, user.Id, deviceId, participant.Role,
            DateTimeOffset.UtcNow,
            SendFrameAsync),
            context.RequestAborted);

        participant.ConnectionId = connectionId;
        participant.Status = ParticipantStatuses.Connected;
        participant.LeftAt = null;
        await db.SaveChangesAsync(context.RequestAborted);
        logger.LogInformation(
            "Connected signaling participant {ParticipantId} to session {SessionId} with connection {ConnectionId}",
            participant.Id, sessionId, connectionId);

        try
        {
            await SendAsync(SendFrameAsync, new
            {
                type = "session.joined",
                participantId = participant.Id,
                participant.Role
            }, context.RequestAborted);
            await ReceiveLoopAsync(socket, SendFrameAsync, sessionId, participant.Id, db, registry, logger,
                context.RequestAborted);
        }
        finally
        {
            await registry.UnregisterAsync(connectionId, CancellationToken.None);
            await MarkDisconnectedAsync(db, participant.Id, connectionId);
            logger.LogInformation(
                "Disconnected signaling participant {ParticipantId} from session {SessionId} with connection {ConnectionId}",
                participant.Id, sessionId, connectionId);
            await BroadcastAsync(registry, sessionId, participant.Id, new
            {
                type = "session.left",
                participantId = participant.Id
            }, CancellationToken.None);

            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                try
                {
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "signaling closed", CancellationToken.None);
                }
                catch (WebSocketException)
                {
                    // The peer disconnected before completing the close handshake.
                }
            }
        }
    }

    private static async Task ReceiveLoopAsync(WebSocket socket,
        Func<ReadOnlyMemory<byte>, CancellationToken, Task> sendAsync, Guid sessionId, Guid participantId,
        AppDbContext db, IConnectionRegistry registry, ILogger logger, CancellationToken ct)
    {
        while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            using var receiveCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var receiveTask = ReceiveMessageAsync(socket, receiveCancellation.Token);
            while (!receiveTask.IsCompleted)
            {
                var completed = await Task.WhenAny(receiveTask, Task.Delay(TimeSpan.FromSeconds(1), ct));
                if (completed != receiveTask && await SessionEndedAsync(db, sessionId, ct))
                {
                    await SendAsync(sendAsync, new { type = "session.ended" }, ct);
                    await receiveCancellation.CancelAsync();
                    try { await receiveTask; } catch (OperationCanceledException) { }
                    return;
                }
            }

            var message = await receiveTask;
            if (message is null) return;
            if (!await HandleMessageAsync(sendAsync, sessionId, participantId, message, db, registry, logger, ct))
                return;
        }
    }

    private static async Task<bool> HandleMessageAsync(Func<ReadOnlyMemory<byte>, CancellationToken, Task> sendAsync,
        Guid sessionId, Guid participantId, byte[] message, AppDbContext db, IConnectionRegistry registry, ILogger logger,
        CancellationToken ct)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(message);
        }
        catch (JsonException)
        {
            await SendErrorAsync(sendAsync, "invalid_message", ct);
            return true;
        }

        using (document)
        {
            var root = document.RootElement;
            if (!root.TryGetProperty("type", out var typeElement) || typeElement.ValueKind != JsonValueKind.String)
            {
                await SendErrorAsync(sendAsync, "invalid_message", ct);
                return true;
            }

            var type = typeElement.GetString()!;
            if (type == "ping")
            {
                await SendAsync(sendAsync, new { type = "pong" }, ct);
                return true;
            }
            if (!RoutedMessageTypes.Contains(type))
            {
                await SendErrorAsync(sendAsync, "unsupported_message_type", ct);
                return true;
            }
            if (!root.TryGetProperty("to", out var toElement) || !toElement.TryGetGuid(out var toParticipantId))
            {
                await SendErrorAsync(sendAsync, "invalid_recipient", ct);
                return true;
            }

            if (await SessionEndedAsync(db, sessionId, ct))
            {
                logger.LogInformation("Closing signaling for terminal session {SessionId} and participant {ParticipantId}",
                    sessionId, participantId);
                await SendAsync(sendAsync, new { type = "session.ended" }, ct);
                return false;
            }

            var payload = root.TryGetProperty("payload", out var payloadElement)
                ? payloadElement.Clone()
                : (JsonElement?)null;
            var routed = JsonSerializer.SerializeToUtf8Bytes(new
            {
                type,
                from = participantId,
                to = toParticipantId,
                payload
            });
            var delivered = await registry.SendToParticipantAsync(sessionId, toParticipantId, routed, ct);
            if (!delivered)
            {
                await SendErrorAsync(sendAsync, "participant_not_found", ct);
                return true;
            }

            // Deliberately log only routing metadata. SDP and ICE payloads are never logged.
            logger.LogDebug("Routed signaling message {MessageType} in session {SessionId} from {FromParticipantId} to {ToParticipantId}",
                type, sessionId, participantId, toParticipantId);
            return true;
        }
    }

    private static async Task<byte[]?> ReceiveMessageAsync(WebSocket socket, CancellationToken ct)
    {
        using var stream = new MemoryStream();
        var buffer = new byte[4096];
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            if (result.MessageType != WebSocketMessageType.Text)
                throw new WebSocketException(WebSocketError.InvalidMessageType);
            if (stream.Length + result.Count > MaxMessageBytes)
                throw new WebSocketException(WebSocketError.HeaderError, "Signaling message is too large.");
            await stream.WriteAsync(buffer.AsMemory(0, result.Count), ct);
            if (result.EndOfMessage) return stream.ToArray();
        }
    }

    private static async Task<bool> SessionEndedAsync(AppDbContext db, Guid sessionId, CancellationToken ct)
    {
        var state = await db.StreamSessions.AsNoTracking()
            .Where(x => x.Id == sessionId)
            .Select(x => new { x.Status, x.CodeExpiresAt })
            .SingleOrDefaultAsync(ct);
        return state is null
            || state.Status is SessionStatuses.Ended or SessionStatuses.Expired
            || state.CodeExpiresAt <= DateTimeOffset.UtcNow;
    }

    private static async Task MarkDisconnectedAsync(AppDbContext db, Guid participantId, string connectionId)
    {
        try
        {
            var participant = await db.SessionParticipants.SingleOrDefaultAsync(x => x.Id == participantId);
            if (participant?.ConnectionId != connectionId) return;
            participant.ConnectionId = null;
            participant.Status = ParticipantStatuses.Disconnected;
            participant.LeftAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }
        catch (Exception exception) when (exception is DbUpdateException or OperationCanceledException)
        {
            // Connection cleanup must not mask the socket termination.
        }
    }

    private static async Task BroadcastAsync(IConnectionRegistry registry, Guid sessionId, Guid excludedParticipantId,
        object message, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message);
        var connections = await registry.ListBySessionAsync(sessionId, ct);
        foreach (var connection in connections.Where(x => x.ParticipantId != excludedParticipantId))
        {
            try
            {
                await registry.SendToParticipantAsync(sessionId, connection.ParticipantId, bytes, ct);
            }
            catch (WebSocketException)
            {
                // A concurrent disconnect will be cleaned up by that connection's handler.
            }
        }
    }

    private static Task SendErrorAsync(Func<ReadOnlyMemory<byte>, CancellationToken, Task> sendAsync,
        string code, CancellationToken ct) =>
        SendAsync(sendAsync, new { type = "error", code }, ct);

    private static Task SendAsync(Func<ReadOnlyMemory<byte>, CancellationToken, Task> sendAsync,
        object message, CancellationToken ct) => sendAsync(JsonSerializer.SerializeToUtf8Bytes(message), ct);
}
