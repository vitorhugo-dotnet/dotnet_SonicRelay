using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using SonicRelay.Api.Contracts;
using Xunit;

namespace SonicRelay.Api.IntegrationTests;

public sealed class PairingChallengeTests : IClassFixture<SonicRelayApiFactory>
{
    private readonly HttpClient _client;

    public PairingChallengeTests(SonicRelayApiFactory factory) => _client = factory.CreateClient();

    private async Task<string> BootstrapAndAuthenticateAsync(string type, string platform)
    {
        var bootstrap = await (await _client.PostAsJsonAsync("/api/devices/bootstrap",
            new BootstrapDeviceRequest("Device", type, platform)))
            .Content.ReadFromJsonAsync<BootstrapDeviceResponse>();
        var token = await (await _client.PostAsJsonAsync("/api/devices/token",
            new DeviceTokenRequest(bootstrap!.DeviceId, bootstrap.CredentialSecret)))
            .Content.ReadFromJsonAsync<DeviceTokenResponse>();
        return token!.AccessToken;
    }

    private HttpRequestMessage Authorized(HttpMethod method, string url, string accessToken, object? body = null)
    {
        var request = new HttpRequestMessage(method, url);
        if (body is not null) request.Content = JsonContent.Create(body);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return request;
    }

    [Fact]
    public async Task Publisher_Creates_Challenge_And_Viewer_Completes_Pairing()
    {
        var publisherToken = await BootstrapAndAuthenticateAsync("windows_publisher", "windows");
        var viewerToken = await BootstrapAndAuthenticateAsync("flutter_viewer", "android");

        var challengeResponse = await _client.SendAsync(
            Authorized(HttpMethod.Post, "/api/pairings/challenges", publisherToken));
        Assert.Equal(HttpStatusCode.Created, challengeResponse.StatusCode);
        var challenge = await challengeResponse.Content.ReadFromJsonAsync<CreateChallengeResponse>();
        Assert.NotNull(challenge);
        Assert.Contains(challenge!.ChallengeId.ToString(), challenge.QrPayload);

        var completeResponse = await _client.SendAsync(Authorized(HttpMethod.Post, "/api/pairings/complete",
            viewerToken, new CompletePairingRequest(challenge.ChallengeId, challenge.Code)));

        Assert.Equal(HttpStatusCode.OK, completeResponse.StatusCode);
        var pairing = await completeResponse.Content.ReadFromJsonAsync<PairingResponse>();
        Assert.Equal("active", pairing!.Status);
    }

    [Fact]
    public async Task Complete_Rejects_WrongCode_Without_Revealing_Reason()
    {
        var publisherToken = await BootstrapAndAuthenticateAsync("windows_publisher", "windows");
        var viewerToken = await BootstrapAndAuthenticateAsync("flutter_viewer", "android");
        var challengeResponse = await _client.SendAsync(
            Authorized(HttpMethod.Post, "/api/pairings/challenges", publisherToken));
        var challenge = await challengeResponse.Content.ReadFromJsonAsync<CreateChallengeResponse>();

        var wrongCode = await _client.SendAsync(Authorized(HttpMethod.Post, "/api/pairings/complete",
            viewerToken, new CompletePairingRequest(challenge!.ChallengeId, "WRONGCODE")));
        var unknownChallenge = await _client.SendAsync(Authorized(HttpMethod.Post, "/api/pairings/complete",
            viewerToken, new CompletePairingRequest(Guid.NewGuid(), challenge.Code)));

        Assert.Equal(HttpStatusCode.BadRequest, wrongCode.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, unknownChallenge.StatusCode);

        var wrongCodeBody = await wrongCode.Content.ReadAsStringAsync();
        var unknownChallengeBody = await unknownChallenge.Content.ReadAsStringAsync();

        // The two failure modes (wrong code vs. unknown/expired challenge id) must be
        // indistinguishable to the caller: identical status code AND identical body.
        // If the bodies ever diverged, that divergence itself would leak which case applied.
        Assert.Equal("{\"error\":\"Invalid or expired pairing code.\"}", wrongCodeBody);
        Assert.Equal(wrongCodeBody, unknownChallengeBody);
    }

    [Fact]
    public async Task Complete_Rejects_AlreadyConsumed_Challenge_With_Same_Generic_Error()
    {
        var publisherToken = await BootstrapAndAuthenticateAsync("windows_publisher", "windows");
        var firstViewerToken = await BootstrapAndAuthenticateAsync("flutter_viewer", "android");
        var secondViewerToken = await BootstrapAndAuthenticateAsync("flutter_viewer", "android");

        var challengeResponse = await _client.SendAsync(
            Authorized(HttpMethod.Post, "/api/pairings/challenges", publisherToken));
        var challenge = await challengeResponse.Content.ReadFromJsonAsync<CreateChallengeResponse>();

        // Consume the challenge once, successfully, with a fresh viewer device.
        var firstComplete = await _client.SendAsync(Authorized(HttpMethod.Post, "/api/pairings/complete",
            firstViewerToken, new CompletePairingRequest(challenge!.ChallengeId, challenge.Code)));
        Assert.Equal(HttpStatusCode.OK, firstComplete.StatusCode);

        // A different, freshly-bootstrapped viewer device attempts to reuse the same
        // (now-consumed) challenge id/code pair.
        var reuseAttempt = await _client.SendAsync(Authorized(HttpMethod.Post, "/api/pairings/complete",
            secondViewerToken, new CompletePairingRequest(challenge.ChallengeId, challenge.Code)));

        Assert.Equal(HttpStatusCode.BadRequest, reuseAttempt.StatusCode);
        var reuseAttemptBody = await reuseAttempt.Content.ReadAsStringAsync();
        Assert.Equal("{\"error\":\"Invalid or expired pairing code.\"}", reuseAttemptBody);

        // Note: this deliberately does not cover the expiry branch of IsUsable — doing so
        // would require faking TimeProvider, which SonicRelayApiFactory doesn't currently
        // wire up for this feature. That remains a known, documented gap.
    }

    [Fact]
    public async Task Complete_Rejects_Code_After_MaxAttempts()
    {
        var publisherToken = await BootstrapAndAuthenticateAsync("windows_publisher", "windows");
        var viewerToken = await BootstrapAndAuthenticateAsync("flutter_viewer", "android");
        var challengeResponse = await _client.SendAsync(
            Authorized(HttpMethod.Post, "/api/pairings/challenges", publisherToken));
        var challenge = await challengeResponse.Content.ReadFromJsonAsync<CreateChallengeResponse>();

        for (var i = 0; i < 5; i++)
        {
            await _client.SendAsync(Authorized(HttpMethod.Post, "/api/pairings/complete",
                viewerToken, new CompletePairingRequest(challenge!.ChallengeId, "WRONGCODE")));
        }

        var finalAttempt = await _client.SendAsync(Authorized(HttpMethod.Post, "/api/pairings/complete",
            viewerToken, new CompletePairingRequest(challenge!.ChallengeId, challenge.Code)));

        Assert.Equal(HttpStatusCode.BadRequest, finalAttempt.StatusCode);
    }

    [Fact]
    public async Task Only_PublisherDevices_Can_Create_Challenges()
    {
        var viewerToken = await BootstrapAndAuthenticateAsync("flutter_viewer", "android");

        var response = await _client.SendAsync(
            Authorized(HttpMethod.Post, "/api/pairings/challenges", viewerToken));

        // A viewer's token never carries the "pairing:create" scope (see
        // DeviceCredentialService.ScopesFor in Task 2), so the "pairing:create"
        // policy itself rejects the request before the handler runs.
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
