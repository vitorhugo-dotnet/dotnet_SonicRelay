using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using SonicRelay.Api.Contracts;
using SonicRelay.Api.Services;
using SonicRelay.Domain.DeviceIdentities;
using SonicRelay.Infrastructure.Persistence;

namespace SonicRelay.Api.Endpoints;

public static class PairingEndpoints
{
    public static IEndpointRouteBuilder MapPairingEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/pairings/challenges", CreateChallengeAsync)
            .RequireAuthorization("pairing:create")
            .RequireRateLimiting("pairing-create")
            .WithTags("Pairing");

        app.MapPost("/api/pairings/complete", CompleteAsync)
            .RequireAuthorization("pairing:complete")
            .RequireRateLimiting("pairing-complete")
            .WithTags("Pairing");

        app.MapGet("/api/devices/{deviceId:guid}/pairings", ListAsync)
            .RequireAuthorization("device:read")
            .WithTags("Pairing");

        app.MapDelete("/api/pairings/{pairingId:guid}", RevokeAsync)
            .RequireAuthorization("pairing:revoke")
            .WithTags("Pairing");

        return app;
    }

    // The "pairing:create" policy already restricts callers to publisher
    // devices (DeviceCredentialService.ScopesFor only grants that scope to
    // windows_publisher), so no device-type check is needed here.
    private static async Task<IResult> CreateChallengeAsync(ClaimsPrincipal principal,
        PairingChallengeService challenges, AppDbContext db, TimeProvider time, CancellationToken ct)
    {
        var publisher = await DeviceIdentityEndpoints.RequireDeviceAsync(principal, db, ct);
        if (publisher is null || publisher.Status != DeviceIdentityStatuses.Active) return Results.Unauthorized();

        var code = challenges.GenerateCode();
        var challenge = new PairingChallenge
        {
            Id = Guid.NewGuid(),
            PublisherDeviceId = publisher.Id,
            CodeHash = challenges.HashCode(code),
            ExpiresAt = challenges.NewExpiry(),
            MaxAttempts = challenges.MaxAttempts,
            CreatedAt = time.GetUtcNow()
        };
        db.PairingChallenges.Add(challenge);
        await db.SaveChangesAsync(ct);

        var qrPayload = JsonSerializer.Serialize(new { challengeId = challenge.Id, code });
        return Results.Created($"/api/pairings/{challenge.Id}",
            new CreateChallengeResponse(challenge.Id, code, qrPayload, challenge.ExpiresAt));
    }

    // The "pairing:complete" policy already restricts callers to viewer
    // devices, mirroring CreateChallengeAsync above.
    private static async Task<IResult> CompleteAsync(CompletePairingRequest request, ClaimsPrincipal principal,
        PairingChallengeService challenges, AppDbContext db, TimeProvider time, CancellationToken ct)
    {
        var viewer = await DeviceIdentityEndpoints.RequireDeviceAsync(principal, db, ct);
        if (viewer is null || viewer.Status != DeviceIdentityStatuses.Active) return Results.Unauthorized();

        var challenge = await db.PairingChallenges.SingleOrDefaultAsync(x => x.Id == request.ChallengeId, ct);
        if (!IsUsable(challenge, time.GetUtcNow()))
            return Results.BadRequest(new { error = "Invalid or expired pairing code." });

        if (challenges.HashCode(request.Code ?? string.Empty) != challenge!.CodeHash)
        {
            challenge.AttemptCount += 1;
            await db.SaveChangesAsync(ct);
            return Results.BadRequest(new { error = "Invalid or expired pairing code." });
        }

        challenge.ConsumedAt = time.GetUtcNow();
        var pairing = new DevicePairing
        {
            Id = Guid.NewGuid(),
            PublisherDeviceId = challenge.PublisherDeviceId,
            ViewerDeviceId = viewer.Id,
            Status = DevicePairingStatuses.Active,
            CreatedAt = time.GetUtcNow(),
            LastUsedAt = time.GetUtcNow()
        };
        db.DevicePairings.Add(pairing);
        await db.SaveChangesAsync(ct);
        return Results.Ok(ToResponse(pairing));
    }

    private static async Task<IResult> ListAsync(Guid deviceId, ClaimsPrincipal principal,
        AppDbContext db, CancellationToken ct)
    {
        var caller = await DeviceIdentityEndpoints.RequireDeviceAsync(principal, db, ct);
        if (caller is null || caller.Id != deviceId) return Results.Unauthorized();

        var pairings = await db.DevicePairings.AsNoTracking()
            .Where(x => (x.PublisherDeviceId == deviceId || x.ViewerDeviceId == deviceId)
                && x.Status == DevicePairingStatuses.Active)
            .OrderByDescending(x => x.CreatedAt)
            .ToListAsync(ct);
        return Results.Ok(pairings.Select(ToResponse));
    }

    private static async Task<IResult> RevokeAsync(Guid pairingId, ClaimsPrincipal principal,
        AppDbContext db, TimeProvider time, CancellationToken ct)
    {
        var caller = await DeviceIdentityEndpoints.RequireDeviceAsync(principal, db, ct);
        if (caller is null) return Results.Unauthorized();

        var pairing = await db.DevicePairings.SingleOrDefaultAsync(x =>
            x.Id == pairingId && (x.PublisherDeviceId == caller.Id || x.ViewerDeviceId == caller.Id), ct);
        if (pairing is null) return Results.NotFound();

        if (pairing.Status != DevicePairingStatuses.Revoked)
        {
            pairing.Status = DevicePairingStatuses.Revoked;
            pairing.RevokedAt = time.GetUtcNow();
            await db.SaveChangesAsync(ct);
        }
        return Results.NoContent();
    }

    private static bool IsUsable(PairingChallenge? challenge, DateTimeOffset now) =>
        challenge is not null
        && challenge.ConsumedAt is null
        && challenge.ExpiresAt > now
        && challenge.AttemptCount < challenge.MaxAttempts;

    internal static PairingResponse ToResponse(DevicePairing pairing) => new(
        pairing.Id, pairing.PublisherDeviceId, pairing.ViewerDeviceId, pairing.Status,
        pairing.CreatedAt, pairing.LastUsedAt);
}
