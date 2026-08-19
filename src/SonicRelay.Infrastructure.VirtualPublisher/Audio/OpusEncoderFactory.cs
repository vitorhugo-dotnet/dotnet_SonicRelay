using Concentus.Enums;
using Concentus.Structs;

namespace SonicRelay.Infrastructure.VirtualPublisher.Audio;

/// <summary>
/// Builds the Concentus Opus encoder for the fixed <see cref="AudioQualityProfile"/>
/// with packet-loss resilience configured explicitly (see the desktop app's
/// equivalent factory for the full rationale on in-band FEC applicability).
/// </summary>
public static class OpusEncoderFactory
{
    public const int SampleRate = 48000;

    public static OpusEncoder Create(AudioQualityProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        profile.Validate();
        var channels = profile.Channels;
        var application = channels == 2
            ? OpusApplication.OPUS_APPLICATION_AUDIO
            : OpusApplication.OPUS_APPLICATION_VOIP;
        return new OpusEncoder(SampleRate, channels, application)
        {
            Bitrate = profile.OpusBitrateKbps * 1000,
            Complexity = 10,
            SignalType = channels == 2 ? OpusSignal.OPUS_SIGNAL_MUSIC : OpusSignal.OPUS_SIGNAL_VOICE,
            UseVBR = true,
            UseConstrainedVBR = true,
            UseDTX = false,
            UseInbandFEC = true,
            PacketLossPercent = profile.ExpectedPacketLossPercent,
        };
    }
}
