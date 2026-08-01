using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using SonicRelay.Application.Abstractions;
using SonicRelay.Domain.Sessions;
using SonicRelay.Infrastructure.Persistence;

namespace SonicRelay.Api.Endpoints;

public static class SessionEndpoints
{
    public static IEndpointRouteBuilder MapSessionEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/sessions").WithTags("Sessions");
        group.MapPost("/", CreateAsync).RequireAuthorization("session:create").RequireRateLimiting("create-session");
        group.MapGet("/active", GetActiveAsync).RequireAuthorization("DeviceAuthenticated");
        group.MapGet("/{sessionId:guid}", GetAsync).RequireAuthorization("DeviceAuthenticated");
        group.MapPost("/{sessionId:guid}/end", EndAsync).RequireAuthorization("session:end");
        group.MapPost("/{sessionId:guid}/rotate-code", RotateCodeAsync).RequireAuthorization("session:end").RequireRateLimiting("rotate-code");
        group.MapPost("/join", JoinAsync).RequireAuthorization("session:join").RequireRateLimiting("join-session");
        return app;
    }

    private static async Task<IResult> CreateAsync(CreateSessionRequest request,
        ClaimsPrincipal principal, AppDbContext db, ISessionCodeStore codeStore, IConfiguration configuration,
        ILoggerFactory loggerFactory, CancellationToken ct)
    {
        var device = await DeviceIdentityEndpoints.RequireDeviceAsync(principal, db, ct);
        if (device is null) return Results.Unauthorized();
        var maxViewers = request.MaxViewers ?? configuration.GetValue("Sessions:MaxViewersPerSession", 3);
        if (maxViewers < 1) return Results.BadRequest(new { error = "MaxViewers must be at least one." });

        var now = DateTimeOffset.UtcNow;
        var ttl = CodeTtl(configuration);
        var session = new StreamSession
        {
            Id = Guid.NewGuid(),
            SourceDeviceId = device.Id,
            MaxViewers = maxViewers,
            CodeExpiresAt = now.Add(ttl),
            CreatedAt = now
        };
        db.StreamSessions.Add(session);
        db.SessionParticipants.Add(new SessionParticipant
        {
            Id = Guid.NewGuid(),
            SessionId = session.Id,
            DeviceId = device.Id,
            Role = ParticipantRoles.Publisher,
            Status = ParticipantStatuses.Connected,
            JoinedAt = now
        });
        await db.SaveChangesAsync(ct);

        var code = GenerateCode();
        await codeStore.StoreAsync(HashCode(code, configuration), session.Id, ttl, ct);
        loggerFactory.CreateLogger("SonicRelay.Sessions").LogInformation(
            "Created session {SessionId} from device {DeviceId}", session.Id, device.Id);
        return Results.Created($"/api/sessions/{session.Id}", ToResponse(session, code));
    }

    private static async Task<IResult> GetActiveAsync(ClaimsPrincipal principal, AppDbContext db, CancellationToken ct)
    {
        var device = await DeviceIdentityEndpoints.RequireDeviceAsync(principal, db, ct);
        if (device is null) return Results.Unauthorized();
        var sessions = await db.StreamSessions
            .Where(x => (x.Status == SessionStatuses.Waiting || x.Status == SessionStatuses.Active)
                && (x.SourceDeviceId == device.Id || db.SessionParticipants.Any(p => p.SessionId == x.Id && p.DeviceId == device.Id)))
            .OrderByDescending(x => x.CreatedAt)
            .Select(x => new
            {
                x.Id,
                x.SourceDeviceId,
                x.Status,
                x.MaxViewers,
                x.CodeExpiresAt,
                x.StartedAt,
                x.EndedAt,
                x.CreatedAt,
                ViewerCount = db.SessionParticipants.Count(p => p.SessionId == x.Id && p.Role == ParticipantRoles.Viewer
                    && p.Status == ParticipantStatuses.Connected)
            }).ToListAsync(ct);
        return Results.Ok(sessions);
    }

    private static async Task<IResult> GetAsync(Guid sessionId, ClaimsPrincipal principal, AppDbContext db, CancellationToken ct)
    {
        var device = await DeviceIdentityEndpoints.RequireDeviceAsync(principal, db, ct);
        if (device is null) return Results.Unauthorized();
        var session = await db.StreamSessions.SingleOrDefaultAsync(x => x.Id == sessionId, ct);
        if (session is null) return Results.NotFound();
        var canAccess = session.SourceDeviceId == device.Id
            || await db.SessionParticipants.AnyAsync(x => x.SessionId == sessionId && x.DeviceId == device.Id, ct);
        return canAccess ? Results.Ok(ToResponse(session)) : Results.NotFound();
    }

    private static async Task<IResult> EndAsync(Guid sessionId, ClaimsPrincipal principal, AppDbContext db,
        ISessionCodeStore codeStore, IParticipantReconnectTracker reconnectTracker, ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var device = await DeviceIdentityEndpoints.RequireDeviceAsync(principal, db, ct);
        if (device is null) return Results.Unauthorized();
        var session = await db.StreamSessions.SingleOrDefaultAsync(x => x.Id == sessionId && x.SourceDeviceId == device.Id, ct);
        if (session is null) return Results.NotFound();
        if (session.Status != SessionStatuses.Ended)
        {
            var now = DateTimeOffset.UtcNow;
            session.Status = SessionStatuses.Ended;
            session.EndedAt = now;
            // Includes participants mid-reconnect-grace-period: an owner-initiated end must win
            // immediately over a pending grace timer, which we also cancel so it can't fire a
            // stale "session.left" broadcast afterwards.
            var connected = await db.SessionParticipants.Where(x => x.SessionId == sessionId
                && (x.Status == ParticipantStatuses.Connected || x.Status == ParticipantStatuses.Reconnecting))
                .ToListAsync(ct);
            foreach (var participant in connected)
            {
                participant.Status = ParticipantStatuses.Disconnected;
                participant.ConnectionId = null;
                participant.LeftAt = now;
                reconnectTracker.TryCancelGracePeriod(participant.Id);
            }
            await db.SaveChangesAsync(ct);
            await codeStore.RemoveAsync(sessionId, ct);
            loggerFactory.CreateLogger("SonicRelay.Sessions").LogInformation(
                "Ended session {SessionId} from device {DeviceId}; disconnected {ParticipantCount} participants",
                sessionId, device.Id, connected.Count);
        }
        return Results.Ok(ToResponse(session));
    }

    private static async Task<IResult> RotateCodeAsync(Guid sessionId, ClaimsPrincipal principal, AppDbContext db,
        ISessionCodeStore codeStore, IConfiguration configuration, ILoggerFactory loggerFactory, CancellationToken ct)
    {
        var device = await DeviceIdentityEndpoints.RequireDeviceAsync(principal, db, ct);
        if (device is null) return Results.Unauthorized();
        var session = await db.StreamSessions.SingleOrDefaultAsync(x => x.Id == sessionId && x.SourceDeviceId == device.Id, ct);
        if (session is null) return Results.NotFound();
        if (session.Status is SessionStatuses.Ended or SessionStatuses.Expired) return Results.Conflict();

        var code = GenerateCode();
        var ttl = CodeTtl(configuration);
        session.CodeExpiresAt = DateTimeOffset.UtcNow.Add(ttl);
        await db.SaveChangesAsync(ct);
        await codeStore.StoreAsync(HashCode(code, configuration), session.Id, ttl, ct);
        loggerFactory.CreateLogger("SonicRelay.Sessions").LogInformation(
            "Rotated join code for session {SessionId} from device {DeviceId}", session.Id, device.Id);
        return Results.Ok(ToResponse(session, code));
    }

    private static async Task<IResult> JoinAsync(JoinSessionRequest request, ClaimsPrincipal principal, AppDbContext db,
        ISessionCodeStore codeStore, IConfiguration configuration, ILoggerFactory loggerFactory, CancellationToken ct)
    {
        var device = await DeviceIdentityEndpoints.RequireDeviceAsync(principal, db, ct);
        if (device is null) return Results.Unauthorized();
        var normalizedCode = request.Code?.Trim().ToUpperInvariant() ?? string.Empty;
        if (normalizedCode.Length != 6 || normalizedCode.Any(c => !char.IsAsciiLetterOrDigit(c)))
            return InvalidCode();

        var sessionId = await codeStore.RedeemAsync(HashCode(normalizedCode, configuration), ct);
        if (sessionId is null) return InvalidCode();
        var session = await db.StreamSessions.SingleOrDefaultAsync(x => x.Id == sessionId.Value, ct);
        var now = DateTimeOffset.UtcNow;
        if (session is null || session.CodeExpiresAt <= now
            || session.Status is SessionStatuses.Ended or SessionStatuses.Expired)
        {
            if (session is not null && session.CodeExpiresAt <= now && session.Status != SessionStatuses.Ended)
            {
                session.Status = SessionStatuses.Expired;
                await db.SaveChangesAsync(ct);
                await codeStore.RemoveAsync(session.Id, ct);
                loggerFactory.CreateLogger("SonicRelay.Sessions").LogInformation(
                    "Marked session {SessionId} expired during join", session.Id);
            }
            return InvalidCode();
        }

        var existing = await db.SessionParticipants.SingleOrDefaultAsync(x => x.SessionId == session.Id
            && x.DeviceId == device.Id && x.Role == ParticipantRoles.Viewer, ct);
        if (existing is not null)
        {
            existing.Status = ParticipantStatuses.Connected;
            existing.LeftAt = null;
            await db.SaveChangesAsync(ct);
            loggerFactory.CreateLogger("SonicRelay.Sessions").LogInformation(
                "Reconnected participant {ParticipantId} to session {SessionId} from device {DeviceId}",
                existing.Id, session.Id, device.Id);
            return Results.Ok(ToResponse(session));
        }

        // Viewers mid-reconnect-grace-period still hold their slot, otherwise a new viewer
        // could take it during the grace window and leave a maxViewers=1 session with two
        // viewers once the original one's WebSocket reconnects.
        var viewerCount = await db.SessionParticipants.CountAsync(x => x.SessionId == session.Id
            && x.Role == ParticipantRoles.Viewer
            && (x.Status == ParticipantStatuses.Connected || x.Status == ParticipantStatuses.Reconnecting), ct);
        if (viewerCount >= session.MaxViewers) return Results.Conflict(new { error = "Session viewer limit reached." });

        var participant = new SessionParticipant
        {
            Id = Guid.NewGuid(),
            SessionId = session.Id,
            DeviceId = device.Id,
            Role = ParticipantRoles.Viewer,
            Status = ParticipantStatuses.Connected,
            JoinedAt = now
        };
        db.SessionParticipants.Add(participant);
        if (session.Status == SessionStatuses.Waiting)
        {
            session.Status = SessionStatuses.Active;
            session.StartedAt = now;
        }
        await db.SaveChangesAsync(ct);
        loggerFactory.CreateLogger("SonicRelay.Sessions").LogInformation(
            "Joined session {SessionId} as participant {ParticipantId} from device {DeviceId}",
            session.Id, participant.Id, device.Id);
        return Results.Ok(ToResponse(session));
    }

    private static IResult InvalidCode() => Results.NotFound(new { error = "Invalid or expired session code." });

    private static object ToResponse(StreamSession session, string? code = null) => new
    {
        session.Id,
        session.SourceDeviceId,
        session.Status,
        session.MaxViewers,
        session.CodeExpiresAt,
        session.StartedAt,
        session.EndedAt,
        session.CreatedAt,
        code
    };

    private static TimeSpan CodeTtl(IConfiguration configuration) =>
        TimeSpan.FromMinutes(configuration.GetValue("Sessions:CodeTtlMinutes", 10));

    private static string HashCode(string code, IConfiguration configuration)
    {
        var key = configuration["Sessions:CodeHmacKey"];
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("Sessions:CodeHmacKey must be configured.");
        return Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), Encoding.ASCII.GetBytes(code)));
    }

    private static string GenerateCode()
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
        return string.Create(6, alphabet, static (span, chars) =>
        {
            for (var i = 0; i < span.Length; i++) span[i] = chars[RandomNumberGenerator.GetInt32(chars.Length)];
        });
    }

    // SourceDeviceId/DeviceId are no longer client-supplied: the caller's own device identity
    // (from the DeviceBearer token) is always the publisher of a created session and always the
    // viewer that joins, so there is nothing left for the client to assert about which device it is.
    private sealed record CreateSessionRequest(int? MaxViewers);
    private sealed record JoinSessionRequest(string Code);
}
