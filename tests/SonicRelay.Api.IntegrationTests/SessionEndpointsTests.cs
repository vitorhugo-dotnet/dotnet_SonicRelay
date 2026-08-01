using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SonicRelay.Api.Services;
using SonicRelay.Domain.Devices;
using SonicRelay.Domain.Sessions;
using SonicRelay.Infrastructure.Persistence;
using Xunit;

namespace SonicRelay.Api.IntegrationTests;

public sealed class SessionEndpointsTests : IClassFixture<SonicRelayApiFactory>
{
    private readonly SonicRelayApiFactory _factory;

    public SessionEndpointsTests(SonicRelayApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Create_from_a_publisher_device_creates_a_session_and_adds_it_as_publisher()
    {
        var (client, deviceId) = await BootstrapAsync(DeviceTypes.WindowsPublisher, DevicePlatforms.Windows);

        var response = await client.PostAsJsonAsync("/api/sessions", new { maxViewers = 2 });
        var body = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Matches("^[A-Z0-9]{6}$", body.GetProperty("code").GetString()!);
        Assert.Equal(deviceId, body.GetProperty("sourceDeviceId").GetGuid());
        var sessionId = body.GetProperty("id").GetGuid();
        await using var scope = _factory.Services.CreateAsyncScope();
        var participant = await scope.ServiceProvider.GetRequiredService<AppDbContext>().SessionParticipants.SingleAsync(x => x.SessionId == sessionId);
        Assert.Equal(ParticipantRoles.Publisher, participant.Role);
        Assert.Equal(deviceId, participant.DeviceId);
    }

    [Fact]
    public async Task Create_rejects_a_viewer_device_that_lacks_the_session_create_scope()
    {
        var (client, _) = await BootstrapAsync(DeviceTypes.FlutterViewer, DevicePlatforms.Android);

        var response = await client.PostAsJsonAsync("/api/sessions", new { maxViewers = 2 });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Create_rejects_a_revoked_publisher_device_even_with_a_still_unexpired_token()
    {
        var (client, deviceId) = await BootstrapAsync(DeviceTypes.WindowsPublisher, DevicePlatforms.Windows);
        await RevokeDeviceAsync(deviceId);

        var response = await client.PostAsJsonAsync("/api/sessions", new { maxViewers = 2 });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Join_from_a_viewer_device_validates_the_code_and_adds_it_as_viewer()
    {
        var (_, sessionId, code) = await CreateSessionAsync();
        var (viewerClient, viewerDeviceId) = await BootstrapAsync(DeviceTypes.FlutterViewer, DevicePlatforms.Android);

        var response = await viewerClient.PostAsJsonAsync("/api/sessions/join", new { code });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using var scope = _factory.Services.CreateAsyncScope();
        var participant = await scope.ServiceProvider.GetRequiredService<AppDbContext>().SessionParticipants
            .SingleAsync(x => x.SessionId == sessionId && x.Role == ParticipantRoles.Viewer);
        Assert.Equal(viewerDeviceId, participant.DeviceId);
    }

    [Fact]
    public async Task Join_rejects_a_publisher_device_that_lacks_the_session_join_scope()
    {
        var (_, _, code) = await CreateSessionAsync();
        var (publisherClient, _) = await BootstrapAsync(DeviceTypes.WindowsPublisher, DevicePlatforms.Windows);

        var response = await publisherClient.PostAsJsonAsync("/api/sessions/join", new { code });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Join_rejects_a_revoked_viewer_device_even_with_a_still_unexpired_token()
    {
        var (_, _, code) = await CreateSessionAsync();
        var (viewerClient, viewerDeviceId) = await BootstrapAsync(DeviceTypes.FlutterViewer, DevicePlatforms.Android);
        await RevokeDeviceAsync(viewerDeviceId);

        var response = await viewerClient.PostAsJsonAsync("/api/sessions/join", new { code });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Wrong_and_expired_codes_have_the_same_response()
    {
        var (_, sessionId, code) = await CreateSessionAsync();
        var (viewerClient, _) = await BootstrapAsync(DeviceTypes.FlutterViewer, DevicePlatforms.Android);
        await SetSessionExpiryAsync(sessionId, DateTimeOffset.UtcNow.AddMinutes(-1));

        var expired = await viewerClient.PostAsJsonAsync("/api/sessions/join", new { code });
        var wrong = await viewerClient.PostAsJsonAsync("/api/sessions/join", new { code = "ZZZZZZ" });

        Assert.Equal(HttpStatusCode.NotFound, expired.StatusCode);
        Assert.Equal(wrong.StatusCode, expired.StatusCode);
        Assert.Equal(await wrong.Content.ReadAsStringAsync(), await expired.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Rotate_invalidates_old_code_and_returns_a_new_code()
    {
        var (ownerClient, sessionId, oldCode) = await CreateSessionAsync();

        var rotated = await ownerClient.PostAsync($"/api/sessions/{sessionId}/rotate-code", null);
        var body = await ReadJsonAsync(rotated);
        var newCode = body.GetProperty("code").GetString()!;

        Assert.Equal(HttpStatusCode.OK, rotated.StatusCode);
        Assert.Matches("^[A-Z0-9]{6}$", newCode);
        Assert.NotEqual(oldCode, newCode);

        var (viewerClient, _) = await BootstrapAsync(DeviceTypes.FlutterViewer, DevicePlatforms.Android);
        var oldJoin = await viewerClient.PostAsJsonAsync("/api/sessions/join", new { code = oldCode });
        var newJoin = await viewerClient.PostAsJsonAsync("/api/sessions/join", new { code = newCode });
        Assert.Equal(HttpStatusCode.NotFound, oldJoin.StatusCode);
        Assert.Equal(HttpStatusCode.OK, newJoin.StatusCode);
    }

    [Fact]
    public async Task Join_rejects_viewers_beyond_the_limit()
    {
        var (_, _, code) = await CreateSessionAsync(maxViewers: 1);
        var (firstClient, _) = await BootstrapAsync(DeviceTypes.FlutterViewer, DevicePlatforms.Android);
        var (secondClient, _) = await BootstrapAsync(DeviceTypes.FlutterViewer, DevicePlatforms.Android);

        var accepted = await firstClient.PostAsJsonAsync("/api/sessions/join", new { code });
        var rejected = await secondClient.PostAsJsonAsync("/api/sessions/join", new { code });

        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);
    }

    [Fact]
    public async Task Join_rejects_a_second_viewer_while_the_only_slot_is_mid_reconnect_grace_period()
    {
        var (_, sessionId, code) = await CreateSessionAsync(maxViewers: 1);
        var (firstClient, _) = await BootstrapAsync(DeviceTypes.FlutterViewer, DevicePlatforms.Android);
        var joined = await firstClient.PostAsJsonAsync("/api/sessions/join", new { code });
        Assert.Equal(HttpStatusCode.OK, joined.StatusCode);

        // Simulate the first viewer's WebSocket dropping mid-session: the signaling endpoint
        // moves it to Reconnecting (not Disconnected) while the backend's grace period runs.
        // It must still hold its slot, otherwise a second viewer could take it here and the
        // first one could then also reconnect, leaving two viewers in a maxViewers=1 session.
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var participant = await db.SessionParticipants
                .SingleAsync(x => x.SessionId == sessionId && x.Role == ParticipantRoles.Viewer);
            participant.Status = ParticipantStatuses.Reconnecting;
            participant.ConnectionId = null;
            await db.SaveChangesAsync();
        }

        var (secondClient, _) = await BootstrapAsync(DeviceTypes.FlutterViewer, DevicePlatforms.Android);
        var rejected = await secondClient.PostAsJsonAsync("/api/sessions/join", new { code });

        Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);
    }

    [Fact]
    public async Task Create_is_rate_limited_by_ip_regardless_of_which_device_calls()
    {
        await using var factory = new SonicRelayApiFactory(new Dictionary<string, string?>
        {
            ["RateLimits:CreateSession:PermitLimit"] = "1"
        });
        var (firstClient, _) = await BootstrapAsync(DeviceTypes.WindowsPublisher, DevicePlatforms.Windows, factory);
        var (secondClient, _) = await BootstrapAsync(DeviceTypes.WindowsPublisher, DevicePlatforms.Windows, factory);

        var accepted = await firstClient.PostAsJsonAsync("/api/sessions", new { maxViewers = 2 });
        // create-session is IP-keyed (DeviceBearer tokens carry no ClaimTypes.NameIdentifier, so a
        // per-user limiter would silently fall back to IP anyway) — a second, unrelated device
        // hitting the same test host shares the same quota.
        var rejected = await secondClient.PostAsJsonAsync("/api/sessions", new { maxViewers = 2 });

        Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
    }

    [Fact]
    public async Task Join_is_rate_limited_by_ip_regardless_of_which_device_calls()
    {
        await using var factory = new SonicRelayApiFactory(new Dictionary<string, string?>
        {
            ["RateLimits:JoinSession:PermitLimit"] = "1"
        });
        var (_, _, code) = await CreateSessionAsync(factory: factory, maxViewers: 3);
        var (firstClient, _) = await BootstrapAsync(DeviceTypes.FlutterViewer, DevicePlatforms.Android, factory);
        var (secondClient, _) = await BootstrapAsync(DeviceTypes.FlutterViewer, DevicePlatforms.Android, factory);

        var accepted = await firstClient.PostAsJsonAsync("/api/sessions/join", new { code });
        var rejected = await secondClient.PostAsJsonAsync("/api/sessions/join", new { code });

        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
    }

    [Fact]
    public async Task Rotate_code_is_rate_limited()
    {
        await using var factory = new SonicRelayApiFactory(new Dictionary<string, string?>
        {
            ["RateLimits:RotateCode:PermitLimit"] = "1"
        });
        var (ownerClient, sessionId, _) = await CreateSessionAsync(factory);

        var accepted = await ownerClient.PostAsync($"/api/sessions/{sessionId}/rotate-code", null);
        var rejected = await ownerClient.PostAsync($"/api/sessions/{sessionId}/rotate-code", null);

        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
    }

    [Fact]
    public async Task Cleanup_expires_sessions_and_removes_only_stale_disconnected_participants()
    {
        await using var factory = new SonicRelayApiFactory(new Dictionary<string, string?>
        {
            ["Sessions:DisconnectedParticipantRetentionHours"] = "24"
        });
        var (_, sessionId, _) = await CreateSessionAsync(factory);
        var staleId = Guid.NewGuid();
        var recentId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await using (var seedScope = factory.Services.CreateAsyncScope())
        {
            var db = seedScope.ServiceProvider.GetRequiredService<AppDbContext>();
            var session = await db.StreamSessions.SingleAsync(x => x.Id == sessionId);
            session.CodeExpiresAt = now.AddMinutes(-1);
            db.SessionParticipants.AddRange(
                new SessionParticipant
                {
                    Id = staleId,
                    SessionId = sessionId,
                    DeviceId = session.SourceDeviceId,
                    Role = ParticipantRoles.Viewer,
                    Status = ParticipantStatuses.Disconnected,
                    JoinedAt = now.AddDays(-3),
                    LeftAt = now.AddDays(-2)
                },
                new SessionParticipant
                {
                    Id = recentId,
                    SessionId = sessionId,
                    DeviceId = session.SourceDeviceId,
                    Role = ParticipantRoles.Viewer,
                    Status = ParticipantStatuses.Disconnected,
                    JoinedAt = now.AddHours(-2),
                    LeftAt = now.AddHours(-1)
                });
            await db.SaveChangesAsync();
        }

        await factory.Services.GetRequiredService<SessionCleanupService>().CleanupOnceAsync(CancellationToken.None);

        await using var assertScope = factory.Services.CreateAsyncScope();
        var assertDb = assertScope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(SessionStatuses.Expired,
            (await assertDb.StreamSessions.SingleAsync(x => x.Id == sessionId)).Status);
        Assert.False(await assertDb.SessionParticipants.AnyAsync(x => x.Id == staleId));
        Assert.True(await assertDb.SessionParticipants.AnyAsync(x => x.Id == recentId));
        Assert.True(await assertDb.SessionParticipants.AnyAsync(x => x.SessionId == sessionId
            && x.Status == ParticipantStatuses.Connected));
    }

    [Fact]
    public async Task Owner_can_list_get_and_end_a_session()
    {
        var (ownerClient, sessionId, _) = await CreateSessionAsync();

        var active = await ownerClient.GetFromJsonAsync<JsonElement>("/api/sessions/active");
        Assert.Contains(active.EnumerateArray(), x => x.GetProperty("id").GetGuid() == sessionId);
        var get = await ownerClient.GetAsync($"/api/sessions/{sessionId}");
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);

        var end = await ownerClient.PostAsync($"/api/sessions/{sessionId}/end", null);
        Assert.Equal(HttpStatusCode.OK, end.StatusCode);
        var ended = await ReadJsonAsync(end);
        Assert.Equal(SessionStatuses.Ended, ended.GetProperty("status").GetString());
    }

    [Fact]
    public async Task A_viewer_device_cannot_end_or_rotate_a_session_it_does_not_own()
    {
        var (_, sessionId, _) = await CreateSessionAsync();
        var (viewerClient, _) = await BootstrapAsync(DeviceTypes.FlutterViewer, DevicePlatforms.Android);

        var end = await viewerClient.PostAsync($"/api/sessions/{sessionId}/end", null);
        var rotate = await viewerClient.PostAsync($"/api/sessions/{sessionId}/rotate-code", null);

        // Both routes require the "session:end" scope, which a viewer device's token never
        // carries (DeviceCredentialService only grants it to publishers) — so the policy itself
        // rejects the request with 403 before the handler's own ownership check ever runs.
        Assert.Equal(HttpStatusCode.Forbidden, end.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, rotate.StatusCode);
    }

    [Fact]
    public async Task A_publisher_device_cannot_end_or_rotate_another_publishers_session()
    {
        var (_, sessionId, _) = await CreateSessionAsync();
        var (otherPublisherClient, _) = await BootstrapAsync(DeviceTypes.WindowsPublisher, DevicePlatforms.Windows);

        var end = await otherPublisherClient.PostAsync($"/api/sessions/{sessionId}/end", null);
        var rotate = await otherPublisherClient.PostAsync($"/api/sessions/{sessionId}/rotate-code", null);

        // Unlike the viewer case above, this caller does hold the "session:end" scope, so the
        // policy check passes and this actually exercises SessionEndpoints' own ownership guard
        // (SourceDeviceId == device.Id) rather than being rejected before the handler runs.
        Assert.Equal(HttpStatusCode.NotFound, end.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, rotate.StatusCode);
    }

    [Fact]
    public async Task Two_independent_device_pairs_do_not_see_each_others_sessions()
    {
        var (firstOwnerClient, firstSessionId, _) = await CreateSessionAsync();
        var (secondOwnerClient, secondSessionId, _) = await CreateSessionAsync();

        var firstActive = await firstOwnerClient.GetFromJsonAsync<JsonElement>("/api/sessions/active");
        var secondActive = await secondOwnerClient.GetFromJsonAsync<JsonElement>("/api/sessions/active");

        Assert.Contains(firstActive.EnumerateArray(), x => x.GetProperty("id").GetGuid() == firstSessionId);
        Assert.DoesNotContain(firstActive.EnumerateArray(), x => x.GetProperty("id").GetGuid() == secondSessionId);
        Assert.Contains(secondActive.EnumerateArray(), x => x.GetProperty("id").GetGuid() == secondSessionId);
        Assert.DoesNotContain(secondActive.EnumerateArray(), x => x.GetProperty("id").GetGuid() == firstSessionId);

        var crossGet = await secondOwnerClient.GetAsync($"/api/sessions/{firstSessionId}");
        Assert.Equal(HttpStatusCode.NotFound, crossGet.StatusCode);
    }

    [Fact]
    public async Task Ending_a_session_finalizes_participants_mid_reconnect_grace_period()
    {
        var (ownerClient, sessionId, _) = await CreateSessionAsync();
        var participantId = Guid.NewGuid();
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var session = await db.StreamSessions.SingleAsync(x => x.Id == sessionId);
            db.SessionParticipants.Add(new SessionParticipant
            {
                Id = participantId,
                SessionId = sessionId,
                DeviceId = session.SourceDeviceId,
                Role = ParticipantRoles.Viewer,
                ConnectionId = null,
                Status = ParticipantStatuses.Reconnecting,
                JoinedAt = DateTimeOffset.UtcNow.AddSeconds(-5)
            });
            await db.SaveChangesAsync();
        }

        var end = await ownerClient.PostAsync($"/api/sessions/{sessionId}/end", null);
        Assert.Equal(HttpStatusCode.OK, end.StatusCode);

        await using var assertScope = _factory.Services.CreateAsyncScope();
        var assertDb = assertScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var participant = await assertDb.SessionParticipants.SingleAsync(x => x.Id == participantId);
        Assert.Equal(ParticipantStatuses.Disconnected, participant.Status);
        Assert.NotNull(participant.LeftAt);
    }

    private async Task<(HttpClient Client, Guid DeviceId)> BootstrapAsync(string deviceType, string platform,
        SonicRelayApiFactory? factory = null)
    {
        var client = (factory ?? _factory).CreateClient();
        var session = await DeviceIdentityTestHelper.BootstrapAndAuthorizeAsync(client, deviceType, platform);
        return (client, session.DeviceId);
    }

    private async Task<(HttpClient Owner, Guid SessionId, string Code)> CreateSessionAsync(
        SonicRelayApiFactory? factory = null, int maxViewers = 2)
    {
        var (client, _) = await BootstrapAsync(DeviceTypes.WindowsPublisher, DevicePlatforms.Windows, factory);
        var response = await client.PostAsJsonAsync("/api/sessions", new { maxViewers });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await ReadJsonAsync(response);
        return (client, body.GetProperty("id").GetGuid(), body.GetProperty("code").GetString()!);
    }

    private async Task RevokeDeviceAsync(Guid deviceId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var device = await db.DeviceIdentities.SingleAsync(x => x.Id == deviceId);
        device.Status = SonicRelay.Domain.DeviceIdentities.DeviceIdentityStatuses.Revoked;
        await db.SaveChangesAsync();
    }

    private async Task SetSessionExpiryAsync(Guid sessionId, DateTimeOffset expiry)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var session = await db.StreamSessions.SingleAsync(x => x.Id == sessionId);
        session.CodeExpiresAt = expiry;
        await db.SaveChangesAsync();
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        return document.RootElement.Clone();
    }
}
