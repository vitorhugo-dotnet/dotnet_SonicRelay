using System.Net.WebSockets;
using System.Text;

namespace SonicRelay.Api.Endpoints;

public static class SignalingWebSocketEndpoint
{
    public static IEndpointRouteBuilder MapSignalingWebSocketEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet("/ws/signaling", async context =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            var hello = Encoding.UTF8.GetBytes("{\"type\":\"publisher.ready\"}");
            await socket.SendAsync(hello, WebSocketMessageType.Text, true, context.RequestAborted);
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "signaling skeleton", context.RequestAborted);
        }).RequireAuthorization();
        return app;
    }
}
