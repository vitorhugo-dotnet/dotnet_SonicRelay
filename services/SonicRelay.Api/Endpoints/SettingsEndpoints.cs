using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using SonicRelay.Api.Contracts;
using SonicRelay.Domain.DeviceIdentities;
using SonicRelay.Domain.RelaySettings;
using SonicRelay.Infrastructure.Persistence;

namespace SonicRelay.Api.Endpoints;

/// <summary>
/// Per-device relay preferences that follow the user across their paired devices: each device
/// writes its own row, and reads resolve to the most recently updated row among the device and
/// its actively paired peers. Changing the coturn override on the phone therefore changes it
/// on the paired desktop too, without any shared account concept.
/// </summary>
public static class SettingsEndpoints
{
    public static IEndpointRouteBuilder MapSettingsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/settings").WithTags("Settings");
        group.MapGet("/relay", GetRelaySettingsAsync).RequireAuthorization("device:read");
        group.MapPut("/relay", UpdateRelaySettingsAsync).RequireAuthorization("device:manage");
        return app;
    }

    private static async Task<IResult> GetRelaySettingsAsync(ClaimsPrincipal principal, AppDbContext db,
        CancellationToken ct)
    {
        var device = await DeviceIdentityEndpoints.RequireDeviceAsync(principal, db, ct);
        if (device is null) return Results.Unauthorized();

        var effective = await ResolveEffectiveAsync(db, device.Id, ct);
        return Results.Ok(ToResponse(effective));
    }

    private static async Task<IResult> UpdateRelaySettingsAsync(UpdateRelaySettingsRequest request,
        ClaimsPrincipal principal, AppDbContext db, TimeProvider time, CancellationToken ct)
    {
        var device = await DeviceIdentityEndpoints.RequireDeviceAsync(principal, db, ct);
        if (device is null) return Results.Unauthorized();

        if (request.RelayMode is { } mode && !RelayModes.IsValid(mode))
        {
            return Results.BadRequest(new { error = "relayMode must be one of: automatic, forceRelay, disableFallback." });
        }
        if (request.TurnUris is { } uris && uris.Any(uri => !IsValidTurnUri(uri)))
        {
            return Results.BadRequest(new { error = "turnUris entries must be turn:, turns: or stun: URIs." });
        }

        var row = await db.RelayDeviceSettings.SingleOrDefaultAsync(x => x.DeviceId == device.Id, ct);
        if (row is null)
        {
            // A first write starts from the current effective settings, so a device that only
            // changes the relay mode does not silently discard the coturn override a paired
            // device configured earlier (and vice versa).
            var effective = await ResolveEffectiveAsync(db, device.Id, ct);
            row = new RelayDeviceSettings
            {
                DeviceId = device.Id,
                RelayMode = effective?.RelayMode ?? RelayModes.Automatic,
                TurnUris = effective?.TurnUris ?? [],
                TurnUsername = effective?.TurnUsername,
                TurnCredential = effective?.TurnCredential
            };
            db.RelayDeviceSettings.Add(row);
        }

        if (request.RelayMode is { } newMode) row.RelayMode = newMode;
        if (request.TurnUris is { } newUris)
        {
            row.TurnUris = newUris.Select(x => x.Trim()).ToArray();
            if (row.TurnUris.Length == 0)
            {
                // Clearing the custom relay also clears its credentials — they belong to the
                // removed server and must not leak onto whatever relay is used next.
                row.TurnUsername = null;
                row.TurnCredential = null;
            }
        }
        if (request.TurnUsername is { } newUsername)
            row.TurnUsername = string.IsNullOrWhiteSpace(newUsername) ? null : newUsername.Trim();
        if (request.TurnCredential is { } newCredential)
            row.TurnCredential = string.IsNullOrWhiteSpace(newCredential) ? null : newCredential;
        row.UpdatedAt = time.GetUtcNow();

        await db.SaveChangesAsync(ct);
        return Results.Ok(ToResponse(row));
    }

    /// <summary>
    /// The effective settings for a device: the most recently updated row among the device
    /// itself and every device it is actively paired with (latest write wins across devices).
    /// </summary>
    internal static async Task<RelayDeviceSettings?> ResolveEffectiveAsync(AppDbContext db, Guid deviceId,
        CancellationToken ct)
    {
        var deviceIds = await db.DevicePairings.AsNoTracking()
            .Where(x => (x.PublisherDeviceId == deviceId || x.ViewerDeviceId == deviceId)
                && x.Status == DevicePairingStatuses.Active)
            .Select(x => x.PublisherDeviceId == deviceId ? x.ViewerDeviceId : x.PublisherDeviceId)
            .ToListAsync(ct);
        deviceIds.Add(deviceId);

        return await db.RelayDeviceSettings.AsNoTracking()
            .Where(x => deviceIds.Contains(x.DeviceId))
            .OrderByDescending(x => x.UpdatedAt)
            .FirstOrDefaultAsync(ct);
    }

    private static bool IsValidTurnUri(string? uri) =>
        !string.IsNullOrWhiteSpace(uri)
        && (uri.Trim().StartsWith("turn:", StringComparison.OrdinalIgnoreCase)
            || uri.Trim().StartsWith("turns:", StringComparison.OrdinalIgnoreCase)
            || uri.Trim().StartsWith("stun:", StringComparison.OrdinalIgnoreCase));

    private static RelaySettingsResponse ToResponse(RelayDeviceSettings? row) => new(
        row?.RelayMode ?? RelayModes.Automatic,
        row?.TurnUris ?? [],
        row?.TurnUsername,
        !string.IsNullOrWhiteSpace(row?.TurnCredential),
        row?.UpdatedAt);
}
