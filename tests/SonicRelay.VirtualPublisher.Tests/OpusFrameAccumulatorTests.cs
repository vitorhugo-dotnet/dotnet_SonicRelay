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

    // --- Ported from the desktop suite (SonicRelay.Windows.WebRtc.Tests.OpusFrameAccumulatorTests) ---

    [Fact]
    public void Emits_exact_20ms_stereo_frames_from_ragged_packets()
    {
        var accumulator = new OpusFrameAccumulator(48000, 2);
        // 960 samples/channel * 2 channels = 1920 shorts per 20 ms frame.
        // Feed three ragged packets that together make two full frames.
        accumulator.Append(MakeStereo(500), 48000, 2);
        Assert.False(accumulator.TryTakeFrame(out _));
        accumulator.Append(MakeStereo(500), 48000, 2);
        accumulator.Append(MakeStereo(200), 48000, 2); // total 1200 frames -> one 960 frame + 240 remainder

        Assert.True(accumulator.TryTakeFrame(out var frame));
        Assert.Equal(1920, frame.Length);
        Assert.False(accumulator.TryTakeFrame(out _));
    }

    [Fact]
    public void Downmixes_stereo_source_when_target_is_mono()
    {
        var accumulator = new OpusFrameAccumulator(48000, 1);
        var stereo = new short[960 * 2];
        for (var i = 0; i < 960; i++) { stereo[i * 2] = 100; stereo[i * 2 + 1] = 200; }
        accumulator.Append(stereo, 48000, 2);

        Assert.True(accumulator.TryTakeFrame(out var frame));
        Assert.Equal(960, frame.Length);
        Assert.All(frame, sample => Assert.Equal(150, sample));
    }

    [Fact]
    public void Resamples_44100_to_48000_producing_target_length_frames()
    {
        var accumulator = new OpusFrameAccumulator(48000, 2);
        // 44100 Hz -> 20 ms frame = 882 samples/channel of source produces 960/channel target.
        accumulator.Append(MakeStereo(882), 44100, 2);

        Assert.True(accumulator.TryTakeFrame(out var frame));
        Assert.Equal(1920, frame.Length);
    }

    [Fact]
    public void Source_rate_change_discards_stale_buffer()
    {
        var accumulator = new OpusFrameAccumulator(48000, 2);
        accumulator.Append(MakeStereo(400), 48000, 2);
        // A device switch changes the rate before a full frame accumulated.
        accumulator.Append(MakeStereo(882), 44100, 2);

        Assert.True(accumulator.TryTakeFrame(out var frame));
        Assert.Equal(1920, frame.Length);
    }

    [Fact]
    public void Rejects_sample_rate_not_divisible_by_50()
    {
        var accumulator = new OpusFrameAccumulator(48000, 2);
        Assert.Throws<ArgumentException>(() => accumulator.Append(MakeStereo(10), 44101, 2));
    }

    [Fact]
    public void Emits_10ms_stereo_frames_when_configured()
    {
        // 10 ms stereo at 48 kHz = 480 samples/channel * 2 = 960 shorts per frame.
        var accumulator = new OpusFrameAccumulator(48000, 2, frameDurationMs: 10);
        Assert.Equal(960, accumulator.TargetFrameSize);
        accumulator.Append(MakeStereo(480), 48000, 2);

        Assert.True(accumulator.TryTakeFrame(out var frame));
        Assert.Equal(960, frame.Length);
        Assert.False(accumulator.TryTakeFrame(out _));
    }

    [Fact]
    public void Emits_40ms_stereo_frames_when_configured()
    {
        // 40 ms stereo at 48 kHz = 1920 samples/channel * 2 = 3840 shorts per frame.
        var accumulator = new OpusFrameAccumulator(48000, 2, frameDurationMs: 40);
        accumulator.Append(MakeStereo(1920), 48000, 2);

        Assert.True(accumulator.TryTakeFrame(out var frame));
        Assert.Equal(3840, frame.Length);
    }

    [Fact]
    public void Rejects_invalid_frame_duration()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new OpusFrameAccumulator(48000, 2, frameDurationMs: 15));
    }

    private static short[] MakeStereo(int framesPerChannel)
    {
        var data = new short[framesPerChannel * 2];
        for (var i = 0; i < data.Length; i++) data[i] = (short)(i % 100);
        return data;
    }
}
