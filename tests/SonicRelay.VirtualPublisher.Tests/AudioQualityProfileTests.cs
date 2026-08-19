using SonicRelay.Infrastructure.VirtualPublisher.Audio;
using Xunit;

namespace SonicRelay.VirtualPublisher.Tests;

public sealed class AudioQualityProfileTests
{
    [Fact]
    public void Default_is_stereo_128kbps_20ms_48khz()
    {
        var profile = AudioQualityProfile.Default;

        Assert.Equal(2, profile.Channels);
        Assert.Equal(128, profile.OpusBitrateKbps);
        Assert.Equal(20, profile.FrameDurationMs);
        Assert.Equal(48000, profile.SampleRateHz);
        profile.Validate(); // does not throw
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public void Validate_rejects_invalid_channel_count(int channels)
    {
        var profile = AudioQualityProfile.Default with { Channels = channels };
        Assert.Throws<ArgumentException>(profile.Validate);
    }

    [Fact]
    public void Validate_rejects_bitrate_out_of_range()
    {
        var profile = AudioQualityProfile.Default with { OpusBitrateKbps = 8 };
        Assert.Throws<ArgumentException>(profile.Validate);
    }

    [Fact]
    public void Validate_rejects_unsupported_frame_duration()
    {
        var profile = AudioQualityProfile.Default with { FrameDurationMs = 15 };
        Assert.Throws<ArgumentException>(profile.Validate);
    }
}
