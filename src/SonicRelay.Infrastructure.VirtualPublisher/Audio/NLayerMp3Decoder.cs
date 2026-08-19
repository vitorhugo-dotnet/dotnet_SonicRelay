using NLayer;

namespace SonicRelay.Infrastructure.VirtualPublisher.Audio;

/// <summary>
/// Decodes an MP3 file to interleaved S16 PCM frames using NLayer (a managed
/// decoder — no native dependency, matching the rest of this project's stack).
/// Reads in ~20 ms chunks so <see cref="Mp3TrackSource"/> can interleave decoding
/// across the whole playlist rotation instead of loading a full track at once.
/// </summary>
public sealed class NLayerMp3Decoder : IMp3Decoder
{
    private const int SamplesPerChunkPerChannel = 960; // 20 ms at 48 kHz

    public IEnumerable<Mp3Frame> DecodeFrames(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        using var mpegFile = new MpegFile(stream);
        var channels = mpegFile.Channels;
        var sampleRate = mpegFile.SampleRate;
        var chunkFloats = new float[SamplesPerChunkPerChannel * channels];

        while (true)
        {
            var read = mpegFile.ReadSamples(chunkFloats, 0, chunkFloats.Length);
            if (read <= 0) yield break;

            var pcm = new short[read];
            for (var i = 0; i < read; i++)
            {
                pcm[i] = (short)Math.Round(Math.Clamp(chunkFloats[i], -1f, 1f) * short.MaxValue);
            }
            yield return new Mp3Frame(pcm, sampleRate, channels);
        }
    }
}
