using System.Net;
using SonicRelay.Domain.Devices;
using Xunit;

namespace SonicRelay.Api.IntegrationTests;

public sealed class DeviceIdentityTestHelperTests : IClassFixture<SonicRelayApiFactory>
{
    private readonly SonicRelayApiFactory _factory;

    public DeviceIdentityTestHelperTests(SonicRelayApiFactory factory) => _factory = factory;

    [Fact]
    public async Task BootstrapAndAuthorizeAsync_Returns_A_Usable_DeviceBearer_Token()
    {
        var client = _factory.CreateClient();

        var session = await DeviceIdentityTestHelper.BootstrapAndAuthorizeAsync(
            client, DeviceTypes.WindowsPublisher, DevicePlatforms.Windows);

        Assert.NotEqual(Guid.Empty, session.DeviceId);
        Assert.False(string.IsNullOrWhiteSpace(session.AccessToken));

        // device:manage is granted to every device type, so a successful call here proves the
        // returned access token is a genuinely valid, scoped DeviceBearer credential — not just
        // a non-empty string.
        var revokeResponse = await client.PostAsync("/api/devices/revoke", null);
        Assert.Equal(HttpStatusCode.NoContent, revokeResponse.StatusCode);
    }
}
