using Microsoft.Extensions.Configuration;
using SonicRelay.Api.Services;
using Xunit;

namespace SonicRelay.Api.IntegrationTests;

public sealed class PublicRoomOptionsTests
{
    [Fact]
    public void Binds_from_configuration_with_documented_defaults()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["PublicRoom:Enabled"] = "true",
            ["PublicRoom:TracksPath"] = "/app/tracks",
        }).Build();

        var options = new PublicRoomOptions();
        configuration.GetSection("PublicRoom").Bind(options);

        Assert.True(options.Enabled);
        Assert.Equal("/app/tracks", options.TracksPath);
        Assert.Equal(20, options.MaxViewers); // not set above -> default
    }

    [Fact]
    public void Defaults_to_disabled_when_the_section_is_absent()
    {
        var configuration = new ConfigurationBuilder().Build();

        var options = new PublicRoomOptions();
        configuration.GetSection("PublicRoom").Bind(options);

        Assert.False(options.Enabled);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1000, 500)]
    [InlineData(20, 20)]
    public void EffectiveMaxViewers_clamps_to_the_supported_range(int configured, int expected)
    {
        var options = new PublicRoomOptions { MaxViewers = configured };
        Assert.Equal(expected, options.EffectiveMaxViewers);
    }
}
