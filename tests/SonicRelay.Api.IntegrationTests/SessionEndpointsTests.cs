using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SonicRelay.Api.Services;
using SonicRelay.Application.Abstractions;
using SonicRelay.Domain.Devices;
using SonicRelay.Domain.Sessions;
using SonicRelay.Infrastructure.Persistence;
using Xunit;

namespace SonicRelay.Api.IntegrationTests;

public sealed class SessionEndpointsTests : IClassFixture<SonicRelayApiFactory>
{
    private const string Password = "Valid1!Password";
    private readonly SonicRelayApiFactory _factory;

    public SessionEndpointsTests(SonicRelayApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Create_requires_an_owned_non_revoked_device_and_adds_publisher()
    {
        var owner = await CreateUserAsync("create-owner");
        var source = await AddDeviceAsync(owner.UserId, revoked: false, DeviceTypes.WindowsPublisher);

        var response = await owner.Client.PostAsJsonAsync("/api/sessions", new { sourceDeviceId = source, maxViewers = 2 });
        var body = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Matches("^[A-Z0-9]{6}$", body.GetProperty("code").GetString()!);
        var sessionId = body.GetProperty("id").GetGuid();
        await using var scope = _factory.Services.CreateAsyncScope();
        var participant = await scope.ServiceProvider.GetRequiredService<AppDbContext>().SessionParticipants.SingleAsync(x => x.SessionId == sessionId);
        Assert.Equal(ParticipantRoles.Publisher, participant.Role);
        Assert.Equal(source, participant.DeviceId);

        var other = await CreateUserAsync("create-other");
        var foreign = await other.Client.PostAsJsonAsync("/api/sessions", new { sourceDeviceId = source, maxViewers = 2 });
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);

        var revoked = await AddDeviceAsync(owner.UserId, revoked: true, DeviceTypes.WindowsPublisher);
        var revokedResponse = await owner.Client.PostAsJsonAsync("/api/sessions", new { sourceDeviceId = revoked, maxViewers = 2 });
        Assert.Equal(HttpStatusCode.Forbidden, revokedResponse.StatusCode);

        var viewerDevice = await AddDeviceAsync(owner.UserId, revoked: false, DeviceTypes.FlutterViewer);
        var wrongType = await owner.Client.PostAsJsonAsync("/api/sessions", new { sourceDeviceId = viewerDevice, maxViewers = 2 });
        Assert.Equal(HttpStatusCode.Forbidden, wrongType.StatusCode);
    }

    [Fact]
    public async Task Join_validates_viewer_ownership_and_adds_viewer()
    {
        var (owner, sessionId, code) = await CreateSessionAsync("join-owner", 2);
        var viewer = await CreateUserAsync("join-viewer");
        var viewerDevice = await AddDeviceAsync(viewer.UserId, false, DeviceTypes.FlutterViewer);

        var response = await viewer.Client.PostAsJsonAsync("/api/sessions/join", new { code, deviceId = viewerDevice });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using var scope = _factory.Services.CreateAsyncScope();
        var participant = await scope.ServiceProvider.GetRequiredService<AppDbContext>().SessionParticipants
            .SingleAsync(x => x.SessionId == sessionId && x.Role == ParticipantRoles.Viewer);
        Assert.Equal(viewer.UserId, participant.UserId);

        var foreignDevice = await AddDeviceAsync(owner.UserId, false, DeviceTypes.FlutterViewer);
        var foreign = await viewer.Client.PostAsJsonAsync("/api/sessions/join", new { code, deviceId = foreignDevice });
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);

        var revokedDevice = await AddDeviceAsync(viewer.UserId, true, DeviceTypes.FlutterViewer);
        var revoked = await viewer.Client.PostAsJsonAsync("/api/sessions/join", new { code, deviceId = revokedDevice });
        Assert.Equal(HttpStatusCode.Forbidden, revoked.StatusCode);

        var publisherDevice = await AddDeviceAsync(viewer.UserId, false, DeviceTypes.WindowsPublisher);
        var wrongType = await viewer.Client.PostAsJsonAsync("/api/sessions/join", new { code, deviceId = publisherDevice });
        Assert.Equal(HttpStatusCode.Forbidden, wrongType.StatusCode);
    }

    [Fact]
    public async Task Wrong_and_expired_codes_have_the_same_response()
    {
        var (_, sessionId, code) = await CreateSessionAsync("expiry-owner", 2);
        var viewer = await CreateUserAsync("expiry-viewer");
        var device = await AddDeviceAsync(viewer.UserId, false, DeviceTypes.FlutterViewer);
        await SetSessionExpiryAsync(sessionId, DateTimeOffset.UtcNow.AddMinutes(-1));

        var expired = await viewer.Client.PostAsJsonAsync("/api/sessions/join", new { code, deviceId = device });
        var wrong = await viewer.Client.PostAsJsonAsync("/api/sessions/join", new { code = "ZZZZZZ", deviceId = device });

        Assert.Equal(HttpStatusCode.NotFound, expired.StatusCode);
        Assert.Equal(wrong.StatusCode, expired.StatusCode);
        Assert.Equal(await wrong.Content.ReadAsStringAsync(), await expired.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Rotate_invalidates_old_code_and_returns_a_new_code()
    {
        var (owner, sessionId, oldCode) = await CreateSessionAsync("rotate-owner", 2);

        var rotated = await owner.Client.PostAsync($"/api/sessions/{sessionId}/rotate-code", null);
        var body = await ReadJsonAsync(rotated);
        var newCode = body.GetProperty("code").GetString()!;

        Assert.Equal(HttpStatusCode.OK, rotated.StatusCode);
        Assert.Matches("^[A-Z0-9]{6}$", newCode);
        Assert.NotEqual(oldCode, newCode);

        var viewer = await CreateUserAsync("rotate-viewer");
        var device = await AddDeviceAsync(viewer.UserId, false, DeviceTypes.FlutterViewer);
        var oldJoin = await viewer.Client.PostAsJsonAsync("/api/sessions/join", new { code = oldCode, deviceId = device });
        var newJoin = await viewer.Client.PostAsJsonAsync("/api/sessions/join", new { code = newCode, deviceId = device });
        Assert.Equal(HttpStatusCode.NotFound, oldJoin.StatusCode);
        Assert.Equal(HttpStatusCode.OK, newJoin.StatusCode);
    }

    [Fact]
    public async Task Join_rejects_viewers_beyond_the_limit()
    {
        var (_, _, code) = await CreateSessionAsync("limit-owner", 1);
        var first = await CreateUserAsync("limit-first");
        var second = await CreateUserAsync("limit-second");
        var firstDevice = await AddDeviceAsync(first.UserId, false, DeviceTypes.FlutterViewer);
        var secondDevice = await AddDeviceAsync(second.UserId, false, DeviceTypes.FlutterViewer);

        var accepted = await first.Client.PostAsJsonAsync("/api/sessions/join", new { code, deviceId = firstDevice });
        var rejected = await second.Client.PostAsJsonAsync("/api/sessions/join", new { code, deviceId = secondDevice });

        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);
    }

    [Fact]
    public async Task Create_is_rate_limited_per_user()
    {
        await using var factory = new SonicRelayApiFactory(new Dictionary<string, string?>
        {
            ["RateLimits:CreateSession:PermitLimit"] = "1"
        });
        var owner = await CreateUserAsync("limited-create", factory);
        var firstDevice = await AddDeviceAsync(owner.UserId, false, DeviceTypes.WindowsPublisher, factory);
        var secondDevice = await AddDeviceAsync(owner.UserId, false, DeviceTypes.WindowsPublisher, factory);

        var accepted = await owner.Client.PostAsJsonAsync("/api/sessions", new { sourceDeviceId = firstDevice, maxViewers = 2 });
        var rejected = await owner.Client.PostAsJsonAsync("/api/sessions", new { sourceDeviceId = secondDevice, maxViewers = 2 });

        Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);

        var other = await CreateUserAsync("limited-create-other", factory);
        var otherDevice = await AddDeviceAsync(other.UserId, false, DeviceTypes.WindowsPublisher, factory);
        Assert.Equal(HttpStatusCode.Created,
            (await other.Client.PostAsJsonAsync("/api/sessions", new { sourceDeviceId = otherDevice, maxViewers = 2 })).StatusCode);
    }

    [Fact]
    public async Task Join_is_rate_limited_per_user()
    {
        await using var factory = new SonicRelayApiFactory(new Dictionary<string, string?>
        {
            ["RateLimits:JoinSession:PermitLimit"] = "1"
        });
        var (_, _, code) = await CreateSessionAsync("limited-join-owner", 3, factory);
        var viewer = await CreateUserAsync("limited-join", factory);
        var firstDevice = await AddDeviceAsync(viewer.UserId, false, DeviceTypes.FlutterViewer, factory);
        var secondDevice = await AddDeviceAsync(viewer.UserId, false, DeviceTypes.FlutterViewer, factory);

        var accepted = await viewer.Client.PostAsJsonAsync("/api/sessions/join", new { code, deviceId = firstDevice });
        var rejected = await viewer.Client.PostAsJsonAsync("/api/sessions/join", new { code, deviceId = secondDevice });

        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);

        var other = await CreateUserAsync("limited-join-other", factory);
        var otherDevice = await AddDeviceAsync(other.UserId, false, DeviceTypes.FlutterViewer, factory);
        Assert.Equal(HttpStatusCode.OK,
            (await other.Client.PostAsJsonAsync("/api/sessions/join", new { code, deviceId = otherDevice })).StatusCode);
    }

    [Fact]
    public async Task Rotate_code_is_rate_limited()
    {
        await using var factory = new SonicRelayApiFactory(new Dictionary<string, string?>
        {
            ["RateLimits:RotateCode:PermitLimit"] = "1"
        });
        var (owner, sessionId, _) = await CreateSessionAsync("limited-rotate", 2, factory);

        var accepted = await owner.Client.PostAsync($"/api/sessions/{sessionId}/rotate-code", null);
        var rejected = await owner.Client.PostAsync($"/api/sessions/{sessionId}/rotate-code", null);

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
        var (_, sessionId, code) = await CreateSessionAsync("cleanup", 2, factory);
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
                    UserId = session.OwnerUserId,
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
                    UserId = session.OwnerUserId,
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
        var store = assertScope.ServiceProvider.GetRequiredService<ISessionCodeStore>();
        Assert.Null(await store.RedeemAsync(HashCode(code), CancellationToken.None));
    }

    [Fact]
    public async Task Owner_can_list_get_and_end_a_session()
    {
        var (owner, sessionId, _) = await CreateSessionAsync("lifecycle-owner", 2);

        var active = await owner.Client.GetFromJsonAsync<JsonElement>("/api/sessions/active");
        Assert.Contains(active.EnumerateArray(), x => x.GetProperty("id").GetGuid() == sessionId);
        var get = await owner.Client.GetAsync($"/api/sessions/{sessionId}");
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);

        var end = await owner.Client.PostAsync($"/api/sessions/{sessionId}/end", null);
        Assert.Equal(HttpStatusCode.OK, end.StatusCode);
        var ended = await ReadJsonAsync(end);
        Assert.Equal(SessionStatuses.Ended, ended.GetProperty("status").GetString());
    }

    private async Task<(TestUser Owner, Guid SessionId, string Code)> CreateSessionAsync(string prefix, int maxViewers,
        SonicRelayApiFactory? factory = null)
    {
        factory ??= _factory;
        var owner = await CreateUserAsync(prefix, factory);
        var source = await AddDeviceAsync(owner.UserId, false, DeviceTypes.WindowsPublisher, factory);
        var response = await owner.Client.PostAsJsonAsync("/api/sessions", new { sourceDeviceId = source, maxViewers });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await ReadJsonAsync(response);
        return (owner, body.GetProperty("id").GetGuid(), body.GetProperty("code").GetString()!);
    }

    private async Task<TestUser> CreateUserAsync(string prefix, SonicRelayApiFactory? factory = null)
    {
        factory ??= _factory;
        var client = factory.CreateClient();
        var email = $"{prefix}-{Guid.NewGuid():N}@example.com";
        var register = await client.PostAsJsonAsync("/auth/register", new { email, password = Password });
        Assert.Equal(HttpStatusCode.OK, register.StatusCode);
        var login = await client.PostAsJsonAsync("/auth/login", new { email, password = Password });
        var tokens = await ReadJsonAsync(login);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.GetProperty("accessToken").GetString());
        var profile = await client.GetFromJsonAsync<JsonElement>("/auth/me");
        return new TestUser(client, profile.GetProperty("id").GetGuid());
    }

    private async Task<Guid> AddDeviceAsync(Guid ownerUserId, bool revoked, string type,
        SonicRelayApiFactory? factory = null)
    {
        factory ??= _factory;
        var id = Guid.NewGuid();
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Devices.Add(new Device
        {
            Id = id,
            OwnerUserId = ownerUserId,
            Name = "Test device",
            Type = type,
            Platform = type == DeviceTypes.WindowsPublisher ? DevicePlatforms.Windows : DevicePlatforms.Android,
            Revoked = revoked,
            CreatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
        return id;
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

    private static string HashCode(string code) => Convert.ToHexString(HMACSHA256.HashData(
        Encoding.UTF8.GetBytes("integration-test-session-code-key"), Encoding.ASCII.GetBytes(code)));

    private sealed record TestUser(HttpClient Client, Guid UserId);
}
