namespace SonicRelay.Api.Services;

/// <summary>
/// Public radio room configuration (docs/superpowers/specs/2026-08-19-public-radio-room-design.md).
/// Bound once at startup from the "PublicRoom" section / PUBLICROOM__* environment
/// variables — there is no runtime toggle, matching every other BackgroundService
/// option in this codebase.
/// </summary>
public sealed class PublicRoomOptions
{
    public const string SectionName = "PublicRoom";

    /// <summary>Master switch. False (the default) means the feature does nothing at all.</summary>
    public bool Enabled { get; set; }

    /// <summary>Directory (inside the container) containing the *.mp3 files to play, in order.</summary>
    public string TracksPath { get; set; } = "/app/tracks";

    public int MaxViewers { get; set; } = 20;

    public int EffectiveMaxViewers => Math.Clamp(MaxViewers, 1, 500);
}
