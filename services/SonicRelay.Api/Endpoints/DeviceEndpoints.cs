using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using SonicRelay.Api.Contracts;
using SonicRelay.Domain.Devices;
using SonicRelay.Domain.Users;
using SonicRelay.Infrastructure.Persistence;

namespace SonicRelay.Api.Endpoints;

public static class DeviceEndpoints
{
    public static IEndpointRouteBuilder MapDeviceEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/devices").RequireAuthorization().WithTags("Devices");
        group.MapPost("/", CreateAsync).RequireAuthorization("CanRegisterDevice");
        group.MapGet("/", ListAsync);
        group.MapGet("/{deviceId:guid}", GetAsync);
        group.MapPatch("/{deviceId:guid}", UpdateAsync);
        group.MapDelete("/{deviceId:guid}", DeleteAsync);
        group.MapPost("/{deviceId:guid}/revoke", RevokeAsync);
        return app;
    }

    private static async Task<IResult> CreateAsync(CreateDeviceRequest request,
        System.Security.Claims.ClaimsPrincipal principal, UserManager<ApplicationUser> userManager,
        AppDbContext db, CancellationToken ct)
    {
        var user = await userManager.GetUserAsync(principal);
        if (user is null) return Results.Unauthorized();
        if (!ValidName(request.Name) || !ValidTypePlatform(request.Type, request.Platform))
            return Results.BadRequest(new { error = "Invalid device name, type, or platform." });

        var device = new Device
        {
            Id = Guid.NewGuid(),
            OwnerUserId = user.Id,
            Name = request.Name!.Trim(),
            Type = request.Type!,
            Platform = request.Platform!,
            PublicKey = request.PublicKey,
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.Devices.Add(device);
        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/devices/{device.Id}", ToResponse(device));
    }

    private static async Task<IResult> ListAsync(System.Security.Claims.ClaimsPrincipal principal,
        UserManager<ApplicationUser> userManager, AppDbContext db, CancellationToken ct)
    {
        var user = await userManager.GetUserAsync(principal);
        if (user is null) return Results.Unauthorized();
        var devices = await db.Devices.AsNoTracking()
            .Where(x => x.OwnerUserId == user.Id)
            .OrderByDescending(x => x.CreatedAt)
            .Select(x => new DeviceResponse(
                x.Id,
                x.Name,
                x.Type,
                x.Platform,
                x.PublicKey,
                x.Trusted,
                x.Revoked,
                x.LastSeenAt,
                x.CreatedAt))
            .ToListAsync(ct);
        return Results.Ok(devices);
    }

    private static async Task<IResult> GetAsync(Guid deviceId,
        System.Security.Claims.ClaimsPrincipal principal, UserManager<ApplicationUser> userManager,
        AppDbContext db, CancellationToken ct)
    {
        var device = await FindOwnedAsync(deviceId, principal, userManager, db, ct);
        return device is null ? Results.NotFound() : Results.Ok(ToResponse(device));
    }

    private static async Task<IResult> UpdateAsync(Guid deviceId, UpdateDeviceRequest request,
        System.Security.Claims.ClaimsPrincipal principal, UserManager<ApplicationUser> userManager,
        AppDbContext db, CancellationToken ct)
    {
        if (request.Name is null && request.PublicKey is null)
            return Results.BadRequest(new { error = "At least one field is required." });
        if (request.Name is not null && !ValidName(request.Name))
            return Results.BadRequest(new { error = "Invalid device name." });

        var device = await FindOwnedAsync(deviceId, principal, userManager, db, ct);
        if (device is null) return Results.NotFound();
        if (request.Name is not null) device.Name = request.Name.Trim();
        if (request.PublicKey is not null) device.PublicKey = request.PublicKey;
        await db.SaveChangesAsync(ct);
        return Results.Ok(ToResponse(device));
    }

    private static async Task<IResult> DeleteAsync(Guid deviceId,
        System.Security.Claims.ClaimsPrincipal principal, UserManager<ApplicationUser> userManager,
        AppDbContext db, CancellationToken ct)
    {
        var device = await FindOwnedAsync(deviceId, principal, userManager, db, ct);
        if (device is null) return Results.NotFound();
        db.Devices.Remove(device);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> RevokeAsync(Guid deviceId,
        System.Security.Claims.ClaimsPrincipal principal, UserManager<ApplicationUser> userManager,
        AppDbContext db, CancellationToken ct)
    {
        var device = await FindOwnedAsync(deviceId, principal, userManager, db, ct);
        if (device is null) return Results.NotFound();
        if (!device.Revoked)
        {
            device.Revoked = true;
            await db.SaveChangesAsync(ct);
        }
        return Results.Ok(ToResponse(device));
    }

    private static async Task<Device?> FindOwnedAsync(Guid deviceId,
        System.Security.Claims.ClaimsPrincipal principal, UserManager<ApplicationUser> userManager,
        AppDbContext db, CancellationToken ct)
    {
        var user = await userManager.GetUserAsync(principal);
        if (user is null) return null;
        return await db.Devices.SingleOrDefaultAsync(x => x.Id == deviceId && x.OwnerUserId == user.Id, ct);
    }

    private static bool ValidName(string? name) =>
        !string.IsNullOrWhiteSpace(name) && name.Trim().Length <= 120;

    private static bool ValidTypePlatform(string? type, string? platform) =>
        (type == DeviceTypes.WindowsPublisher && platform == DevicePlatforms.Windows)
        || (type == DeviceTypes.FlutterViewer
            && platform is DevicePlatforms.Android or DevicePlatforms.Ios);

    private static DeviceResponse ToResponse(Device device) => new(
        device.Id,
        device.Name,
        device.Type,
        device.Platform,
        device.PublicKey,
        device.Trusted,
        device.Revoked,
        device.LastSeenAt,
        device.CreatedAt);
}
