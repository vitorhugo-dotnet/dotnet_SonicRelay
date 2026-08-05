using Microsoft.EntityFrameworkCore;
using SonicRelay.Api.Contracts;
using SonicRelay.Domain.RelaySettings;
using SonicRelay.Infrastructure.Persistence;

namespace SonicRelay.Api.Endpoints;

public static class SettingsEndpoints
{
    public static IEndpointRouteBuilder MapSettingsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/settings").WithTags("Settings");
        group.MapGet("/relay", GetRelaySettingsAsync).RequireAuthorization("device:manage");
        group.MapPut("/relay", UpdateRelaySettingsAsync).RequireAuthorization("device:manage");
        return app;
    }

    private static async Task<IResult> GetRelaySettingsAsync(AppDbContext db, CancellationToken ct)
    {
        var row = await db.RelaySettings.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == RelaySettings.SingletonId, ct);
        return Results.Ok(ToResponse(row));
    }

    private static async Task<IResult> UpdateRelaySettingsAsync(
        UpdateRelaySettingsRequest request, AppDbContext db, TimeProvider time, CancellationToken ct)
    {
        if (request.RelayMode is { } mode && !RelayModes.IsValid(mode))
        {
            return Results.BadRequest(new { error = "relayMode must be one of: automatic, forceRelay, disableFallback." });
        }
        if (request.TurnUris is { } uris && uris.Any(string.IsNullOrWhiteSpace))
        {
            return Results.BadRequest(new { error = "turnUris entries must not be blank." });
        }

        var row = await db.RelaySettings.SingleOrDefaultAsync(x => x.Id == RelaySettings.SingletonId, ct);
        if (row is null)
        {
            row = new RelaySettings { Id = RelaySettings.SingletonId };
            db.RelaySettings.Add(row);
        }

        if (request.RelayMode is { } newMode) row.RelayMode = newMode;
        if (request.TurnUris is { } newUris) row.TurnUris = newUris.ToArray();
        if (request.TurnStaticAuthSecret is { } newSecret) row.TurnStaticAuthSecret = newSecret;
        row.UpdatedAt = time.GetUtcNow();

        await db.SaveChangesAsync(ct);
        return Results.Ok(ToResponse(row));
    }

    private static RelaySettingsResponse ToResponse(RelaySettings? row) => new(
        row?.RelayMode ?? RelayModes.Automatic,
        row?.TurnUris ?? [],
        !string.IsNullOrWhiteSpace(row?.TurnStaticAuthSecret));
}
