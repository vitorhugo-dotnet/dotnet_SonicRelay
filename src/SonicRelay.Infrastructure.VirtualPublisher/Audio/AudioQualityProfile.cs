namespace SonicRelay.Infrastructure.VirtualPublisher.Audio;

/// <summary>
/// Fixed Opus encode profile for the public radio's virtual publisher. Unlike the
/// desktop app there is no user-selectable quality — this is always
/// stereo/128kbps/20ms/48kHz, matching the desktop's "High" preset.
/// </summary>
public sealed record AudioQualityProfile(
    string Id,
    string DisplayName,
    int Channels,
    int OpusBitrateKbps,
    int FrameDurationMs,
    int SampleRateHz)
{
    public const int MinBitrateKbps = 16;
    public const int MaxBitrateKbps = 192;
    public const int FixedSampleRateHz = 48000;

    private static readonly int[] AllowedFrameDurationsMs = [10, 20, 40];

    public int ExpectedPacketLossPercent { get; init; } = 10;

    public static AudioQualityProfile Default { get; } =
        new("high", "High quality", 2, 128, 20, FixedSampleRateHz);

    public void Validate()
    {
        if (Channels is < 1 or > 2)
            throw new ArgumentException($"Channels must be 1 or 2, was {Channels}.", nameof(Channels));
        if (OpusBitrateKbps is < MinBitrateKbps or > MaxBitrateKbps)
            throw new ArgumentException(
                $"Opus bitrate must be between {MinBitrateKbps} and {MaxBitrateKbps} kbps, was {OpusBitrateKbps}.",
                nameof(OpusBitrateKbps));
        if (Array.IndexOf(AllowedFrameDurationsMs, FrameDurationMs) < 0)
            throw new ArgumentException(
                $"Frame duration must be 10, 20, or 40 ms, was {FrameDurationMs}.", nameof(FrameDurationMs));
        if (SampleRateHz != FixedSampleRateHz)
            throw new ArgumentException(
                $"Sample rate must be {FixedSampleRateHz} Hz, was {SampleRateHz}.", nameof(SampleRateHz));
        if (ExpectedPacketLossPercent is < 0 or > 100)
            throw new ArgumentException(
                $"Expected packet loss must be between 0 and 100 percent, was {ExpectedPacketLossPercent}.",
                nameof(ExpectedPacketLossPercent));
    }
}
