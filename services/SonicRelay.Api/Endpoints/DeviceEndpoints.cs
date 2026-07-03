namespace SonicRelay.Api.Endpoints;

public static class DeviceEndpoints
{
    public static IEndpointRouteBuilder MapDeviceEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/devices").RequireAuthorization().WithTags("Devices");
        group.MapPost("/", () => Results.Created()).RequireAuthorization("CanRegisterDevice");
        group.MapGet("/", () => Results.Ok());
        group.MapGet("/{deviceId:guid}", (Guid deviceId) => Results.Ok(new { deviceId }));
        group.MapPatch("/{deviceId:guid}", (Guid deviceId) => Results.Ok(new { deviceId }));
        group.MapDelete("/{deviceId:guid}", (Guid deviceId) => Results.NoContent());
        group.MapPost("/{deviceId:guid}/revoke", (Guid deviceId) => Results.Ok(new { deviceId, revoked = true }));
        return app;
    }
}
