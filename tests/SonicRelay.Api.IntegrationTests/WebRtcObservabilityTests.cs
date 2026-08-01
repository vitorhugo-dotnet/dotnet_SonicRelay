using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SonicRelay.Domain.Devices;
using SonicRelay.Domain.Sessions;
using SonicRelay.Infrastructure.Persistence;
using Xunit;

namespace SonicRelay.Api.IntegrationTests;

public sealed class WebRtcObservabilityTests : IClassFixture<SonicRelayApiFactory>
{
    private readonly SonicRelayApiFactory _factory;

    public WebRtcObservabilityTests(SonicRelayApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Metrics_endpoint_is_anonymous_and_exposes_sonicrelay_series()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/metrics");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("sonicrelay_signaling_connections_active", body);
        Assert.Contains("sonicrelay_sessions_active", body);
    }

    [Fact]
    public async Task Stats_requires_authentication()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/webrtc/stats", new { sessionId = Guid.NewGuid(), role = "viewer" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Stats_forbidden_for_non_participant()
    {
        var client = _factory.CreateClient();
        await DeviceIdentityTestHelper.BootstrapAndAuthorizeAsync(client, DeviceTypes.FlutterViewer, DevicePlatforms.Android);

        var response = await client.PostAsJsonAsync("/api/webrtc/stats", new
        {
            sessionId = Guid.NewGuid(),
            role = "viewer"
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Stats_from_participant_are_accepted_and_recorded()
    {
        var client = _factory.CreateClient();
        var session = await DeviceIdentityTestHelper.BootstrapAndAuthorizeAsync(
            client, DeviceTypes.FlutterViewer, DevicePlatforms.Android);
        var sessionId = await SeedParticipantAsync(session.DeviceId);

        var response = await client.PostAsJsonAsync("/api/webrtc/stats", new
        {
            sessionId,
            role = "viewer",
            iceConnectionState = "connected",
            selectedCandidatePair = new
            {
                localCandidateType = "relay",
                remoteCandidateType = "relay",
                protocol = "udp",
                relayProtocol = "udp"
            },
            inboundAudio = new { packetsReceived = 990, packetsLost = 10, jitter = 0.012 },
            candidatePair = new { currentRoundTripTime = 0.08 }
        });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var metrics = await client.GetStringAsync("/metrics");
        Assert.Contains("sonicrelay_session_transport_mode_total{mode=\"turn_udp\"}", metrics);
        Assert.Contains("sonicrelay_session_rtt_ms", metrics);
    }

    private async Task<Guid> SeedParticipantAsync(Guid deviceId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var sessionId = Guid.NewGuid();
        db.StreamSessions.Add(new StreamSession
        {
            Id = sessionId,
            SourceDeviceId = Guid.NewGuid(),
            Status = SessionStatuses.Active,
            MaxViewers = 3,
            CodeExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10),
            CreatedAt = DateTimeOffset.UtcNow
        });
        db.SessionParticipants.Add(new SessionParticipant
        {
            Id = Guid.NewGuid(),
            SessionId = sessionId,
            DeviceId = deviceId,
            Role = ParticipantRoles.Viewer,
            Status = ParticipantStatuses.Connected,
            JoinedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
        return sessionId;
    }
}
