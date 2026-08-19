using Microsoft.Extensions.Logging.Abstractions;
using SonicRelay.Infrastructure.VirtualPublisher.Audio;
using Xunit;

namespace SonicRelay.VirtualPublisher.Tests;

public sealed class Mp3TrackSourceTests
{
    private sealed class FakeDecoder : IMp3Decoder
    {
        private readonly Dictionary<string, Func<IEnumerable<Mp3Frame>>> byPath;
        public FakeDecoder(Dictionary<string, Func<IEnumerable<Mp3Frame>>> byPath) => this.byPath = byPath;

        public IEnumerable<Mp3Frame> DecodeFrames(string filePath) => byPath[Path.GetFileName(filePath)]();
    }

    private static string CreateTrackDirectory(params string[] fileNames)
    {
        var dir = Path.Combine(Path.GetTempPath(), "sonicrelay-track-tests-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        foreach (var name in fileNames) File.WriteAllBytes(Path.Combine(dir, name), [0]);
        return dir;
    }

    [Fact]
    public void ReadForever_visits_mp3_files_in_alphabetical_order()
    {
        var dir = CreateTrackDirectory("b.mp3", "a.mp3", "c.mp3");
        var order = new List<string>();
        var decoder = new FakeDecoder(new()
        {
            ["a.mp3"] = () => { order.Add("a"); return [new Mp3Frame([1, 2], 48000, 2)]; },
            ["b.mp3"] = () => { order.Add("b"); return [new Mp3Frame([3, 4], 48000, 2)]; },
            ["c.mp3"] = () => { order.Add("c"); return [new Mp3Frame([5, 6], 48000, 2)]; },
        });
        var source = new Mp3TrackSource(dir, decoder, NullLogger<Mp3TrackSource>.Instance);

        var frames = source.ReadForever(CancellationToken.None).Take(3).ToList();

        Assert.Equal(["a", "b", "c"], order);
        Assert.Equal(3, frames.Count);
    }

    [Fact]
    public void ReadForever_loops_back_to_the_first_track_after_the_last()
    {
        var dir = CreateTrackDirectory("a.mp3", "b.mp3");
        var visits = new List<string>();
        var decoder = new FakeDecoder(new()
        {
            ["a.mp3"] = () => { visits.Add("a"); return [new Mp3Frame([1], 48000, 2)]; },
            ["b.mp3"] = () => { visits.Add("b"); return [new Mp3Frame([2], 48000, 2)]; },
        });
        var source = new Mp3TrackSource(dir, decoder, NullLogger<Mp3TrackSource>.Instance);

        source.ReadForever(CancellationToken.None).Take(5).ToList();

        Assert.Equal(["a", "b", "a", "b", "a"], visits);
    }

    [Fact]
    public void ReadForever_skips_a_track_whose_decoder_throws_and_continues_with_the_next()
    {
        var dir = CreateTrackDirectory("a.mp3", "b.mp3");
        var decoder = new FakeDecoder(new()
        {
            ["a.mp3"] = () => throw new InvalidOperationException("corrupt"),
            ["b.mp3"] = () => [new Mp3Frame([9], 48000, 2)],
        });
        var source = new Mp3TrackSource(dir, decoder, NullLogger<Mp3TrackSource>.Instance);

        var frames = source.ReadForever(CancellationToken.None).Take(2).ToList();

        Assert.Equal(2, frames.Count);
        Assert.Equal((short)9, frames[0].Samples[0]);
        Assert.Equal((short)9, frames[1].Samples[0]); // looped back to b.mp3 again, a.mp3 skipped both times
    }

    [Fact]
    public void ReadForever_preserves_the_decoded_frames_sample_rate_and_channel_count()
    {
        var dir = CreateTrackDirectory("a.mp3");
        var decoder = new FakeDecoder(new()
        {
            ["a.mp3"] = () => [new Mp3Frame([1, 2], 44100, 1)],
        });
        var source = new Mp3TrackSource(dir, decoder, NullLogger<Mp3TrackSource>.Instance);

        var frame = source.ReadForever(CancellationToken.None).Take(1).ToList().Single();

        Assert.Equal(44100, frame.SampleRate);
        Assert.Equal(1, frame.Channels);
    }

    [Fact]
    public void ReadForever_yields_nothing_for_an_empty_directory()
    {
        var dir = CreateTrackDirectory();
        var source = new Mp3TrackSource(dir, new FakeDecoder(new()), NullLogger<Mp3TrackSource>.Instance);

        // The directory is (and stays) empty, so ReadForever never has a frame to yield. It
        // idles internally (waiting up to IdleRetryDelay between re-scans) rather than ending
        // the sequence, so we cancel shortly after starting to unblock the wait and let the
        // iterator's own "while (!cancellationToken.IsCancellationRequested)" end enumeration
        // normally. ToList() draining to completion with zero items proves no frame was ever
        // produced before that happened.
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        var frames = source.ReadForever(cts.Token).ToList();

        Assert.Empty(frames);
    }

    [Fact]
    public void ReadForever_yields_nothing_for_a_missing_directory()
    {
        var missing = Path.Combine(Path.GetTempPath(), "sonicrelay-does-not-exist-" + Guid.NewGuid());
        var source = new Mp3TrackSource(missing, new FakeDecoder(new()), NullLogger<Mp3TrackSource>.Instance);

        // Same reasoning as the empty-directory case above: a missing directory also never
        // yields a frame, so we cancel shortly after starting and drain to completion.
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        var frames = source.ReadForever(cts.Token).ToList();

        Assert.Empty(frames);
    }
}
