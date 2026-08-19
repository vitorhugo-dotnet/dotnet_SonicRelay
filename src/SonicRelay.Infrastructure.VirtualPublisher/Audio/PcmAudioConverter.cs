using System.Runtime.InteropServices;

namespace SonicRelay.Infrastructure.VirtualPublisher.Audio;

/// <summary>Sample format of the raw bytes handed to <see cref="PcmAudioConverter.ToS16"/>.</summary>
public enum WebRtcSourceSampleFormat
{
    Pcm16,
    IeeeFloat32
}

/// <summary>Converts raw PCM buffers to the S16LE interleaved samples the Opus encoder consumes.</summary>
public static class PcmAudioConverter
{
    public static short[] ToS16(ReadOnlySpan<byte> data, WebRtcSourceSampleFormat format)
    {
        if (data.IsEmpty) return [];
        switch (format)
        {
            case WebRtcSourceSampleFormat.Pcm16:
                return MemoryMarshal.Cast<byte, short>(data[..(data.Length - data.Length % 2)]).ToArray();
            case WebRtcSourceSampleFormat.IeeeFloat32:
                var floats = MemoryMarshal.Cast<byte, float>(data[..(data.Length - data.Length % 4)]);
                var samples = new short[floats.Length];
                for (var i = 0; i < floats.Length; i++)
                {
                    var clamped = Math.Clamp(floats[i], -1f, 1f);
                    samples[i] = (short)Math.Round(clamped >= 0 ? clamped * short.MaxValue : clamped * -short.MinValue);
                }
                return samples;
            default:
                throw new ArgumentOutOfRangeException(nameof(format));
        }
    }
}
