using Concentus.Enums;
using SonicRelay.Infrastructure.VirtualPublisher.Audio;
using Xunit;

namespace SonicRelay.VirtualPublisher.Tests;

public sealed class OpusEncoderFactoryTests
{
    [Fact]
    public void Create_configures_bitrate_and_resilience_from_the_profile()
    {
        var profile = AudioQualityProfile.Default; // stereo, 128 kbps

        var encoder = OpusEncoderFactory.Create(profile);

        Assert.Equal(128_000, encoder.Bitrate);
        Assert.True(encoder.UseVBR);
        Assert.True(encoder.UseConstrainedVBR);
        Assert.False(encoder.UseDTX);
        Assert.True(encoder.UseInbandFEC);
        Assert.Equal(profile.ExpectedPacketLossPercent, encoder.PacketLossPercent);
    }

    [Fact]
    public void Create_selects_music_signal_for_stereo_profiles()
    {
        var encoder = OpusEncoderFactory.Create(AudioQualityProfile.Default with { Id = "test" });

        Assert.Equal(OpusSignal.OPUS_SIGNAL_MUSIC, encoder.SignalType);
    }

    [Fact]
    public void Create_selects_voice_signal_for_mono_profiles()
    {
        var mono = AudioQualityProfile.Default with { Channels = 1, OpusBitrateKbps = 32 };

        var encoder = OpusEncoderFactory.Create(mono);

        Assert.Equal(OpusSignal.OPUS_SIGNAL_VOICE, encoder.SignalType);
    }

    [Fact]
    public void Create_throws_for_an_invalid_profile()
    {
        var invalid = AudioQualityProfile.Default with { OpusBitrateKbps = 1 };
        Assert.Throws<ArgumentException>(() => OpusEncoderFactory.Create(invalid));
    }
}
