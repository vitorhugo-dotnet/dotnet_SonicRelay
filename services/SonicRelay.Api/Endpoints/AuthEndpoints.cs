namespace SonicRelay.Api.Endpoints;

public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/auth").WithTags("Auth");
        group.MapPost("/register", () => Results.Accepted()).WithName("Register");
        group.MapPost("/login", () => Results.Accepted()).WithName("Login");
        group.MapPost("/refresh", () => Results.Accepted()).WithName("RefreshToken");
        group.MapPost("/logout", () => Results.NoContent()).RequireAuthorization();
        group.MapGet("/me", () => Results.Ok()).RequireAuthorization();
        return app;
    }
}
