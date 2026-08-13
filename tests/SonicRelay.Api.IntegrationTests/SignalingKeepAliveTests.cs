using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace SonicRelay.Api.IntegrationTests;

public sealed class SignalingKeepAliveTests
{
    /// <summary>
    /// A signaling socket carries no traffic between negotiations: the server only answers
    /// `pong` to a client `ping` and never initiates one. Idle WebSockets get reaped by
    /// intermediaries — viewer diagnostics showed a near-constant ~90s close, and nginx's own
    /// default read timeout is 60s — and the ASP.NET Core default keepalive of 2 minutes is
    /// longer than either, so it never fired in time to prevent one.
    ///
    /// Clients set their own keepalive, but they cannot be relied on: an already installed
    /// app version keeps whatever it shipped with, so the server has to hold the connection
    /// open on its own.
    /// </summary>
    [Fact]
    public void ServerPingsOftenEnoughToOutlastAnIdleProxyTimeout()
    {
        using var factory = new SonicRelayApiFactory();

        var options = factory.Services.GetRequiredService<IOptions<WebSocketOptions>>().Value;

        Assert.True(
            options.KeepAliveInterval * 2 < TimeSpan.FromSeconds(60),
            $"KeepAliveInterval is {options.KeepAliveInterval}; even a missed ping must stay "
            + "well inside the shortest idle window we know of.");
    }
}
