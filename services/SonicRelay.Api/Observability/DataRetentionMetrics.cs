using Prometheus;

namespace SonicRelay.Api.Observability;

/// <summary>
/// Aggregate-only metrics for the data-retention sweep (issue #44). Deliberately carries no
/// identifier of anything removed: the only label is the entity name, drawn from a fixed set,
/// so a scrape can never be used to reconstruct which device or session was erased.
/// </summary>
public sealed class DataRetentionMetrics
{
    private readonly Counter _runs = Metrics.CreateCounter(
        "sonicrelay_data_retention_runs_total",
        "Data-retention cleanup passes that completed.");

    private readonly Counter _deleted = Metrics.CreateCounter(
        "sonicrelay_data_retention_deleted_records_total",
        "Records permanently deleted by the data-retention cleanup, by entity.",
        new CounterConfiguration { LabelNames = ["entity"] });

    private readonly Counter _failures = Metrics.CreateCounter(
        "sonicrelay_data_retention_failures_total",
        "Data-retention cleanup passes that failed.");

    private readonly Gauge _lastSuccess = Metrics.CreateGauge(
        "sonicrelay_data_retention_last_success_timestamp",
        "Unix timestamp (seconds) of the last fully successful data-retention pass.");

    private readonly Counter _identityRotations = Metrics.CreateCounter(
        "sonicrelay_device_identity_rotations_total",
        "Device identities replaced by a fresh identifier before the retention ceiling.");

    public void RecordRun() => _runs.Inc();

    public void RecordDeleted(string entity, int count)
    {
        if (count > 0) _deleted.WithLabels(entity).Inc(count);
    }

    public void RecordFailure() => _failures.Inc();

    public void RecordSuccess(DateTimeOffset completedAt) =>
        _lastSuccess.Set(completedAt.ToUnixTimeSeconds());

    public void RecordIdentityRotation() => _identityRotations.Inc();
}
