using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using SonicRelay.Api.Contracts;
using Xunit;

namespace SonicRelay.Api.IntegrationTests;

public sealed class DeviceCredentialLifecycleTests : IClassFixture<SonicRelayApiFactory>
{
    private readonly HttpClient _client;

    public DeviceCredentialLifecycleTests(SonicRelayApiFactory factory) => _client = factory.CreateClient();

    private async Task<(Guid DeviceId, string Secret, string AccessToken)> BootstrapAndAuthenticateAsync(
        string type, string platform)
    {
        var bootstrap = await (await _client.PostAsJsonAsync("/api/devices/bootstrap",
            new BootstrapDeviceRequest("Device", type, platform)))
            .Content.ReadFromJsonAsync<BootstrapDeviceResponse>();
        var token = await (await _client.PostAsJsonAsync("/api/devices/token",
            new DeviceTokenRequest(bootstrap!.DeviceId, bootstrap.CredentialSecret)))
            .Content.ReadFromJsonAsync<DeviceTokenResponse>();
        return (bootstrap.DeviceId, bootstrap.CredentialSecret, token!.AccessToken);
    }

    [Fact]
    public async Task RotateCredential_Invalidates_PreviousToken()
    {
        var (deviceId, secret, accessToken) = await BootstrapAndAuthenticateAsync("windows_publisher", "windows");

        using var rotateRequest = new HttpRequestMessage(HttpMethod.Post, "/api/devices/rotate-credential")
        {
            Content = JsonContent.Create(new RotateCredentialRequest(secret))
        };
        rotateRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        var rotateResponse = await _client.SendAsync(rotateRequest);
        Assert.Equal(HttpStatusCode.OK, rotateResponse.StatusCode);

        using var staleTokenRequest = new HttpRequestMessage(HttpMethod.Post, "/api/devices/revoke");
        staleTokenRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        var staleResponse = await _client.SendAsync(staleTokenRequest);
        Assert.Equal(HttpStatusCode.Forbidden, staleResponse.StatusCode);

        var newSecret = await rotateResponse.Content.ReadFromJsonAsync<RotateCredentialResponse>();
        var newToken = await (await _client.PostAsJsonAsync("/api/devices/token",
            new DeviceTokenRequest(deviceId, newSecret!.CredentialSecret)))
            .Content.ReadFromJsonAsync<DeviceTokenResponse>();
        Assert.NotNull(newToken);
    }

    [Fact]
    public async Task Revoke_Blocks_FutureTokenRequests_And_AlreadyIssuedTokens()
    {
        var (deviceId, secret, accessToken) = await BootstrapAndAuthenticateAsync("flutter_viewer", "android");

        using var revokeRequest = new HttpRequestMessage(HttpMethod.Post, "/api/devices/revoke");
        revokeRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        var revokeResponse = await _client.SendAsync(revokeRequest);
        Assert.Equal(HttpStatusCode.NoContent, revokeResponse.StatusCode);

        var tokenAfterRevoke = await _client.PostAsJsonAsync("/api/devices/token",
            new DeviceTokenRequest(deviceId, secret));
        Assert.Equal(HttpStatusCode.Unauthorized, tokenAfterRevoke.StatusCode);

        using var staleTokenRequest = new HttpRequestMessage(HttpMethod.Post, "/api/devices/revoke");
        staleTokenRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        var staleResponse = await _client.SendAsync(staleTokenRequest);
        Assert.Equal(HttpStatusCode.Forbidden, staleResponse.StatusCode);
    }

    [Fact]
    public async Task RotateCredential_Requires_CurrentSecret()
    {
        var (_, _, accessToken) = await BootstrapAndAuthenticateAsync("windows_publisher", "windows");

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/devices/rotate-credential")
        {
            Content = JsonContent.Create(new RotateCredentialRequest("wrong-secret"))
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
