using SonicRelay.Infrastructure.VirtualPublisher.Audio;
using Xunit;

namespace SonicRelay.VirtualPublisher.Tests;

public sealed class OpusFrameAccumulatorTests
{
    [Fact]
    public void TryTakeFrame_returns_false_when_not_enough_samples_buffered()
    {
        var accumulator = new OpusFrameAccumulator(targetSampleRate: 48000, targetChannels: 2, frameDurationMs: 20);
        accumulator.Append([1, 2, 3, 4], sampleRate: 48000, channelCount: 2); // far short of 960*2

        Assert.False(accumulator.TryTakeFrame(out _));
    }

    [Fact]
    public void TryTakeFrame_emits_exact_frame_size_at_matching_rate()
    {
        var accumulator = new OpusFrameAccumulator(targetSampleRate: 48000, targetChannels: 2, frameDurationMs: 20);
        var samplesPerChannel = 48000 * 20 / 1000; // 960
        var samples = new short[samplesPerChannel * 2];
        for (var i = 0; i < samples.Length; i++) samples[i] = (short)i;

        accumulator.Append(samples, sampleRate: 48000, channelCount: 2);

        Assert.True(accumulator.TryTakeFrame(out var frame));
        Assert.Equal(samplesPerChannel * 2, frame.Length);
        Assert.Equal(samples, frame);
        Assert.False(accumulator.TryTakeFrame(out _)); // buffer now empty
    }

    [Fact]
    public void Append_upmixes_mono_to_stereo_by_duplicating_each_sample()
    {
        var accumulator = new OpusFrameAccumulator(targetSampleRate: 48000, targetChannels: 2, frameDurationMs: 20);
        var samplesPerChannel = 48000 * 20 / 1000;
        var mono = new short[samplesPerChannel];
        for (var i = 0; i < mono.Length; i++) mono[i] = (short)(i + 1);

        accumulator.Append(mono, sampleRate: 48000, channelCount: 1);

        Assert.True(accumulator.TryTakeFrame(out var frame));
        Assert.Equal(samplesPerChannel * 2, frame.Length);
        Assert.Equal(mono[0], frame[0]);
        Assert.Equal(mono[0], frame[1]); // left == right for the first source sample
    }

    [Fact]
    public void Clear_discards_buffered_samples()
    {
        var accumulator = new OpusFrameAccumulator(targetSampleRate: 48000, targetChannels: 2, frameDurationMs: 20);
        accumulator.Append([1, 2, 3, 4], sampleRate: 48000, channelCount: 2);

        accumulator.Clear();

        Assert.False(accumulator.TryTakeFrame(out _));
    }
}
