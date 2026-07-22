using Microsoft.Extensions.Options;
using SonicRelay.Api.Services;
using SonicRelay.Domain.Devices;
using Xunit;

namespace SonicRelay.Api.IntegrationTests;

public sealed class DeviceCredentialServiceTests
{
    private static DeviceCredentialService CreateService() => new(
        Options.Create(new DeviceIdentityOptions
        {
            CredentialHmacKey = "unit-test-credential-hmac-key",
            TokenSigningKey = "unit-test-token-signing-key-needs-32-bytes-min"
        }),
        TimeProvider.System);

    [Fact]
    public void GenerateCredential_Produces_DistinctSecrets_And_VerifiableHash()
    {
        var service = CreateService();
        var (secretA, hashA) = service.GenerateCredential();
        var (secretB, _) = service.GenerateCredential();

        Assert.NotEqual(secretA, secretB);
        Assert.True(service.VerifySecret(secretA, hashA));
    }

    [Fact]
    public void VerifySecret_Rejects_WrongSecret()
    {
        var service = CreateService();
        var (_, hash) = service.GenerateCredential();

        Assert.False(service.VerifySecret("not-the-secret", hash));
    }

    [Theory]
    [InlineData(DeviceTypes.WindowsPublisher, "pairing:create")]
    [InlineData(DeviceTypes.FlutterViewer, "pairing:complete")]
    public void ScopesFor_Grants_TypeSpecificScope(string deviceType, string expectedScope)
    {
        var scopes = DeviceCredentialService.ScopesFor(deviceType);

        Assert.Contains(expectedScope, scopes);
        Assert.Contains("device:read", scopes);
        Assert.Contains("device:manage", scopes);
    }
}
