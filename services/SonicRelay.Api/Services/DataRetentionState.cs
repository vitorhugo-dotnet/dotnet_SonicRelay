namespace SonicRelay.Api.Services;

/// <summary>
/// Process-wide record of the last fully successful retention pass. A silently stalled cleanup
/// is the failure mode that would quietly turn the Data Safety declaration into a false
/// statement, so the timestamp is exposed both as a metric and as a health check.
/// </summary>
public sealed class DataRetentionState
{
    private long _lastSuccessTicks;

    public DateTimeOffset? LastSuccessAt
    {
        get
        {
            var ticks = Interlocked.Read(ref _lastSuccessTicks);
            return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
        }
    }

    public void MarkSuccess(DateTimeOffset completedAt) =>
        Interlocked.Exchange(ref _lastSuccessTicks, completedAt.UtcDateTime.Ticks);
}
