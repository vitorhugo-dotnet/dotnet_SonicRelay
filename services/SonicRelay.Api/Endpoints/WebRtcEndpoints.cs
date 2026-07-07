using Microsoft.AspNetCore.Identity;
using SonicRelay.Api.Services;
using SonicRelay.Domain.Users;

namespace SonicRelay.Api.Endpoints;

public static class WebRtcEndpoints
{
    public static IEndpointRouteBuilder MapWebRtcEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/webrtc").RequireAuthorization().WithTags("WebRTC");
        group.MapGet("/ice-servers", GetIceServersAsync);
        return app;
    }

    private static async Task<IResult> GetIceServersAsync(System.Security.Claims.ClaimsPrincipal principal,
        UserManager<ApplicationUser> userManager, TurnCredentialService credentials)
    {
        var user = await userManager.GetUserAsync(principal);
        if (user is null) return Results.Unauthorized();
        return Results.Ok(credentials.Build(user.Id.ToString("D")));
    }
}
