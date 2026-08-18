namespace SonicRelay.Api.Services;

/// <summary>
/// Central retention policy for every piece of data the backend persists (issue #44).
/// The Google Play Data Safety declaration "user data is deleted automatically within
/// 90 days" is only truthful if this policy is enforced from the moment a row is
/// <em>collected</em>, not from its last activity — so every sweep measures age from the
/// row's own creation timestamp and never resets that clock.
/// </summary>
public sealed class DataRetentionOptions
{
    public const string SectionName = "DataRetention";

    /// <summary>Set false only for tests or a maintenance window; production must run the sweep.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Hard ceiling declared to users. Nothing linkable may outlive it.</summary>
    public int MaxRetentionDays { get; set; } = 90;

    /// <summary>
    /// Deleted this many days *before* <see cref="MaxRetentionDays"/> so a late scheduler,
    /// a short outage or a clock difference cannot push a row past the declared ceiling.
    /// </summary>
    public int SafetyMarginDays { get; set; } = 1;

    /// <summary>
    /// How long the operator's PostgreSQL backups are kept. Deleting a row from the primary
    /// database does nothing for the user if a backup still holds it, so the database cutoff is
    /// pulled forward by this much: the last backup that could still contain a row is itself
    /// destroyed before the row reaches <see cref="MaxRetentionDays"/>. Must match the backup
    /// schedule actually configured for the deployment (see docs/data-retention.md) — setting it
    /// lower than reality is what silently breaks the declaration.
    /// </summary>
    public int BackupRetentionDays { get; set; } = 7;

    public int CleanupIntervalHours { get; set; } = 24;

    /// <summary>
    /// Age at which a still-active device identity is replaced by a brand-new identifier.
    /// Comfortably below <see cref="MaxRetentionDays"/> so a device that keeps using the
    /// service rotates transparently instead of being deleted out from under a live user.
    /// </summary>
    public int DeviceIdentityRotationDays { get; set; } = 60;

    /// <summary>
    /// Consumed/expired pairing challenges hold a code hash bound to a publisher device and
    /// are useless once the challenge is over, so they die on a far shorter clock.
    /// </summary>
    public int PairingChallengeRetentionHours { get; set; } = 24;

    /// <summary>
    /// Rows removed per entity per pass. Bounds the transaction size on a large backlog;
    /// the sweep simply catches up on the next tick.
    /// </summary>
    public int BatchSize { get; set; } = 500;

    /// <summary>Retries per entity for transient database faults inside one pass.</summary>
    public int MaxAttemptsPerEntity { get; set; } = 3;

    /// <summary>
    /// How long the retention health check tolerates no successful pass before reporting
    /// unhealthy. Two missed daily passes still leave the safety margin intact.
    /// </summary>
    public int StaleAfterHours { get; set; } = 48;

    /// <summary>
    /// Age at which a row is actually destroyed: the declared ceiling minus the scheduling margin
    /// minus the backup window, so no copy of the row — live or backed up — outlives
    /// <see cref="MaxRetentionDays"/>.
    /// </summary>
    public TimeSpan EffectiveRetention => TimeSpan.FromDays(Math.Max(1,
        MaxRetentionDays - Math.Max(0, SafetyMarginDays) - Math.Max(0, BackupRetentionDays)));

    public TimeSpan PairingChallengeRetention =>
        TimeSpan.FromHours(Math.Clamp(PairingChallengeRetentionHours, 1, 24 * 30));

    public TimeSpan CleanupInterval =>
        TimeSpan.FromHours(Math.Clamp(CleanupIntervalHours, 1, 24 * 7));

    public TimeSpan StaleAfter => TimeSpan.FromHours(Math.Clamp(StaleAfterHours, 1, 24 * 30));

    /// <summary>
    /// Rotation always lands strictly inside the deletion window: if it were configured at or
    /// past <see cref="EffectiveRetention"/> the sweep would delete the identity before the
    /// device ever got a replacement, turning a transparent rotation into a forced re-pairing.
    /// </summary>
    public TimeSpan DeviceIdentityRotationAfter
    {
        get
        {
            var configured = TimeSpan.FromDays(Math.Max(1, DeviceIdentityRotationDays));
            var ceiling = EffectiveRetention - TimeSpan.FromDays(1);
            return configured < ceiling ? configured : ceiling;
        }
    }
}
