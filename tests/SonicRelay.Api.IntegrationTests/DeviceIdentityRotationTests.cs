using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SonicRelay.Api.Contracts;
using SonicRelay.Domain.Devices;
using SonicRelay.Domain.RelaySettings;
using SonicRelay.Infrastructure.Persistence;
using Xunit;

namespace SonicRelay.Api.IntegrationTests;

/// <summary>
/// A <c>deviceId</c> is a collected identifier, so it cannot be allowed to live forever behind a
/// refreshed <c>LastSeenAt</c> (issue #44). These tests pin the chosen strategy: an identity that
/// reaches the rotation deadline is replaced during an ordinary token exchange, the old row is
/// hard deleted, and the user notices nothing — pairings and sessions keep working.
/// </summary>
public sealed class DeviceIdentityRotationTests
{
    // Comfortably past the 60-day rotation deadline but inside the 89-day deletion sweep.
    private static readonly TimeSpan PastRotationDeadline = TimeSpan.FromDays(61);

    private static SonicRelayApiFactory NewFactory() => new();

    private static async Task<BootstrapDeviceResponse> BootstrapAsync(
        HttpClient client, string deviceType, string platform, string name = "Rotation fixture")
    {
        var response = await client.PostAsJsonAsync("/api/devices/bootstrap",
            new BootstrapDeviceRequest(name, deviceType, platform));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<BootstrapDeviceResponse>())!;
    }

    private static async Task<DeviceTokenResponse> TokenAsync(HttpClient client, Guid deviceId, string secret)
    {
        var response = await client.PostAsJsonAsync("/api/devices/token",
            new DeviceTokenRequest(deviceId, secret));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<DeviceTokenResponse>())!;
    }

    /// <summary>Backdates a device's collection time so it is due for rotation.</summary>
    private static async Task AgeAsync(SonicRelayApiFactory factory, Guid deviceId, TimeSpan age)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var device = await db.DeviceIdentities.SingleAsync(x => x.Id == deviceId);
        device.CreatedAt -= age;
        await db.SaveChangesAsync();
    }

    private static HttpRequestMessage Authorized(HttpMethod method, string url, string token, object? body = null)
    {
        var request = new HttpRequestMessage(method, url);
        if (body is not null) request.Content = JsonContent.Create(body);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    [Fact]
    public async Task A_device_inside_the_rotation_window_keeps_its_identifier()
    {
        await using var factory = NewFactory();
        var client = factory.CreateClient();
        var device = await BootstrapAsync(client, DeviceTypes.WindowsPublisher, DevicePlatforms.Windows);

        var token = await TokenAsync(client, device.DeviceId, device.CredentialSecret);

        Assert.Equal(device.DeviceId, token.DeviceId);
        Assert.Null(token.RotatedCredentialSecret);
    }

    [Fact]
    public async Task Reaching_the_rotation_deadline_issues_a_new_identifier_and_erases_the_old_one()
    {
        await using var factory = NewFactory();
        var client = factory.CreateClient();
        var device = await BootstrapAsync(client, DeviceTypes.WindowsPublisher, DevicePlatforms.Windows);
        await AgeAsync(factory, device.DeviceId, PastRotationDeadline);

        var token = await TokenAsync(client, device.DeviceId, device.CredentialSecret);

        Assert.NotEqual(device.DeviceId, token.DeviceId);
        Assert.NotNull(token.RotatedCredentialSecret);
        Assert.NotEqual(device.CredentialSecret, token.RotatedCredentialSecret);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        // Hard delete: the previous identifier is not archived, tombstoned or back-referenced
        // anywhere, so nothing left in the database can reconstruct it.
        Assert.False(await db.DeviceIdentities.AnyAsync(x => x.Id == device.DeviceId));
        var replacement = await db.DeviceIdentities.SingleAsync();
        Assert.Equal(token.DeviceId, replacement.Id);
        Assert.Equal("Rotation fixture", replacement.Name);
    }

    [Fact]
    public async Task The_replacement_identity_starts_its_own_retention_clock_and_works_immediately()
    {
        await using var factory = NewFactory();
        var client = factory.CreateClient();
        var device = await BootstrapAsync(client, DeviceTypes.WindowsPublisher, DevicePlatforms.Windows);
        await AgeAsync(factory, device.DeviceId, PastRotationDeadline);

        var rotated = await TokenAsync(client, device.DeviceId, device.CredentialSecret);

        // The token returned alongside the rotation is usable straight away — the client is never
        // logged out by a rotation.
        var created = await client.SendAsync(Authorized(
            HttpMethod.Post, "/api/sessions", rotated.AccessToken, new { maxViewers = 2 }));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        // And the new credential authenticates on its own, without a second rotation.
        var again = await TokenAsync(client, rotated.DeviceId, rotated.RotatedCredentialSecret!);
        Assert.Equal(rotated.DeviceId, again.DeviceId);
        Assert.Null(again.RotatedCredentialSecret);
    }

    [Fact]
    public async Task The_previous_credential_stops_working_the_moment_it_is_rotated()
    {
        await using var factory = NewFactory();
        var client = factory.CreateClient();
        var device = await BootstrapAsync(client, DeviceTypes.WindowsPublisher, DevicePlatforms.Windows);
        await AgeAsync(factory, device.DeviceId, PastRotationDeadline);
        await TokenAsync(client, device.DeviceId, device.CredentialSecret);

        var replayed = await client.PostAsJsonAsync("/api/devices/token",
            new DeviceTokenRequest(device.DeviceId, device.CredentialSecret));

        Assert.Equal(HttpStatusCode.Unauthorized, replayed.StatusCode);
    }

    [Fact]
    public async Task Pairing_and_sessions_keep_working_across_a_rotation()
    {
        await using var factory = NewFactory();
        var publisherClient = factory.CreateClient();
        var viewerClient = factory.CreateClient();
        var publisher = await BootstrapAsync(publisherClient, DeviceTypes.WindowsPublisher, DevicePlatforms.Windows);
        var viewer = await BootstrapAsync(viewerClient, DeviceTypes.FlutterViewer, DevicePlatforms.Android);
        var publisherToken = await TokenAsync(publisherClient, publisher.DeviceId, publisher.CredentialSecret);
        var viewerToken = await TokenAsync(viewerClient, viewer.DeviceId, viewer.CredentialSecret);

        // Pair the two devices for real, through the pairing endpoints.
        var challenge = await (await publisherClient.SendAsync(Authorized(
                HttpMethod.Post, "/api/pairings/challenges", publisherToken.AccessToken)))
            .Content.ReadFromJsonAsync<CreateChallengeResponse>();
        var pairingResponse = await viewerClient.SendAsync(Authorized(
            HttpMethod.Post, "/api/pairings/complete", viewerToken.AccessToken,
            new CompletePairingRequest(challenge!.ChallengeId, challenge.Code)));
        pairingResponse.EnsureSuccessStatusCode();

        await AgeAsync(factory, publisher.DeviceId, PastRotationDeadline);
        var rotated = await TokenAsync(publisherClient, publisher.DeviceId, publisher.CredentialSecret);
        Assert.NotEqual(publisher.DeviceId, rotated.DeviceId);

        // The pairing followed the publisher to its new identifier, so the viewer — which knows
        // nothing about the rotation — can still join a session the publisher creates.
        var session = await (await publisherClient.SendAsync(Authorized(
                HttpMethod.Post, "/api/sessions", rotated.AccessToken, new { maxViewers = 2 })))
            .Content.ReadFromJsonAsync<JsonElement>();
        var code = session.GetProperty("code").GetString();

        var join = await viewerClient.SendAsync(Authorized(
            HttpMethod.Post, "/api/sessions/join", viewerToken.AccessToken, new { code }));
        Assert.Equal(HttpStatusCode.OK, join.StatusCode);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var pairing = await db.DevicePairings.SingleAsync();
        Assert.Equal(rotated.DeviceId, pairing.PublisherDeviceId);
        Assert.Equal(viewer.DeviceId, pairing.ViewerDeviceId);
    }

    [Fact]
    public async Task Rotation_moves_relay_settings_without_restarting_their_retention_clock()
    {
        await using var factory = NewFactory();
        var client = factory.CreateClient();
        var device = await BootstrapAsync(client, DeviceTypes.WindowsPublisher, DevicePlatforms.Windows);
        var token = await TokenAsync(client, device.DeviceId, device.CredentialSecret);
        var update = await client.SendAsync(Authorized(HttpMethod.Put, "/api/settings/relay", token.AccessToken,
            new { relayMode = RelayModes.ForceRelay }));
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);

        DateTimeOffset originalCollectedAt;
        await using (var before = factory.Services.CreateAsyncScope())
        {
            originalCollectedAt = (await before.ServiceProvider.GetRequiredService<AppDbContext>()
                .RelayDeviceSettings.SingleAsync()).CreatedAt;
        }

        await AgeAsync(factory, device.DeviceId, PastRotationDeadline);
        var rotated = await TokenAsync(client, device.DeviceId, device.CredentialSecret);

        await using var scope = factory.Services.CreateAsyncScope();
        var settings = await scope.ServiceProvider.GetRequiredService<AppDbContext>()
            .RelayDeviceSettings.SingleAsync();
        Assert.Equal(rotated.DeviceId, settings.DeviceId);
        Assert.Equal(RelayModes.ForceRelay, settings.RelayMode);
        // Carrying the row over must not buy the data another full retention window.
        Assert.Equal(originalCollectedAt, settings.CreatedAt);
    }

    [Fact]
    public async Task Concurrent_token_requests_produce_exactly_one_replacement_identity()
    {
        await using var factory = NewFactory();
        var client = factory.CreateClient();
        var device = await BootstrapAsync(client, DeviceTypes.WindowsPublisher, DevicePlatforms.Windows);
        await AgeAsync(factory, device.DeviceId, PastRotationDeadline);

        var responses = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ =>
            client.PostAsJsonAsync("/api/devices/token",
                new DeviceTokenRequest(device.DeviceId, device.CredentialSecret))));

        // Exactly one caller wins the rotation; the losers see an unauthenticated response
        // because the credential they presented no longer belongs to a stored identity.
        Assert.Single(responses, x => x.StatusCode == HttpStatusCode.OK);
        await using var scope = factory.Services.CreateAsyncScope();
        Assert.Single(await scope.ServiceProvider.GetRequiredService<AppDbContext>()
            .DeviceIdentities.ToListAsync());
    }

    [Fact]
    public async Task A_device_that_never_returns_is_deleted_rather_than_rotated()
    {
        await using var factory = NewFactory();
        var client = factory.CreateClient();
        var device = await BootstrapAsync(client, DeviceTypes.WindowsPublisher, DevicePlatforms.Windows);
        // Rotation only happens when a device shows up. One that goes silent is caught by the
        // sweep instead, which is what keeps the 90-day ceiling unconditional.
        await AgeAsync(factory, device.DeviceId, TimeSpan.FromDays(120));

        await factory.Services.GetRequiredService<SonicRelay.Api.Services.DataRetentionService>()
            .CleanupOnceAsync(CancellationToken.None);

        await using var scope = factory.Services.CreateAsyncScope();
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<AppDbContext>()
            .DeviceIdentities.ToListAsync());
        var afterwards = await client.PostAsJsonAsync("/api/devices/token",
            new DeviceTokenRequest(device.DeviceId, device.CredentialSecret));
        Assert.Equal(HttpStatusCode.Unauthorized, afterwards.StatusCode);
    }
}
