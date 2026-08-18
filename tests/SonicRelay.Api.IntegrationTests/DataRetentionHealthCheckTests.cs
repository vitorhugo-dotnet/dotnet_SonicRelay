using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using SonicRelay.Api.Services;
using Xunit;

namespace SonicRelay.Api.IntegrationTests;

/// <summary>
/// The dangerous failure is not a loud one — it is a cleanup that quietly stops running while the
/// app keeps serving traffic, because the Data Safety declaration silently stops being true.
/// The health check has to go red with days to spare, not after the ceiling is crossed.
/// </summary>
public sealed class DataRetentionHealthCheckTests
{
    private static (DataRetentionHealthCheck Check, DataRetentionState State, TestTimeProvider Clock) Build(
        Action<DataRetentionOptions>? configure = null)
    {
        var options = new DataRetentionOptions();
        configure?.Invoke(options);
        var state = new DataRetentionState();
        var clock = new TestTimeProvider();
        return (new DataRetentionHealthCheck(state, Options.Create(options), clock), state, clock);
    }

    private static async Task<HealthStatus> RunAsync(DataRetentionHealthCheck check) =>
        (await check.CheckHealthAsync(new HealthCheckContext())).Status;

    [Fact]
    public async Task Before_the_first_pass_the_check_is_degraded_rather_than_healthy()
    {
        var (check, _, _) = Build();

        Assert.Equal(HealthStatus.Degraded, await RunAsync(check));
    }

    [Fact]
    public async Task A_recent_successful_pass_is_healthy()
    {
        var (check, state, clock) = Build();
        state.MarkSuccess(clock.GetUtcNow());

        clock.Advance(TimeSpan.FromHours(6));

        Assert.Equal(HealthStatus.Healthy, await RunAsync(check));
    }

    [Fact]
    public async Task A_cleanup_that_stopped_running_turns_unhealthy_long_before_the_ceiling()
    {
        var (check, state, clock) = Build();
        state.MarkSuccess(clock.GetUtcNow());

        clock.Advance(TimeSpan.FromHours(49));

        Assert.Equal(HealthStatus.Unhealthy, await RunAsync(check));
    }

    [Fact]
    public async Task Disabling_retention_is_reported_rather_than_passing_silently()
    {
        var (check, state, clock) = Build(options => options.Enabled = false);
        state.MarkSuccess(clock.GetUtcNow());

        Assert.Equal(HealthStatus.Degraded, await RunAsync(check));
    }

    [Fact]
    public void Rotation_is_always_scheduled_inside_the_deletion_window()
    {
        // Even a misconfigured rotation deadline cannot land on or after the sweep, which would
        // delete identities out from under live devices instead of rotating them.
        var options = new DataRetentionOptions { MaxRetentionDays = 90, DeviceIdentityRotationDays = 365 };

        Assert.True(options.DeviceIdentityRotationAfter < options.EffectiveRetention);
    }

    [Fact]
    public void The_effective_cutoff_leaves_room_for_the_scheduler_and_for_backups()
    {
        var options = new DataRetentionOptions();

        Assert.Equal(90, options.MaxRetentionDays);
        // Deleting from the primary database at day 89 would still leave the row inside a
        // backup taken on day 88. The cutoff has to come forward far enough that the last
        // backup able to contain a row is itself destroyed before the declared ceiling.
        Assert.Equal(TimeSpan.FromDays(82), options.EffectiveRetention);
        var lastBackupCopyExpiresAt =
            options.EffectiveRetention + TimeSpan.FromDays(options.BackupRetentionDays);
        Assert.True(lastBackupCopyExpiresAt < TimeSpan.FromDays(options.MaxRetentionDays));
    }

    [Fact]
    public void A_longer_backup_window_pulls_the_database_cutoff_forward()
    {
        var options = new DataRetentionOptions { BackupRetentionDays = 30 };

        Assert.Equal(TimeSpan.FromDays(59), options.EffectiveRetention);
        Assert.True(options.DeviceIdentityRotationAfter < options.EffectiveRetention);
    }
}
