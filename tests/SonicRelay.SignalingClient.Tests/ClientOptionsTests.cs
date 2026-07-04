using SonicRelay.SignalingClient;
using Xunit;

namespace SonicRelay.SignalingClient.Tests;

public sealed class ClientOptionsTests
{
    [Fact]
    public void Parse_uses_localhost_by_default()
    {
        var options = ClientOptions.Parse([]);

        Assert.Equal(new Uri("http://localhost:8080"), options.BaseUrl);
    }

    [Fact]
    public void Parse_accepts_an_absolute_http_base_url()
    {
        var options = ClientOptions.Parse(["--base-url", "https://relay.example.test/root/"]);

        Assert.Equal(new Uri("https://relay.example.test/root/"), options.BaseUrl);
    }

    [Theory]
    [InlineData("relative")]
    [InlineData("ftp://relay.example.test")]
    public void Parse_rejects_an_invalid_base_url(string value)
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            ClientOptions.Parse(["--base-url", value]));

        Assert.Contains("HTTP", exception.Message, StringComparison.OrdinalIgnoreCase);
    }
}
