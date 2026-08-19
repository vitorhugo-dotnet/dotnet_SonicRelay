using SonicRelay.Infrastructure.VirtualPublisher.Audio;
using Xunit;

namespace SonicRelay.VirtualPublisher.Tests;

public sealed class PcmAudioConverterTests
{
    [Fact]
    public void ToS16_from_pcm16_returns_samples_unchanged()
    {
        short[] expected = [100, -200, 300];
        var bytes = new byte[expected.Length * 2];
        Buffer.BlockCopy(expected, 0, bytes, 0, bytes.Length);

        var result = PcmAudioConverter.ToS16(bytes, WebRtcSourceSampleFormat.Pcm16);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void ToS16_from_float32_scales_and_clamps()
    {
        float[] input = [1.0f, -1.0f, 0.0f, 2.0f]; // 2.0f must clamp to 1.0f
        var bytes = new byte[input.Length * 4];
        Buffer.BlockCopy(input, 0, bytes, 0, bytes.Length);

        var result = PcmAudioConverter.ToS16(bytes, WebRtcSourceSampleFormat.IeeeFloat32);

        Assert.Equal([short.MaxValue, (short)(-short.MaxValue), (short)0, short.MaxValue], result);
    }

    [Fact]
    public void ToS16_from_empty_returns_empty()
    {
        Assert.Empty(PcmAudioConverter.ToS16(ReadOnlySpan<byte>.Empty, WebRtcSourceSampleFormat.Pcm16));
    }

    // Ported from the desktop suite (SonicRelay.Windows.WebRtc.Tests.PcmAudioConverterTests).
    [Fact]
    public void ToS16_ignores_trailing_partial_sample_bytes()
    {
        // 5 bytes = two whole S16 samples plus one dangling byte.
        var result = PcmAudioConverter.ToS16(new byte[] { 1, 0, 2, 0, 9 }, WebRtcSourceSampleFormat.Pcm16);
        Assert.Equal(new short[] { 1, 2 }, result);
    }
}
