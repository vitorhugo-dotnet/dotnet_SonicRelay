using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using SonicRelay.Api.Contracts;
using Xunit;

namespace SonicRelay.Api.IntegrationTests;

public sealed class PairingManagementTests : IClassFixture<SonicRelayApiFactory>
{
    private readonly HttpClient _client;

    public PairingManagementTests(SonicRelayApiFactory factory) => _client = factory.CreateClient();

    private async Task<(Guid DeviceId, string AccessToken)> BootstrapAndAuthenticateAsync(string type, string platform)
    {
        var bootstrap = await (await _client.PostAsJsonAsync("/api/devices/bootstrap",
            new BootstrapDeviceRequest("Device", type, platform)))
            .Content.ReadFromJsonAsync<BootstrapDeviceResponse>();
        var token = await (await _client.PostAsJsonAsync("/api/devices/token",
            new DeviceTokenRequest(bootstrap!.DeviceId, bootstrap.CredentialSecret)))
            .Content.ReadFromJsonAsync<DeviceTokenResponse>();
        return (bootstrap.DeviceId, token!.AccessToken);
    }

    private HttpRequestMessage Authorized(HttpMethod method, string url, string accessToken, object? body = null)
    {
        var request = new HttpRequestMessage(method, url);
        if (body is not null) request.Content = JsonContent.Create(body);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return request;
    }

    private async Task<PairingResponse> PairAsync((Guid DeviceId, string AccessToken) publisher,
        (Guid DeviceId, string AccessToken) viewer)
    {
        var challenge = await (await _client.SendAsync(
            Authorized(HttpMethod.Post, "/api/pairings/challenges", publisher.AccessToken)))
            .Content.ReadFromJsonAsync<CreateChallengeResponse>();
        var pairing = await (await _client.SendAsync(Authorized(HttpMethod.Post, "/api/pairings/complete",
            viewer.AccessToken, new CompletePairingRequest(challenge!.ChallengeId, challenge.Code))))
            .Content.ReadFromJsonAsync<PairingResponse>();
        return pairing!;
    }

    [Fact]
    public async Task List_Returns_Only_ActivePairings_For_The_Authenticated_Device()
    {
        var publisherA = await BootstrapAndAuthenticateAsync("windows_publisher", "windows");
        var viewerA = await BootstrapAndAuthenticateAsync("flutter_viewer", "android");
        var publisherB = await BootstrapAndAuthenticateAsync("windows_publisher", "windows");
        var viewerB = await BootstrapAndAuthenticateAsync("flutter_viewer", "android");

        await PairAsync(publisherA, viewerA);
        await PairAsync(publisherB, viewerB);

        var response = await _client.SendAsync(Authorized(
            HttpMethod.Get, $"/api/devices/{publisherA.DeviceId}/pairings", publisherA.AccessToken));
        var pairings = await response.Content.ReadFromJsonAsync<List<PairingResponse>>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Single(pairings!);
        Assert.Equal(publisherA.DeviceId, pairings![0].PublisherDeviceId);
        Assert.Equal(viewerA.DeviceId, pairings[0].ViewerDeviceId);
    }

    [Fact]
    public async Task Cannot_List_Another_Devices_Pairings()
    {
        var publisherA = await BootstrapAndAuthenticateAsync("windows_publisher", "windows");
        var publisherB = await BootstrapAndAuthenticateAsync("windows_publisher", "windows");

        var response = await _client.SendAsync(Authorized(
            HttpMethod.Get, $"/api/devices/{publisherB.DeviceId}/pairings", publisherA.AccessToken));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Revoke_Removes_Pairing_From_Both_Participants_Lists()
    {
        var publisher = await BootstrapAndAuthenticateAsync("windows_publisher", "windows");
        var viewer = await BootstrapAndAuthenticateAsync("flutter_viewer", "android");
        var pairing = await PairAsync(publisher, viewer);

        var revokeResponse = await _client.SendAsync(Authorized(
            HttpMethod.Delete, $"/api/pairings/{pairing.PairingId}", viewer.AccessToken));
        Assert.Equal(HttpStatusCode.NoContent, revokeResponse.StatusCode);

        var publisherList = await (await _client.SendAsync(Authorized(
            HttpMethod.Get, $"/api/devices/{publisher.DeviceId}/pairings", publisher.AccessToken)))
            .Content.ReadFromJsonAsync<List<PairingResponse>>();

        Assert.Empty(publisherList!);
    }
}
