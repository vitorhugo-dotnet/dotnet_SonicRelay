namespace SonicRelay.Infrastructure.VirtualPublisher.WebRtc;

public sealed record WebRtcSessionDescription(string Type, string Sdp);

public sealed record WebRtcIceCandidate(string Candidate, string? SdpMid = null, int? SdpMLineIndex = null);

public sealed class WebRtcAudioFrame
{
    private readonly byte[] data;

    public WebRtcAudioFrame(ReadOnlySpan<byte> data, int sampleRate, int channelCount)
    {
        if (data.IsEmpty) throw new ArgumentException("Audio frame data cannot be empty.", nameof(data));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        if (channelCount is < 1 or > 2) throw new ArgumentOutOfRangeException(nameof(channelCount));
        this.data = data.ToArray();
        SampleRate = sampleRate;
        ChannelCount = channelCount;
    }

    public ReadOnlyMemory<byte> Data => data;
    public int SampleRate { get; }
    public int ChannelCount { get; }
}
