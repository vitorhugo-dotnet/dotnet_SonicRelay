using System.Net;
using System.Net.Http.Json;
using SonicRelay.Api.Contracts;
using Xunit;

namespace SonicRelay.Api.IntegrationTests;

public sealed class DeviceBootstrapAndTokenTests : IClassFixture<SonicRelayApiFactory>
{
    private readonly HttpClient _client;

    public DeviceBootstrapAndTokenTests(SonicRelayApiFactory factory) => _client = factory.CreateClient();

    [Fact]
    public async Task Bootstrap_Then_Token_Issues_AccessToken_With_TypeScopes()
    {
        var bootstrapResponse = await _client.PostAsJsonAsync("/api/devices/bootstrap",
            new BootstrapDeviceRequest("Living Room PC", "windows_publisher", "windows"));
        Assert.Equal(HttpStatusCode.Created, bootstrapResponse.StatusCode);
        var bootstrap = await bootstrapResponse.Content.ReadFromJsonAsync<BootstrapDeviceResponse>();
        Assert.NotNull(bootstrap);

        var tokenResponse = await _client.PostAsJsonAsync("/api/devices/token",
            new DeviceTokenRequest(bootstrap!.DeviceId, bootstrap.CredentialSecret));
        Assert.Equal(HttpStatusCode.OK, tokenResponse.StatusCode);
        var token = await tokenResponse.Content.ReadFromJsonAsync<DeviceTokenResponse>();
        Assert.NotNull(token);
        Assert.False(string.IsNullOrWhiteSpace(token!.AccessToken));
        Assert.Contains("pairing:create", token.Scopes);
    }

    [Fact]
    public async Task Token_Rejects_WrongSecret()
    {
        var bootstrapResponse = await _client.PostAsJsonAsync("/api/devices/bootstrap",
            new BootstrapDeviceRequest("Phone", "flutter_viewer", "android"));
        var bootstrap = await bootstrapResponse.Content.ReadFromJsonAsync<BootstrapDeviceResponse>();

        var tokenResponse = await _client.PostAsJsonAsync("/api/devices/token",
            new DeviceTokenRequest(bootstrap!.DeviceId, "wrong-secret"));

        Assert.Equal(HttpStatusCode.Unauthorized, tokenResponse.StatusCode);
    }

    [Fact]
    public async Task Token_Rejects_UnknownDevice_With_SameStatusCode_As_WrongSecret()
    {
        var response = await _client.PostAsJsonAsync("/api/devices/token",
            new DeviceTokenRequest(Guid.NewGuid(), "does-not-matter"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Bootstrap_Rejects_Invalid_TypePlatform_Combination()
    {
        var response = await _client.PostAsJsonAsync("/api/devices/bootstrap",
            new BootstrapDeviceRequest("Bad", "windows_publisher", "android"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
