namespace SonicRelay.Api.Endpoints;

public static class SessionEndpoints
{
    public static IEndpointRouteBuilder MapSessionEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/sessions").RequireAuthorization().WithTags("Sessions");
        group.MapPost("/", () => Results.Accepted()).RequireAuthorization("CanCreateSession");
        group.MapGet("/active", () => Results.Ok());
        group.MapGet("/{sessionId:guid}", (Guid sessionId) => Results.Ok(new { sessionId }));
        group.MapPost("/{sessionId:guid}/end", (Guid sessionId) => Results.Ok(new { sessionId, status = "ended" }));
        group.MapPost("/{sessionId:guid}/rotate-code", (Guid sessionId) => Results.Accepted($"/api/sessions/{sessionId}"));
        group.MapPost("/join", () => Results.Accepted()).RequireAuthorization("CanJoinSession");
        return app;
    }
}
