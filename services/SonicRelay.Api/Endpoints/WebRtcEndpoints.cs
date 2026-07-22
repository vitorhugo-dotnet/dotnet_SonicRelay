using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using SonicRelay.Api.Contracts;
using SonicRelay.Api.Observability;
using SonicRelay.Api.Services;
using SonicRelay.Infrastructure.Persistence;

namespace SonicRelay.Api.Endpoints;

public static class WebRtcEndpoints
{
    public static IEndpointRouteBuilder MapWebRtcEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/webrtc").WithTags("WebRTC");
        group.MapGet("/ice-servers", GetIceServersAsync).RequireAuthorization("turn:credentials");
        group.MapPost("/stats", ReportStatsAsync).RequireAuthorization("DeviceAuthenticated").WithName("ReportWebRtcStats");
        return app;
    }

    private static async Task<IResult> GetIceServersAsync(ClaimsPrincipal principal, AppDbContext db,
        TurnCredentialService credentials, CancellationToken ct)
    {
        var device = await DeviceIdentityEndpoints.RequireDeviceAsync(principal, db, ct);
        if (device is null) return Results.Unauthorized();
        return Results.Ok(credentials.Build(device.Id.ToString("D")));
    }

    private static async Task<IResult> ReportStatsAsync(
        WebRtcStatsReport report,
        ClaimsPrincipal principal,
        AppDbContext db,
        SonicRelayMetrics metrics,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var device = await DeviceIdentityEndpoints.RequireDeviceAsync(principal, db, ct);
        if (device is null) return Results.Unauthorized();

        // Only a participant of the session may report stats for it, so an authenticated
        // device cannot inject metrics for arbitrary sessions.
        var isParticipant = await db.SessionParticipants.AsNoTracking()
            .AnyAsync(x => x.SessionId == report.SessionId && x.DeviceId == device.Id, ct);
        if (!isParticipant) return Results.Forbid();

        var role = report.Role is "publisher" or "viewer" ? report.Role : "unknown";
        var transportMode = DeriveTransportMode(report.SelectedCandidatePair);
        var packetLossRatio = DerivePacketLossRatio(report.InboundAudio);
        var jitterMs = report.InboundAudio?.Jitter is { } jitter ? jitter * 1000.0 : (double?)null;
        var rttMs = report.CandidatePair?.CurrentRoundTripTime is { } rtt ? rtt * 1000.0 : (double?)null;

        metrics.RecordStats(role, transportMode, packetLossRatio, jitterMs, rttMs, report.IceRestart);

        // Structured, low-cardinality audit line. The session id is hashed so logs can be
        // correlated to a session without exposing the real id, and no SDP/ICE is logged.
        var logger = loggerFactory.CreateLogger("SonicRelay.WebRtcStats");
        logger.LogInformation(
            "WebRTC stats: session={SessionHash} role={Role} transport={TransportMode} ice={IceState} iceRestart={IceRestart} lossRatio={LossRatio} jitterMs={JitterMs} rttMs={RttMs}",
            HashSession(report.SessionId), role, transportMode ?? "unknown",
            report.IceConnectionState ?? "unknown", report.IceRestart,
            packetLossRatio, jitterMs, rttMs);

        return Results.Accepted();
    }

    // Maps the selected candidate pair to the bounded transport mode used in metrics.
    private static string? DeriveTransportMode(SelectedCandidatePairReport? pair)
    {
        if (pair is null) return null;
        var remote = pair.RemoteCandidateType?.ToLowerInvariant();
        var local = pair.LocalCandidateType?.ToLowerInvariant();
        if (remote == "relay" || local == "relay")
        {
            return (pair.RelayProtocol?.ToLowerInvariant()) switch
            {
                "tls" => "turn_tls",
                "tcp" => "turn_tcp",
                _ => "turn_udp"
            };
        }
        if (remote == "srflx" || local == "srflx" || remote == "prflx" || local == "prflx")
        {
            return "stun";
        }
        if (remote == "host" || local == "host")
        {
            return "direct";
        }
        return null;
    }

    private static double? DerivePacketLossRatio(InboundAudioReport? inbound)
    {
        if (inbound?.PacketsLost is not { } lost || inbound.PacketsReceived is not { } received) return null;
        var total = lost + received;
        return total <= 0 ? 0 : (double)lost / total;
    }

    // Short, stable, non-reversible tag for correlation without exposing the session id.
    private static string HashSession(Guid sessionId)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(sessionId.ToString("N")));
        return Convert.ToHexString(hash.AsSpan(0, 6)).ToLowerInvariant();
    }
}
