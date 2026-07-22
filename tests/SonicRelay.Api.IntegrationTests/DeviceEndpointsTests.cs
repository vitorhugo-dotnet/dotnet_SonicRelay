using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SonicRelay.Infrastructure.Persistence;
using Xunit;

namespace SonicRelay.Api.IntegrationTests;

public sealed class DeviceEndpointsTests : IClassFixture<SonicRelayApiFactory>
{
    private const string Password = "Valid1!Password";
    private readonly SonicRelayApiFactory _factory;

    public DeviceEndpointsTests(SonicRelayApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Authenticated_user_can_create_list_get_patch_and_delete_a_device()
    {
        var user = await CreateUserAsync("crud");

        var created = await user.Client.PostAsJsonAsync("/api/devices", new
        {
            name = "Office PC",
            type = "windows_publisher",
            platform = "windows",
            publicKey = "public-key"
        });
        var createdBody = await ReadJsonAsync(created);
        var deviceId = createdBody.GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal($"/api/devices/{deviceId}", created.Headers.Location?.ToString());
        Assert.Equal("Office PC", createdBody.GetProperty("name").GetString());
        Assert.False(createdBody.GetProperty("revoked").GetBoolean());

        var listed = await user.Client.GetFromJsonAsync<JsonElement>("/api/devices");
        Assert.Equal(deviceId, Assert.Single(listed.EnumerateArray()).GetProperty("id").GetGuid());

        var fetched = await user.Client.GetAsync($"/api/devices/{deviceId}");
        Assert.Equal(HttpStatusCode.OK, fetched.StatusCode);

        var patched = await user.Client.PatchAsJsonAsync($"/api/devices/{deviceId}", new
        {
            name = "Living Room PC",
            publicKey = "new-key"
        });
        var patchedBody = await ReadJsonAsync(patched);
        Assert.Equal(HttpStatusCode.OK, patched.StatusCode);
        Assert.Equal("Living Room PC", patchedBody.GetProperty("name").GetString());
        Assert.Equal("new-key", patchedBody.GetProperty("publicKey").GetString());

        var deleted = await user.Client.DeleteAsync($"/api/devices/{deviceId}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await user.Client.GetAsync($"/api/devices/{deviceId}")).StatusCode);
    }

    [Fact]
    public async Task Device_queries_and_mutations_are_isolated_by_owner()
    {
        var owner = await CreateUserAsync("owner");
        var other = await CreateUserAsync("other");
        var deviceId = await CreateDeviceAsync(owner.Client, "Owner device", "flutter_viewer", "android");

        var otherList = await other.Client.GetFromJsonAsync<JsonElement>("/api/devices");
        Assert.Empty(otherList.EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound,
            (await other.Client.GetAsync($"/api/devices/{deviceId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await other.Client.PatchAsJsonAsync($"/api/devices/{deviceId}", new { name = "Stolen" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await other.Client.PostAsync($"/api/devices/{deviceId}/revoke", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await other.Client.DeleteAsync($"/api/devices/{deviceId}")).StatusCode);

        Assert.Equal(HttpStatusCode.OK,
            (await owner.Client.GetAsync($"/api/devices/{deviceId}")).StatusCode);
    }

    [Theory]
    [InlineData("", "windows_publisher", "windows")]
    [InlineData("   ", "windows_publisher", "windows")]
    [InlineData("Device", "unknown", "windows")]
    [InlineData("Device", "windows_publisher", "android")]
    [InlineData("Device", "flutter_viewer", "windows")]
    [InlineData("Device", "flutter_viewer", "unknown")]
    public async Task Create_rejects_invalid_name_type_or_platform(string name, string type, string platform)
    {
        var user = await CreateUserAsync($"invalid-{Guid.NewGuid():N}");

        var response = await user.Client.PostAsJsonAsync("/api/devices", new { name, type, platform });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Create_rejects_names_over_database_limit_and_patch_rejects_empty_or_invalid_names()
    {
        var user = await CreateUserAsync("invalid-patch");
        Assert.Equal(HttpStatusCode.BadRequest,
            (await user.Client.PostAsJsonAsync("/api/devices", new
            {
                name = new string('a', 121),
                type = "windows_publisher",
                platform = "windows"
            })).StatusCode);

        var deviceId = await CreateDeviceAsync(user.Client, "Valid", "windows_publisher", "windows");
        Assert.Equal(HttpStatusCode.BadRequest,
            (await user.Client.PatchAsync($"/api/devices/{deviceId}",
                new StringContent("{}", Encoding.UTF8, "application/json"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await user.Client.PatchAsJsonAsync($"/api/devices/{deviceId}", new { name = " " })).StatusCode);
    }

    [Fact]
    public async Task Revoke_is_idempotent_and_device_remains_manageable()
    {
        var user = await CreateUserAsync("revoke");
        var deviceId = await CreateDeviceAsync(user.Client, "Phone", "flutter_viewer", "ios");

        var first = await user.Client.PostAsync($"/api/devices/{deviceId}/revoke", null);
        var second = await user.Client.PostAsync($"/api/devices/{deviceId}/revoke", null);
        var fetched = await user.Client.GetFromJsonAsync<JsonElement>($"/api/devices/{deviceId}");

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.True(fetched.GetProperty("revoked").GetBoolean());
    }

    [Fact]
    public async Task All_device_routes_reject_anonymous_requests()
    {
        var client = _factory.CreateClient();
        var id = Guid.NewGuid();
        var responses = new[]
        {
            await client.PostAsJsonAsync("/api/devices", new { name = "PC", type = "windows_publisher", platform = "windows" }),
            await client.GetAsync("/api/devices"),
            await client.GetAsync($"/api/devices/{id}"),
            await client.PatchAsJsonAsync($"/api/devices/{id}", new { name = "PC" }),
            await client.DeleteAsync($"/api/devices/{id}"),
            await client.PostAsync($"/api/devices/{id}/revoke", null)
        };

        Assert.All(responses, response => Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode));
    }

    private async Task<TestUser> CreateUserAsync(string prefix)
    {
        var client = _factory.CreateClient();
        var email = $"devices-{prefix}-{Guid.NewGuid():N}@example.com";
        Assert.Equal(HttpStatusCode.OK,
            (await client.PostAsJsonAsync("/auth/register", new { email, password = Password })).StatusCode);
        var login = await client.PostAsJsonAsync("/auth/login", new { email, password = Password });
        var tokens = await ReadJsonAsync(login);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", tokens.GetProperty("accessToken").GetString());
        var profile = await client.GetFromJsonAsync<JsonElement>("/auth/me");
        return new TestUser(client, profile.GetProperty("id").GetGuid());
    }

    private static async Task<Guid> CreateDeviceAsync(HttpClient client, string name, string type, string platform)
    {
        var response = await client.PostAsJsonAsync("/api/devices", new { name, type, platform });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await ReadJsonAsync(response)).GetProperty("id").GetGuid();
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        return document.RootElement.Clone();
    }

    private sealed record TestUser(HttpClient Client, Guid UserId);
}
