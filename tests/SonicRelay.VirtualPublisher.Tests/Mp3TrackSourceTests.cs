using Microsoft.Extensions.Logging.Abstractions;
using SonicRelay.Infrastructure.VirtualPublisher.Audio;
using Xunit;

namespace SonicRelay.VirtualPublisher.Tests;

public sealed class Mp3TrackSourceTests
{
    private sealed class FakeDecoder : IMp3Decoder
    {
        private readonly Dictionary<string, Func<IEnumerable<short[]>>> byPath;
        public FakeDecoder(Dictionary<string, Func<IEnumerable<short[]>>> byPath) => this.byPath = byPath;

        public IEnumerable<short[]> DecodeFrames(string filePath) => byPath[Path.GetFileName(filePath)]();
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
            ["a.mp3"] = () => { order.Add("a"); return [[1, 2]]; },
            ["b.mp3"] = () => { order.Add("b"); return [[3, 4]]; },
            ["c.mp3"] = () => { order.Add("c"); return [[5, 6]]; },
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
            ["a.mp3"] = () => { visits.Add("a"); return [[1]]; },
            ["b.mp3"] = () => { visits.Add("b"); return [[2]]; },
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
            ["b.mp3"] = () => [[9]],
        });
        var source = new Mp3TrackSource(dir, decoder, NullLogger<Mp3TrackSource>.Instance);

        var frames = source.ReadForever(CancellationToken.None).Take(2).ToList();

        Assert.Equal(2, frames.Count);
        Assert.Equal((short)9, frames[0][0]);
        Assert.Equal((short)9, frames[1][0]); // looped back to b.mp3 again, a.mp3 skipped both times
    }

    [Fact]
    public void ReadForever_yields_nothing_for_an_empty_directory()
    {
        var dir = CreateTrackDirectory();
        var source = new Mp3TrackSource(dir, new FakeDecoder(new()), NullLogger<Mp3TrackSource>.Instance);

        var frames = source.ReadForever(CancellationToken.None).Take(1).ToList();

        Assert.Empty(frames);
    }

    [Fact]
    public void ReadForever_yields_nothing_for_a_missing_directory()
    {
        var missing = Path.Combine(Path.GetTempPath(), "sonicrelay-does-not-exist-" + Guid.NewGuid());
        var source = new Mp3TrackSource(missing, new FakeDecoder(new()), NullLogger<Mp3TrackSource>.Instance);

        var frames = source.ReadForever(CancellationToken.None).Take(1).ToList();

        Assert.Empty(frames);
    }
}
