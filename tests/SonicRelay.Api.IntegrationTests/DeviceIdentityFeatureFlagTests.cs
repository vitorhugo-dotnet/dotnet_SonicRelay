using System.Net;
using System.Net.Http.Json;
using SonicRelay.Api.Contracts;
using Xunit;

namespace SonicRelay.Api.IntegrationTests;

public sealed class DeviceIdentityFeatureFlagTests
{
    [Fact]
    public async Task Disabling_DeviceIdentity_Removes_New_Endpoints_Without_Affecting_Identity_Auth()
    {
        await using var factory = new SonicRelayApiFactory(new Dictionary<string, string?>
        {
            ["DeviceIdentity:Enabled"] = "false"
        });
        var client = factory.CreateClient();

        var bootstrapResponse = await client.PostAsJsonAsync("/api/devices/bootstrap",
            new BootstrapDeviceRequest("Device", "windows_publisher", "windows"));

        // "/api/devices/bootstrap" shares its route shape with the pre-existing,
        // unconditionally-mapped "/api/devices/{deviceId:guid}" owner-scoped
        // routes (GET/PATCH/DELETE), which stay mapped regardless of this flag.
        // ASP.NET Core's router reports 405 for a POST at that shape rather than
        // 404, even though no device-identity handler ever runs and nothing is
        // created — both status codes correctly mean "unavailable", so either is
        // acceptable here; what matters is that it is never a success status.
        Assert.True(
            bootstrapResponse.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed,
            $"Expected the disabled bootstrap endpoint to be unavailable (404 or 405), but got {bootstrapResponse.StatusCode}.");

        var meResponse = await client.GetAsync("/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, meResponse.StatusCode);

        // Guards against re-introducing the exact bug this test once caught:
        // MapDeviceEndpoints() (the pre-existing, unrelated owner-scoped Device
        // CRUD) must stay mapped regardless of this flag. If it were ever
        // re-gated behind DeviceIdentity:Enabled, this unauthenticated GET would
        // return 404 (route gone) instead of 401 (route present, auth required).
        var devicesResponse = await client.GetAsync("/api/devices");
        Assert.Equal(HttpStatusCode.Unauthorized, devicesResponse.StatusCode);
    }
}
