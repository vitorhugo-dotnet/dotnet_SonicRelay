using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SonicRelay.Domain.Devices;
using SonicRelay.Domain.Sessions;
using SonicRelay.Infrastructure.Persistence;
using Xunit;

namespace SonicRelay.Api.IntegrationTests;

public sealed class SignalingWebSocketTests : IClassFixture<SonicRelayApiFactory>
{
    private const string Password = "Valid1!Password";
    private readonly SonicRelayApiFactory _factory;

    public SignalingWebSocketTests(SonicRelayApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Signaling_rejects_an_unauthenticated_websocket_upgrade()
    {
        var client = _factory.Server.CreateWebSocketClient();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => client.ConnectAsync(
            new Uri($"ws://localhost/ws/signaling?sessionId={Guid.NewGuid()}&deviceId={Guid.NewGuid()}"),
            CancellationToken.None));

        Assert.Contains("401", exception.Message);
    }

    [Fact]
    public async Task Signaling_does_not_route_to_a_participant_in_another_session()
    {
        var sender = await CreateParticipantAsync("sender");
        var receiver = await CreateParticipantAsync("receiver");
        using var senderSocket = await ConnectAsync(sender);
        using var receiverSocket = await ConnectAsync(receiver);
        await ReceiveAsync(senderSocket);
        await ReceiveAsync(receiverSocket);

        await SendAsync(senderSocket, new
        {
            type = "webrtc.offer",
            to = receiver.ParticipantId,
            payload = new { sdp = "sensitive-test-sdp" }
        });

        var error = await ReceiveAsync(senderSocket);
        Assert.Equal("error", error.GetProperty("type").GetString());
        Assert.Equal("participant_not_found", error.GetProperty("payload").GetProperty("code").GetString());

        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ReceiveAsync(receiverSocket, timeout.Token));
    }

    [Fact]
    public async Task Signaling_rejects_an_invalid_session_or_device()
    {
        var participant = await CreateParticipantAsync("invalid-admission");
        var client = _factory.Server.CreateWebSocketClient();
        client.ConfigureRequest = request => request.Headers.Authorization = $"Bearer {participant.AccessToken}";

        var missingSession = await Assert.ThrowsAsync<InvalidOperationException>(() => client.ConnectAsync(
            new Uri($"ws://localhost/ws/signaling?sessionId={Guid.NewGuid()}&deviceId={participant.DeviceId}"),
            CancellationToken.None));
        Assert.Contains("404", missingSession.Message);

        var missingDevice = await Assert.ThrowsAsync<InvalidOperationException>(() => client.ConnectAsync(
            new Uri($"ws://localhost/ws/signaling?sessionId={participant.SessionId}&deviceId={Guid.NewGuid()}"),
            CancellationToken.None));
        Assert.Contains("403", missingDevice.Message);
    }

    [Fact]
    public async Task Signaling_normalizes_the_envelope_and_overwrites_client_metadata()
    {
        var participant = await CreateParticipantAsync("normalized");
        using var socket = await ConnectAsync(participant);
        var joined = await ReceiveAsync(socket);
        AssertEnvelope(joined, "session.joined", participant.SessionId);

        var messageId = Guid.NewGuid();
        await SendAsync(socket, new
        {
            type = "webrtc.offer",
            messageId,
            sessionId = Guid.NewGuid(),
            from = Guid.NewGuid(),
            to = participant.ParticipantId,
            timestamp = DateTimeOffset.UnixEpoch,
            payload = new { sdp = "opaque-sdp" }
        });

        var routed = await ReceiveAsync(socket);
        AssertEnvelope(routed, "webrtc.offer", participant.SessionId);
        Assert.Equal(messageId, routed.GetProperty("messageId").GetGuid());
        Assert.Equal(participant.ParticipantId, routed.GetProperty("from").GetGuid());
        Assert.Equal(participant.ParticipantId, routed.GetProperty("to").GetGuid());
        Assert.Equal("opaque-sdp", routed.GetProperty("payload").GetProperty("sdp").GetString());
        Assert.NotEqual(DateTimeOffset.UnixEpoch, routed.GetProperty("timestamp").GetDateTimeOffset());
    }

    [Fact]
    public async Task Signaling_announces_a_new_participant_to_existing_session_peers()
    {
        var publisher = await CreateParticipantAsync("join-announcement-publisher");
        var viewer = await CreateViewerAsync(publisher, "join-announcement-viewer");
        using var publisherSocket = await ConnectAsync(publisher);
        await ReceiveAsync(publisherSocket);

        using var viewerSocket = await ConnectAsync(viewer);
        var viewerJoined = await ReceiveAsync(viewerSocket);
        Assert.Equal(viewer.ParticipantId, viewerJoined.GetProperty("payload").GetProperty("participantId").GetGuid());
        Assert.Equal(ParticipantRoles.Viewer, viewerJoined.GetProperty("payload").GetProperty("role").GetString());

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var announced = await ReceiveAsync(publisherSocket, timeout.Token);
        AssertEnvelope(announced, "session.joined", publisher.SessionId);
        Assert.Equal(viewer.ParticipantId, announced.GetProperty("from").GetGuid());
        Assert.Equal(publisher.ParticipantId, announced.GetProperty("to").GetGuid());
        Assert.Equal(viewer.ParticipantId, announced.GetProperty("payload").GetProperty("participantId").GetGuid());
        Assert.Equal(ParticipantRoles.Viewer, announced.GetProperty("payload").GetProperty("role").GetString());
    }

    [Fact]
    public async Task Signaling_reconnecting_within_the_grace_period_reports_reconnection_not_departure()
    {
        await using var factory = new SonicRelayApiFactory(new Dictionary<string, string?>
        {
            ["Sessions:ParticipantDisconnectGraceSeconds"] = "10"
        });
        var publisher = await CreateParticipantAsync("grace-reconnect-publisher", factory);
        var viewer = await CreateViewerAsync(publisher, "grace-reconnect-viewer", factory);
        using var publisherSocket = await ConnectAsync(publisher, factory);
        await ReceiveAsync(publisherSocket);

        var viewerSocket = await ConnectAsync(viewer, factory);
        await ReceiveAsync(viewerSocket);
        var joinAnnouncement = await ReceiveAsync(publisherSocket);
        Assert.Equal("session.joined", joinAnnouncement.GetProperty("type").GetString());

        viewerSocket.Dispose();

        using var disconnectTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var disconnected = await ReceiveAsync(publisherSocket, disconnectTimeout.Token);
        AssertEnvelope(disconnected, "participant.disconnected", publisher.SessionId);
        Assert.Equal(viewer.ParticipantId, disconnected.GetProperty("payload").GetProperty("participantId").GetGuid());

        using var reconnectedViewerSocket = await ConnectAsync(viewer, factory);
        await ReceiveAsync(reconnectedViewerSocket);

        using var reconnectTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var reconnected = await ReceiveAsync(publisherSocket, reconnectTimeout.Token);
        AssertEnvelope(reconnected, "participant.reconnected", publisher.SessionId);
        Assert.Equal(viewer.ParticipantId, reconnected.GetProperty("payload").GetProperty("participantId").GetGuid());

        await using var assertScope = factory.Services.CreateAsyncScope();
        var db = assertScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var participantRow = await db.SessionParticipants.SingleAsync(x => x.Id == viewer.ParticipantId);
        Assert.Equal(ParticipantStatuses.Connected, participantRow.Status);
    }

    [Fact]
    public async Task Signaling_finalizes_as_left_after_the_grace_period_elapses_without_reconnecting()
    {
        await using var factory = new SonicRelayApiFactory(new Dictionary<string, string?>
        {
            ["Sessions:ParticipantDisconnectGraceSeconds"] = "1"
        });
        var publisher = await CreateParticipantAsync("grace-expiry-publisher", factory);
        var viewer = await CreateViewerAsync(publisher, "grace-expiry-viewer", factory);
        using var publisherSocket = await ConnectAsync(publisher, factory);
        await ReceiveAsync(publisherSocket);

        var viewerSocket = await ConnectAsync(viewer, factory);
        await ReceiveAsync(viewerSocket);
        await ReceiveAsync(publisherSocket); // session.joined announcement

        viewerSocket.Dispose();

        using var disconnectTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var disconnected = await ReceiveAsync(publisherSocket, disconnectTimeout.Token);
        AssertEnvelope(disconnected, "participant.disconnected", publisher.SessionId);

        using var leftTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var left = await ReceiveAsync(publisherSocket, leftTimeout.Token);
        AssertEnvelope(left, "session.left", publisher.SessionId);
        Assert.Equal(viewer.ParticipantId, left.GetProperty("payload").GetProperty("participantId").GetGuid());

        await using var assertScope = factory.Services.CreateAsyncScope();
        var db = assertScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var participantRow = await db.SessionParticipants.SingleAsync(x => x.Id == viewer.ParticipantId);
        Assert.Equal(ParticipantStatuses.Disconnected, participantRow.Status);
        Assert.NotNull(participantRow.LeftAt);
    }

    [Fact]
    public async Task Signaling_finalizes_a_participant_that_rejoined_over_http_but_never_reopened_the_socket()
    {
        await using var factory = new SonicRelayApiFactory(new Dictionary<string, string?>
        {
            ["Sessions:ParticipantDisconnectGraceSeconds"] = "1"
        });
        var publisher = await CreateParticipantAsync("http-rejoin-publisher", factory);
        var viewer = await CreateViewerAsync(publisher, "http-rejoin-viewer", factory);
        using var publisherSocket = await ConnectAsync(publisher, factory);
        await ReceiveAsync(publisherSocket);

        var viewerSocket = await ConnectAsync(viewer, factory);
        await ReceiveAsync(viewerSocket);
        await ReceiveAsync(publisherSocket); // session.joined announcement

        viewerSocket.Dispose();
        using var disconnectTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await ReceiveAsync(publisherSocket, disconnectTimeout.Token); // participant.disconnected

        // The documented full-reconnect flow calls POST /api/sessions/join before reopening the
        // WebSocket (SessionEndpoints.JoinAsync's existing-viewer branch sets Status back to
        // Connected without touching ConnectionId). Simulate that HTTP leg completing while the
        // client crashes before ever reopening the socket, so ConnectionId stays null.
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var participantRow = await db.SessionParticipants.SingleAsync(x => x.Id == viewer.ParticipantId);
            participantRow.Status = ParticipantStatuses.Connected;
            participantRow.LeftAt = null;
            await db.SaveChangesAsync();
        }

        // The grace timer must still finalize this participant once it expires — a null
        // ConnectionId is not a live socket that "claimed" the row.
        using var leftTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var left = await ReceiveAsync(publisherSocket, leftTimeout.Token);
        AssertEnvelope(left, "session.left", publisher.SessionId);
        Assert.Equal(viewer.ParticipantId, left.GetProperty("payload").GetProperty("participantId").GetGuid());

        await using var assertScope = factory.Services.CreateAsyncScope();
        var assertDb = assertScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var finalRow = await assertDb.SessionParticipants.SingleAsync(x => x.Id == viewer.ParticipantId);
        Assert.Equal(ParticipantStatuses.Disconnected, finalRow.Status);
    }

    [Theory]
    [InlineData("unsupported.type", "unsupported_message_type")]
    [InlineData("session.ended", "unsupported_message_type")]
    public async Task Signaling_returns_a_structured_error_for_unsupported_client_types(string type, string code)
    {
        var participant = await CreateParticipantAsync($"unsupported-{Guid.NewGuid():N}");
        using var socket = await ConnectAsync(participant);
        await ReceiveAsync(socket);

        await SendAsync(socket, new { type });

        var error = await ReceiveAsync(socket);
        AssertEnvelope(error, "error", participant.SessionId);
        Assert.Equal(participant.ParticipantId, error.GetProperty("to").GetGuid());
        Assert.Equal(code, error.GetProperty("payload").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Signaling_returns_a_structured_error_for_invalid_json()
    {
        var participant = await CreateParticipantAsync("invalid-json");
        using var socket = await ConnectAsync(participant);
        await ReceiveAsync(socket);

        await SendTextAsync(socket, "{not-json");

        var error = await ReceiveAsync(socket);
        AssertEnvelope(error, "error", participant.SessionId);
        Assert.Equal("invalid_message", error.GetProperty("payload").GetProperty("code").GetString());
    }

    [Theory]
    [InlineData(SessionStatuses.Ended, false)]
    [InlineData(SessionStatuses.Expired, false)]
    [InlineData(SessionStatuses.Active, true)]
    public async Task Signaling_rejects_terminal_or_elapsed_sessions(string status, bool elapsed)
    {
        var participant = await CreateParticipantAsync($"terminal-{status}-{elapsed}");
        await SetSessionStateAsync(participant.SessionId, status,
            elapsed ? DateTimeOffset.UtcNow.AddMinutes(-1) : DateTimeOffset.UtcNow.AddMinutes(5));
        var client = _factory.Server.CreateWebSocketClient();
        client.ConfigureRequest = request => request.Headers.Authorization = $"Bearer {participant.AccessToken}";

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => client.ConnectAsync(
            new Uri($"ws://localhost/ws/signaling?sessionId={participant.SessionId}&deviceId={participant.DeviceId}"),
            CancellationToken.None));

        Assert.Contains("410", exception.Message);
    }

    [Fact]
    public async Task Signaling_does_not_route_a_frame_after_the_session_ends()
    {
        var participant = await CreateParticipantAsync("terminal-route");
        using var socket = await ConnectAsync(participant);
        await ReceiveAsync(socket);
        await SetSessionStateAsync(participant.SessionId, SessionStatuses.Ended, DateTimeOffset.UtcNow.AddMinutes(5));

        await SendAsync(socket, new
        {
            type = "webrtc.offer",
            to = participant.ParticipantId,
            payload = new { sdp = "must-not-route" }
        });

        var response = await ReceiveAsync(socket);
        Assert.Equal("session.ended", response.GetProperty("type").GetString());
        var buffer = new byte[64];
        var close = await socket.ReceiveAsync(buffer, CancellationToken.None);
        Assert.Equal(WebSocketMessageType.Close, close.MessageType);
    }

    [Fact]
    public async Task Signaling_rejects_a_revoked_device_before_accepting_the_socket()
    {
        var participant = await CreateParticipantAsync("revoked");
        await SetDeviceRevokedAsync(participant.DeviceId);
        var client = _factory.Server.CreateWebSocketClient();
        client.ConfigureRequest = request => request.Headers.Authorization = $"Bearer {participant.AccessToken}";

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => client.ConnectAsync(
            new Uri($"ws://localhost/ws/signaling?sessionId={participant.SessionId}&deviceId={participant.DeviceId}"),
            CancellationToken.None));

        Assert.Contains("403", exception.Message);
    }

    private async Task<TestParticipant> CreateParticipantAsync(string prefix, SonicRelayApiFactory? factory = null)
    {
        factory ??= _factory;
        var http = factory.CreateClient();
        var email = $"ws-{prefix}-{Guid.NewGuid():N}@example.com";
        var register = await http.PostAsJsonAsync("/auth/register", new { email, password = Password });
        Assert.Equal(HttpStatusCode.OK, register.StatusCode);
        var login = await http.PostAsJsonAsync("/auth/login", new { email, password = Password });
        var tokens = await ReadJsonAsync(login);
        var accessToken = tokens.GetProperty("accessToken").GetString()!;

        var userId = await GetUserIdAsync(email, factory);
        var deviceId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var participantId = Guid.NewGuid();
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Devices.Add(new Device
        {
            Id = deviceId,
            OwnerUserId = userId,
            Name = $"{prefix} device",
            Type = DeviceTypes.WindowsPublisher,
            Platform = DevicePlatforms.Windows,
            CreatedAt = DateTimeOffset.UtcNow
        });
        db.StreamSessions.Add(new StreamSession
        {
            Id = sessionId,
            OwnerUserId = userId,
            SourceDeviceId = deviceId,
            Status = SessionStatuses.Active,
            MaxViewers = 1,
            CodeExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
            CreatedAt = DateTimeOffset.UtcNow
        });
        db.SessionParticipants.Add(new SessionParticipant
        {
            Id = participantId,
            SessionId = sessionId,
            UserId = userId,
            DeviceId = deviceId,
            Role = ParticipantRoles.Publisher,
            Status = ParticipantStatuses.Connected,
            JoinedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
        return new TestParticipant(accessToken, sessionId, deviceId, participantId);
    }

    private async Task<Guid> GetUserIdAsync(string email, SonicRelayApiFactory? factory = null)
    {
        factory ??= _factory;
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Users
            .Where(x => x.Email == email)
            .Select(x => x.Id)
            .SingleAsync();
    }

    private async Task<TestParticipant> CreateViewerAsync(TestParticipant publisher, string prefix,
        SonicRelayApiFactory? factory = null)
    {
        factory ??= _factory;
        var http = factory.CreateClient();
        var email = $"ws-{prefix}-{Guid.NewGuid():N}@example.com";
        var register = await http.PostAsJsonAsync("/auth/register", new { email, password = Password });
        Assert.Equal(HttpStatusCode.OK, register.StatusCode);
        var login = await http.PostAsJsonAsync("/auth/login", new { email, password = Password });
        var tokens = await ReadJsonAsync(login);
        var accessToken = tokens.GetProperty("accessToken").GetString()!;

        var userId = await GetUserIdAsync(email, factory);
        var deviceId = Guid.NewGuid();
        var participantId = Guid.NewGuid();
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Devices.Add(new Device
        {
            Id = deviceId,
            OwnerUserId = userId,
            Name = $"{prefix} device",
            Type = DeviceTypes.FlutterViewer,
            Platform = DevicePlatforms.Android,
            CreatedAt = DateTimeOffset.UtcNow
        });
        db.SessionParticipants.Add(new SessionParticipant
        {
            Id = participantId,
            SessionId = publisher.SessionId,
            UserId = userId,
            DeviceId = deviceId,
            Role = ParticipantRoles.Viewer,
            Status = ParticipantStatuses.Connected,
            JoinedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
        return new TestParticipant(accessToken, publisher.SessionId, deviceId, participantId);
    }

    private async Task<WebSocket> ConnectAsync(TestParticipant participant, SonicRelayApiFactory? factory = null)
    {
        var client = (factory ?? _factory).Server.CreateWebSocketClient();
        client.ConfigureRequest = request =>
            request.Headers.Authorization = $"Bearer {participant.AccessToken}";
        return await client.ConnectAsync(
            new Uri($"ws://localhost/ws/signaling?sessionId={participant.SessionId}&deviceId={participant.DeviceId}"),
            CancellationToken.None);
    }

    private async Task SetSessionStateAsync(Guid sessionId, string status, DateTimeOffset codeExpiresAt)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var session = await db.StreamSessions.SingleAsync(x => x.Id == sessionId);
        session.Status = status;
        session.CodeExpiresAt = codeExpiresAt;
        await db.SaveChangesAsync();
    }

    private async Task SetDeviceRevokedAsync(Guid deviceId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var device = await db.Devices.SingleAsync(x => x.Id == deviceId);
        device.Revoked = true;
        await db.SaveChangesAsync();
    }

    private static async Task SendAsync(WebSocket socket, object message)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message);
        await socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
    }

    private static async Task SendTextAsync(WebSocket socket, string message)
    {
        var bytes = Encoding.UTF8.GetBytes(message);
        await socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
    }

    private static void AssertEnvelope(JsonElement message, string type, Guid sessionId)
    {
        Assert.Equal(type, message.GetProperty("type").GetString());
        Assert.NotEqual(Guid.Empty, message.GetProperty("messageId").GetGuid());
        Assert.Equal(sessionId, message.GetProperty("sessionId").GetGuid());
        Assert.Equal(JsonValueKind.String, message.GetProperty("timestamp").ValueKind);
        Assert.True(message.TryGetProperty("from", out _));
        Assert.True(message.TryGetProperty("to", out _));
        Assert.True(message.TryGetProperty("payload", out _));
    }

    private static async Task<JsonElement> ReceiveAsync(WebSocket socket, CancellationToken ct = default)
    {
        var buffer = new byte[4096];
        var result = await socket.ReceiveAsync(buffer, ct);
        Assert.Equal(WebSocketMessageType.Text, result.MessageType);
        using var document = JsonDocument.Parse(buffer.AsMemory(0, result.Count));
        return document.RootElement.Clone();
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        return document.RootElement.Clone();
    }

    private sealed record TestParticipant(string AccessToken, Guid SessionId, Guid DeviceId, Guid ParticipantId);
}
