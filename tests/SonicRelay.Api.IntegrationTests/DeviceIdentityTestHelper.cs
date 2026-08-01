using System.Net.Http.Headers;
using System.Net.Http.Json;
using SonicRelay.Api.Contracts;

namespace SonicRelay.Api.IntegrationTests;

public sealed record DeviceIdentitySession(Guid DeviceId, string AccessToken, HttpClient Client);

public static class DeviceIdentityTestHelper
{
    public static async Task<DeviceIdentitySession> BootstrapAndAuthorizeAsync(
        HttpClient client, string deviceType, string platform, string name = "Test device")
    {
        var bootstrapResponse = await client.PostAsJsonAsync("/api/devices/bootstrap",
            new BootstrapDeviceRequest(name, deviceType, platform));
        bootstrapResponse.EnsureSuccessStatusCode();
        var bootstrap = await bootstrapResponse.Content.ReadFromJsonAsync<BootstrapDeviceResponse>();

        var tokenResponse = await client.PostAsJsonAsync("/api/devices/token",
            new DeviceTokenRequest(bootstrap!.DeviceId, bootstrap.CredentialSecret));
        tokenResponse.EnsureSuccessStatusCode();
        var token = await tokenResponse.Content.ReadFromJsonAsync<DeviceTokenResponse>();

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token!.AccessToken);
        return new DeviceIdentitySession(bootstrap.DeviceId, token.AccessToken, client);
    }
}
