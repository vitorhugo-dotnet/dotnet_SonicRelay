using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SonicRelay.Domain.DeviceIdentities;
using SonicRelay.Domain.Devices;
using SonicRelay.Domain.Sessions;
using SonicRelay.Infrastructure.Persistence;
using Xunit;

namespace SonicRelay.Api.IntegrationTests;

/// <summary>
/// Rejoin has to be idempotent per device: a recovering client may fire several joins at once
/// (the automatic orchestrator and a manual retry, or two attempts either side of a network
/// handover), and every one of them has to land on the same participant row. A second row is
/// not a cosmetic duplicate — it consumes a viewer slot, splits signaling routing across two
/// participant ids, and makes the publisher offer to a participant nobody is listening on.
/// </summary>
public sealed class SessionRejoinIdempotencyTests : IClassFixture<SonicRelayApiFactory>
{
    private readonly SonicRelayApiFactory _factory;

    public SessionRejoinIdempotencyTests(SonicRelayApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Concurrent_rejoins_from_one_device_create_a_single_participant()
    {
        var (sessionId, viewerClient, viewerDeviceId) = await PairedViewerAsync();

        var responses = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => viewerClient.PostAsync($"/api/sessions/{sessionId}/join", null)));

        Assert.All(responses, response => Assert.Equal(HttpStatusCode.OK, response.StatusCode));
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var participants = await db.SessionParticipants
            .Where(x => x.SessionId == sessionId && x.DeviceId == viewerDeviceId
                && x.Role == ParticipantRoles.Viewer)
            .ToListAsync();
        Assert.Single(participants);
    }

    [Fact]
    public async Task Concurrent_rejoins_do_not_exhaust_a_single_viewer_slot()
    {
        var (sessionId, viewerClient, _) = await PairedViewerAsync(maxViewers: 1);

        var responses = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => viewerClient.PostAsync($"/api/sessions/{sessionId}/join", null)));

        // A duplicate row would push the counted viewer total past MaxViewers, so the device
        // recovering from a network loss would start rejecting its own rejoin with 409.
        Assert.All(responses, response => Assert.Equal(HttpStatusCode.OK, response.StatusCode));
    }

    [Fact]
    public async Task Rejoin_recovers_from_duplicate_rows_left_by_an_earlier_build()
    {
        var (sessionId, viewerClient, viewerDeviceId) = await PairedViewerAsync();
        Assert.Equal(HttpStatusCode.OK,
            (await viewerClient.PostAsync($"/api/sessions/{sessionId}/join", null)).StatusCode);
        await SeedDuplicateParticipantAsync(sessionId, viewerDeviceId);

        var response = await viewerClient.PostAsync($"/api/sessions/{sessionId}/join", null);

        // Rows predating the uniqueness invariant must not wedge the endpoint on a
        // "sequence contains more than one element" 500 — the device can never recover from
        // that on its own, and the session it is trying to rejoin is still perfectly alive.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private async Task<(Guid SessionId, HttpClient ViewerClient, Guid ViewerDeviceId)> PairedViewerAsync(
        int maxViewers = 2)
    {
        var publisherClient = _factory.CreateClient();
        await DeviceIdentityTestHelper.BootstrapAndAuthorizeAsync(
            publisherClient, DeviceTypes.WindowsPublisher, DevicePlatforms.Windows);
        var created = await publisherClient.PostAsJsonAsync("/api/sessions", new { maxViewers });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var body = await created.Content.ReadFromJsonAsync<SessionResponse>();
        var sessionId = body!.Id;

        var viewerClient = _factory.CreateClient();
        var viewer = await DeviceIdentityTestHelper.BootstrapAndAuthorizeAsync(
            viewerClient, DeviceTypes.FlutterViewer, DevicePlatforms.Android);

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.DevicePairings.Add(new DevicePairing
        {
            Id = Guid.NewGuid(),
            PublisherDeviceId = body.SourceDeviceId,
            ViewerDeviceId = viewer.DeviceId,
            Status = DevicePairingStatuses.Active,
            CreatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
        return (sessionId, viewerClient, viewer.DeviceId);
    }

    private async Task SeedDuplicateParticipantAsync(Guid sessionId, Guid deviceId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.SessionParticipants.Add(new SessionParticipant
        {
            Id = Guid.NewGuid(),
            SessionId = sessionId,
            DeviceId = deviceId,
            Role = ParticipantRoles.Viewer,
            Status = ParticipantStatuses.Disconnected,
            JoinedAt = DateTimeOffset.UtcNow,
            LeftAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
    }

    private sealed record SessionResponse(Guid Id, Guid SourceDeviceId);
}
