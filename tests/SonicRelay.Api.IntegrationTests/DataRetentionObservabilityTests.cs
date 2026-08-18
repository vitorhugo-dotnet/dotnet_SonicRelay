using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using SonicRelay.Api.Services;
using Xunit;

namespace SonicRelay.Api.IntegrationTests;

/// <summary>
/// Retention is only trustworthy if an operator can tell from the outside that it is still
/// running, so the sweep publishes aggregate counters and a last-success timestamp — and nothing
/// that could identify what it erased.
/// </summary>
public sealed class DataRetentionObservabilityTests : IClassFixture<SonicRelayApiFactory>
{
    private readonly SonicRelayApiFactory _factory;

    public DataRetentionObservabilityTests(SonicRelayApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Retention_series_are_scrapable_and_track_the_last_successful_pass()
    {
        var client = _factory.CreateClient();
        await _factory.Services.GetRequiredService<DataRetentionService>()
            .CleanupOnceAsync(CancellationToken.None);

        var response = await client.GetAsync("/metrics");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("sonicrelay_data_retention_runs_total", body);
        Assert.Contains("sonicrelay_data_retention_failures_total", body);
        Assert.Contains("sonicrelay_data_retention_last_success_timestamp", body);
        Assert.Contains("sonicrelay_device_identity_rotations_total", body);
        Assert.NotNull(_factory.Services.GetRequiredService<DataRetentionState>().LastSuccessAt);
    }

    [Fact]
    public async Task Retention_is_part_of_the_readiness_probe()
    {
        // Postgres and Redis are unreachable from the test host, so the aggregate readiness
        // status says nothing useful; what matters is that a stalled sweep is one of the things
        // readiness reports on at all.
        var report = await _factory.Services.GetRequiredService<HealthCheckService>()
            .CheckHealthAsync(registration => registration.Name == "data-retention");

        var entry = Assert.Single(report.Entries);
        Assert.Equal("data-retention", entry.Key);
        Assert.Contains("retention", entry.Value.Tags);
    }
}
