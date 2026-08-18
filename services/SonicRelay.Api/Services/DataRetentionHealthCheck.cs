using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace SonicRelay.Api.Services;

/// <summary>
/// Reports whether the retention sweep is still running. It goes unhealthy well before data
/// could reach the declared 90-day ceiling, so an operator has time to fix the cleanup rather
/// than discovering afterwards that the Data Safety declaration stopped being true.
/// </summary>
public sealed class DataRetentionHealthCheck(
    DataRetentionState state,
    IOptions<DataRetentionOptions> options,
    TimeProvider time) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var settings = options.Value;
        if (!settings.Enabled)
        {
            return Task.FromResult(HealthCheckResult.Degraded("Data retention cleanup is disabled."));
        }

        var lastSuccess = state.LastSuccessAt;
        if (lastSuccess is null)
        {
            // Expected only in the window between startup and the first pass.
            return Task.FromResult(HealthCheckResult.Degraded("Data retention cleanup has not completed a pass yet."));
        }

        var age = time.GetUtcNow() - lastSuccess.Value;
        return Task.FromResult(age > settings.StaleAfter
            ? HealthCheckResult.Unhealthy(
                $"Last successful data retention pass was {(int)age.TotalHours}h ago "
                + $"(threshold {(int)settings.StaleAfter.TotalHours}h).")
            : HealthCheckResult.Healthy());
    }
}
