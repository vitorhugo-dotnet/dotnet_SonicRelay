# Relay & Coturn Settings API Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a global, singleton "relay settings" resource (`RelayMode` + optional TURN
override) that both the Windows Publisher and the Flutter viewer can read and write through
the API, and make `/api/webrtc/ice-servers` honor it — this is the backend half of
`windows_SonicRelay`'s design doc
`docs/superpowers/specs/2026-08-04-pairing-nav-and-relay-settings-design.md`.

**Architecture:** One EF Core entity (`RelaySettings`, single fixed-id row) persisted via the
existing `AppDbContext`/Postgres setup. A new `/api/settings/relay` endpoint group exposes
GET (read, secret never echoed) and PUT (partial update). `TurnCredentialService` is changed
from sync to async so it can read the override row and layer it over the existing
`appsettings.json`-derived `TurnOptions`, field by field, and to omit TURN entries entirely
when the mode is `disableFallback`.

**Tech Stack:** ASP.NET Core minimal APIs, EF Core (Npgsql in production, InMemory in tests),
xUnit + `WebApplicationFactory`.

## Global Constraints

- This backend has no account/user/owner concept — `RelaySettings` is one single global row,
  not scoped to a device or account.
- The write endpoint uses the existing `device:manage` authorization policy (already granted
  to every device type on bootstrap) — this is the same trust boundary
  `rotate-credential`/`revoke` already use, not a new one.
- `GET /api/settings/relay` never returns the TURN static auth secret in any form — only
  `hasCustomTurnSecret: bool`.
- `PUT /api/settings/relay` is a partial update: a field that is `null`/omitted leaves the
  currently stored value unchanged.
- `RelayMode` is one of exactly three values — `automatic`, `forceRelay`, `disableFallback` —
  mutually exclusive by construction (no independent booleans).
- When no override row exists, or a field on it is null/empty, behavior must be byte-for-byte
  identical to today's `appsettings.json`-only `TurnCredentialService`/`ice-servers` output.
  Verify this with the existing tests in `WebRtcEndpointsTests.cs` before considering any task
  done — they must keep passing unmodified.

---

### Task 1: `RelaySettings` domain entity, persistence, and migration

**Files:**
- Create: `src/SonicRelay.Domain/RelaySettings/RelaySettings.cs`
- Modify: `src/SonicRelay.Infrastructure/Persistence/AppDbContext.cs`
- Test: `tests/SonicRelay.Api.IntegrationTests/RelaySettingsPersistenceTests.cs`

**Interfaces:**
- Produces: `SonicRelay.Domain.RelaySettings.RelaySettings` with `Id` (Guid), `RelayMode`
  (string), `TurnUris` (string[]?), `TurnStaticAuthSecret` (string?), `UpdatedAt`
  (DateTimeOffset); `SonicRelay.Domain.RelaySettings.RelayModes` with `Automatic`,
  `ForceRelay`, `DisableFallback` constants and `IsValid(string?)`; `RelaySettings.SingletonId`
  (the fixed `Guid` every row must use — there is only ever one row); `AppDbContext.RelaySettings`
  (`DbSet<RelaySettings>`).

- [ ] **Step 1: Write the failing persistence test**

```csharp
using Microsoft.EntityFrameworkCore;
using SonicRelay.Domain.RelaySettings;
using SonicRelay.Infrastructure.Persistence;
using Xunit;

namespace SonicRelay.Api.IntegrationTests;

public sealed class RelaySettingsPersistenceTests
{
    [Fact]
    public async Task RoundTrips_a_singleton_row()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"relay-settings-persistence-{Guid.NewGuid()}")
            .Options;
        await using var db = new AppDbContext(options);

        db.RelaySettings.Add(new RelaySettings
        {
            Id = RelaySettings.SingletonId,
            RelayMode = RelayModes.ForceRelay,
            TurnUris = ["turn:relay.example.com:3478?transport=udp"],
            TurnStaticAuthSecret = "shared-secret",
            UpdatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();

        var reloaded = await db.RelaySettings.SingleAsync(x => x.Id == RelaySettings.SingletonId);
        Assert.Equal(RelayModes.ForceRelay, reloaded.RelayMode);
        Assert.Equal(["turn:relay.example.com:3478?transport=udp"], reloaded.TurnUris);
        Assert.Equal("shared-secret", reloaded.TurnStaticAuthSecret);
    }

    [Theory]
    [InlineData(RelayModes.Automatic, true)]
    [InlineData(RelayModes.ForceRelay, true)]
    [InlineData(RelayModes.DisableFallback, true)]
    [InlineData("bogus", false)]
    [InlineData(null, false)]
    public void IsValid_accepts_only_the_three_known_modes(string? mode, bool expected) =>
        Assert.Equal(expected, RelayModes.IsValid(mode));
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/SonicRelay.Api.IntegrationTests/SonicRelay.Api.IntegrationTests.csproj --filter FullyQualifiedName~RelaySettingsPersistenceTests`
Expected: FAIL to build — `RelaySettings`/`RelayModes`/`AppDbContext.RelaySettings` don't exist yet.

- [ ] **Step 3: Create the domain entity**

```csharp
namespace SonicRelay.Domain.RelaySettings;

/// <summary>
/// Global relay/coturn override, shared by every device this backend serves (there is no
/// account/owner concept). There is exactly one row, always at <see cref="SingletonId"/>;
/// absent or null fields mean "fall back to appsettings.json".
/// </summary>
public sealed class RelaySettings
{
    public static readonly Guid SingletonId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    public Guid Id { get; set; } = SingletonId;
    public string RelayMode { get; set; } = RelayModes.Automatic;
    public string[]? TurnUris { get; set; }
    public string? TurnStaticAuthSecret { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public static class RelayModes
{
    public const string Automatic = "automatic";
    public const string ForceRelay = "forceRelay";
    public const string DisableFallback = "disableFallback";

    public static bool IsValid(string? value) => value is Automatic or ForceRelay or DisableFallback;
}
```

- [ ] **Step 4: Register the entity on `AppDbContext`**

In `src/SonicRelay.Infrastructure/Persistence/AppDbContext.cs`, add the `using` and the
`DbSet`:

```csharp
using SonicRelay.Domain.RelaySettings;
```

```csharp
public DbSet<RelaySettings> RelaySettings => Set<RelaySettings>();
```

And inside `OnModelCreating`, alongside the other `modelBuilder.Entity<...>` blocks:

```csharp
modelBuilder.Entity<RelaySettings>(entity =>
{
    entity.ToTable("relay_settings");
    entity.HasKey(x => x.Id);
    entity.Property(x => x.RelayMode).HasMaxLength(20).IsRequired();
});
```

- [ ] **Step 5: Run the tests and verify they pass**

Run: `dotnet test tests/SonicRelay.Api.IntegrationTests/SonicRelay.Api.IntegrationTests.csproj --filter FullyQualifiedName~RelaySettingsPersistenceTests`
Expected: PASS.

- [ ] **Step 6: Generate the EF Core migration**

Install the EF tool if `dotnet ef` is not already on `PATH`:

Run: `dotnet tool install --global dotnet-ef` (skip if it prints "already installed"; run
`export PATH="$PATH:$HOME/.dotnet/tools"` if the command isn't found afterward).

Run:
```bash
dotnet ef migrations add AddRelaySettings \
  --project src/SonicRelay.Infrastructure/SonicRelay.Infrastructure.csproj \
  --startup-project services/SonicRelay.Api/SonicRelay.Api.csproj \
  --output-dir Persistence/Migrations
```

Expected: a new `Migrations/*_AddRelaySettings.cs` + `.Designer.cs` creating table
`relay_settings`, and an updated `AppDbContextModelSnapshot.cs`. This uses
`AppDbContextFactory`'s design-time connection string and does not require a reachable
database.

- [ ] **Step 7: Commit**

```bash
git add src/SonicRelay.Domain/RelaySettings src/SonicRelay.Infrastructure/Persistence tests/SonicRelay.Api.IntegrationTests/RelaySettingsPersistenceTests.cs
git commit -m "Add RelaySettings entity and migration"
```

---

### Task 2: `TurnCredentialService` reads the override and honors `disableFallback`

**Files:**
- Modify: `services/SonicRelay.Api/Services/TurnCredentialService.cs`
- Modify: `services/SonicRelay.Api/Endpoints/WebRtcEndpoints.cs:22-28`
- Modify: `services/SonicRelay.Api/Program.cs:29` (`AddSingleton<TurnCredentialService>` →
  `AddScoped<TurnCredentialService>` — it now takes the scoped `AppDbContext`, so it can no
  longer be a singleton)
- Test: `tests/SonicRelay.Api.IntegrationTests/WebRtcEndpointsTests.cs`

**Interfaces:**
- Consumes: `SonicRelay.Domain.RelaySettings.RelaySettings`/`RelayModes` (Task 1),
  `SonicRelay.Infrastructure.Persistence.AppDbContext.RelaySettings`.
- Produces: `TurnCredentialService.BuildAsync(string deviceId, CancellationToken ct)` — same
  return shape (`IceServersResponse`) as the old sync `Build`, which it replaces.

- [ ] **Step 1: Write the failing tests**

Add to `tests/SonicRelay.Api.IntegrationTests/WebRtcEndpointsTests.cs` (new `using
SonicRelay.Domain.RelaySettings;` and `using SonicRelay.Infrastructure.Persistence;` at the
top, plus `using Microsoft.Extensions.DependencyInjection;` for `factory.Services`):

```csharp
[Fact]
public async Task Ice_servers_omits_turn_when_relay_mode_is_disable_fallback()
{
    const string secret = "disable-fallback-secret";
    await using var factory = new SonicRelayApiFactory(new Dictionary<string, string?>
    {
        ["Turn:StaticAuthSecret"] = secret,
        ["Turn:TurnUris:0"] = "turn:relay.example.com:3478?transport=udp"
    });
    var (client, _) = await BootstrapAsync(factory);
    await SeedRelaySettingsAsync(factory, RelayModes.DisableFallback);

    var body = await GetIceServersAsync(client);

    var servers = body.GetProperty("iceServers").EnumerateArray().ToList();
    Assert.All(servers, entry =>
        Assert.False(entry.GetProperty("urls")[0].GetString()!.StartsWith("turn:", StringComparison.Ordinal)));
}

[Fact]
public async Task Ice_servers_uses_the_overridden_turn_uri_and_secret_when_present()
{
    await using var factory = new SonicRelayApiFactory(new Dictionary<string, string?>
    {
        ["Turn:StaticAuthSecret"] = "appsettings-secret",
        ["Turn:TurnUris:0"] = "turn:appsettings.example.com:3478?transport=udp"
    });
    var (client, deviceId) = await BootstrapAsync(factory);
    const string overrideSecret = "override-secret";
    const string overrideUri = "turn:override.example.com:3478?transport=udp";
    await SeedRelaySettingsAsync(factory, RelayModes.Automatic, [overrideUri], overrideSecret);

    var body = await GetIceServersAsync(client);

    var turn = body.GetProperty("iceServers").EnumerateArray()
        .Single(item => item.GetProperty("urls")[0].GetString()!.StartsWith("turn:", StringComparison.Ordinal));
    Assert.Equal(overrideUri, turn.GetProperty("urls")[0].GetString());
    var username = turn.GetProperty("username").GetString()!;
    var expected = Convert.ToBase64String(HMACSHA1.HashData(
        Encoding.UTF8.GetBytes(overrideSecret), Encoding.UTF8.GetBytes(username)));
    Assert.Equal(expected, turn.GetProperty("credential").GetString());
}

private static async Task SeedRelaySettingsAsync(
    SonicRelayApiFactory factory, string relayMode, string[]? turnUris = null, string? turnSecret = null)
{
    using var scope = factory.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.RelaySettings.Add(new RelaySettings
    {
        Id = RelaySettings.SingletonId,
        RelayMode = relayMode,
        TurnUris = turnUris,
        TurnStaticAuthSecret = turnSecret,
        UpdatedAt = DateTimeOffset.UtcNow
    });
    await db.SaveChangesAsync();
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/SonicRelay.Api.IntegrationTests/SonicRelay.Api.IntegrationTests.csproj --filter FullyQualifiedName~WebRtcEndpointsTests`
Expected: FAIL — `RelaySettings`/`RelayModes` compile, but the two new tests fail because
`TurnCredentialService` doesn't read the override yet (TURN still comes from `appsettings.json`
unconditionally).

- [ ] **Step 3: Rewrite `TurnCredentialService` to read the override asynchronously**

Replace the whole `TurnCredentialService` class body in
`services/SonicRelay.Api/Services/TurnCredentialService.cs`:

```csharp
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SonicRelay.Domain.RelaySettings;
using SonicRelay.Infrastructure.Persistence;

namespace SonicRelay.Api.Services;

public sealed class TurnOptions
{
    public string? StaticAuthSecret { get; set; }
    public string[] TurnUris { get; set; } = [];
    public string[] StunUris { get; set; } = ["stun:stun.l.google.com:19302"];
    public int CredentialTtlSeconds { get; set; } = 3600;
}

public sealed record IceServerEntry(IReadOnlyList<string> Urls, string? Username = null, string? Credential = null);

public sealed record IceServersResponse(IReadOnlyList<IceServerEntry> IceServers, int TtlSeconds);

public sealed class TurnCredentialService(IOptions<TurnOptions> options, AppDbContext db, TimeProvider time)
{
    public async Task<IceServersResponse> BuildAsync(string deviceId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        var settings = options.Value;
        var overrideRow = await db.RelaySettings.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == RelaySettings.SingletonId, cancellationToken);

        var relayMode = overrideRow?.RelayMode ?? RelayModes.Automatic;
        var turnUris = overrideRow?.TurnUris is { Length: > 0 } overriddenUris ? overriddenUris : settings.TurnUris;
        var secret = string.IsNullOrWhiteSpace(overrideRow?.TurnStaticAuthSecret)
            ? settings.StaticAuthSecret
            : overrideRow.TurnStaticAuthSecret;

        var servers = new List<IceServerEntry>();
        if (settings.StunUris.Length > 0)
        {
            servers.Add(new IceServerEntry(settings.StunUris));
        }

        if (relayMode != RelayModes.DisableFallback && !string.IsNullOrWhiteSpace(secret) && turnUris.Length > 0)
        {
            var expiry = time.GetUtcNow().ToUnixTimeSeconds() + settings.CredentialTtlSeconds;
            var username = FormattableString.Invariant($"{expiry}:{deviceId}");
            var credential = Convert.ToBase64String(HMACSHA1.HashData(
                Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(username)));
            servers.Add(new IceServerEntry(turnUris, username, credential));
        }

        return new IceServersResponse(servers, settings.CredentialTtlSeconds);
    }
}
```

- [ ] **Step 4: Update the call site**

In `services/SonicRelay.Api/Endpoints/WebRtcEndpoints.cs`, change `GetIceServersAsync`:

```csharp
private static async Task<IResult> GetIceServersAsync(ClaimsPrincipal principal, AppDbContext db,
    TurnCredentialService credentials, CancellationToken ct)
{
    var device = await DeviceIdentityEndpoints.RequireDeviceAsync(principal, db, ct);
    if (device is null) return Results.Unauthorized();
    return Results.Ok(await credentials.BuildAsync(device.Id.ToString("D"), ct));
}
```

- [ ] **Step 5: Fix the DI lifetime**

In `services/SonicRelay.Api/Program.cs`, change:

```csharp
builder.Services.AddSingleton<TurnCredentialService>();
```

to:

```csharp
builder.Services.AddScoped<TurnCredentialService>();
```

(It now depends on the scoped `AppDbContext`; leaving it a singleton would throw a
captive-dependency validation error at startup.)

- [ ] **Step 6: Run the full WebRtcEndpoints test file and verify everything passes**

Run: `dotnet test tests/SonicRelay.Api.IntegrationTests/SonicRelay.Api.IntegrationTests.csproj --filter FullyQualifiedName~WebRtcEndpointsTests`
Expected: PASS — all pre-existing tests in this file (the ones covering `appsettings.json`-only
resolution, host derivation, flat env vars) keep passing unmodified, plus the two new ones.

- [ ] **Step 7: Commit**

```bash
git add services/SonicRelay.Api/Services/TurnCredentialService.cs services/SonicRelay.Api/Endpoints/WebRtcEndpoints.cs services/SonicRelay.Api/Program.cs tests/SonicRelay.Api.IntegrationTests/WebRtcEndpointsTests.cs
git commit -m "TurnCredentialService reads the RelaySettings override and honors disableFallback"
```

---

### Task 3: `/api/settings/relay` GET/PUT endpoints

**Files:**
- Create: `services/SonicRelay.Api/Contracts/SettingsContracts.cs`
- Create: `services/SonicRelay.Api/Endpoints/SettingsEndpoints.cs`
- Modify: `services/SonicRelay.Api/Program.cs:189-192` (register the new endpoint group)
- Test: `tests/SonicRelay.Api.IntegrationTests/SettingsEndpointsTests.cs`

**Interfaces:**
- Consumes: `RelaySettings`/`RelayModes` (Task 1), `AppDbContext.RelaySettings` (Task 1),
  `DeviceIdentityTestHelper.BootstrapAndAuthorizeAsync` (existing test helper).
- Produces: `RelaySettingsResponse(string RelayMode, IReadOnlyList<string> TurnUris, bool
  HasCustomTurnSecret)`, `UpdateRelaySettingsRequest(string? RelayMode, IReadOnlyList<string>?
  TurnUris, string? TurnStaticAuthSecret)`, both consumed later by
  `windows_SonicRelay`/`flutter_SonicRelay` client code.

- [ ] **Step 1: Write the failing tests**

```csharp
using System.Net;
using System.Net.Http.Json;
using SonicRelay.Api.Contracts;
using SonicRelay.Domain.Devices;
using Xunit;

namespace SonicRelay.Api.IntegrationTests;

public sealed class SettingsEndpointsTests : IClassFixture<SonicRelayApiFactory>
{
    private readonly SonicRelayApiFactory _factory;

    public SettingsEndpointsTests(SonicRelayApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Get_relay_settings_requires_authentication()
    {
        var response = await _factory.CreateClient().GetAsync("/api/settings/relay");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Get_relay_settings_defaults_to_automatic_with_no_override()
    {
        var client = await BootstrapAsync();

        var response = await client.GetAsync("/api/settings/relay");

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<RelaySettingsResponse>();
        Assert.Equal("automatic", body!.RelayMode);
        Assert.Empty(body.TurnUris);
        Assert.False(body.HasCustomTurnSecret);
    }

    [Fact]
    public async Task Put_relay_settings_rejects_an_unknown_mode()
    {
        var client = await BootstrapAsync();

        var response = await client.PutAsJsonAsync("/api/settings/relay",
            new UpdateRelaySettingsRequest("bogus", null, null));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Put_then_get_round_trips_relay_mode_without_echoing_the_secret()
    {
        var client = await BootstrapAsync();

        var putResponse = await client.PutAsJsonAsync("/api/settings/relay",
            new UpdateRelaySettingsRequest("disableFallback", ["turn:mine.example.com:3478"], "my-secret"));
        putResponse.EnsureSuccessStatusCode();
        var putBody = await putResponse.Content.ReadFromJsonAsync<RelaySettingsResponse>();
        Assert.Equal("disableFallback", putBody!.RelayMode);
        Assert.True(putBody.HasCustomTurnSecret);

        var getResponse = await client.GetAsync("/api/settings/relay");
        var getBody = await getResponse.Content.ReadFromJsonAsync<RelaySettingsResponse>();
        Assert.Equal("disableFallback", getBody!.RelayMode);
        Assert.Equal(["turn:mine.example.com:3478"], getBody.TurnUris);
        Assert.True(getBody.HasCustomTurnSecret);
        Assert.DoesNotContain("my-secret", await getResponse.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Put_with_only_relay_mode_leaves_previously_stored_turn_uris_untouched()
    {
        var client = await BootstrapAsync();
        await client.PutAsJsonAsync("/api/settings/relay",
            new UpdateRelaySettingsRequest("automatic", ["turn:kept.example.com:3478"], null));

        var response = await client.PutAsJsonAsync("/api/settings/relay",
            new UpdateRelaySettingsRequest("forceRelay", null, null));

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<RelaySettingsResponse>();
        Assert.Equal("forceRelay", body!.RelayMode);
        Assert.Equal(["turn:kept.example.com:3478"], body.TurnUris);
    }

    private async Task<HttpClient> BootstrapAsync()
    {
        var client = _factory.CreateClient();
        await DeviceIdentityTestHelper.BootstrapAndAuthorizeAsync(
            client, DeviceTypes.WindowsPublisher, DevicePlatforms.Windows);
        return client;
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/SonicRelay.Api.IntegrationTests/SonicRelay.Api.IntegrationTests.csproj --filter FullyQualifiedName~SettingsEndpointsTests`
Expected: FAIL to build — `RelaySettingsResponse`/`UpdateRelaySettingsRequest` and the
`/api/settings/relay` route don't exist yet.

- [ ] **Step 3: Add the contracts**

```csharp
namespace SonicRelay.Api.Contracts;

public sealed record RelaySettingsResponse(string RelayMode, IReadOnlyList<string> TurnUris, bool HasCustomTurnSecret);

public sealed record UpdateRelaySettingsRequest(string? RelayMode, IReadOnlyList<string>? TurnUris, string? TurnStaticAuthSecret);
```

- [ ] **Step 4: Add the endpoint group**

```csharp
using Microsoft.EntityFrameworkCore;
using SonicRelay.Api.Contracts;
using SonicRelay.Domain.RelaySettings;
using SonicRelay.Infrastructure.Persistence;

namespace SonicRelay.Api.Endpoints;

public static class SettingsEndpoints
{
    public static IEndpointRouteBuilder MapSettingsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/settings").WithTags("Settings");
        group.MapGet("/relay", GetRelaySettingsAsync).RequireAuthorization("device:manage");
        group.MapPut("/relay", UpdateRelaySettingsAsync).RequireAuthorization("device:manage");
        return app;
    }

    private static async Task<IResult> GetRelaySettingsAsync(AppDbContext db, CancellationToken ct)
    {
        var row = await db.RelaySettings.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == RelaySettings.SingletonId, ct);
        return Results.Ok(ToResponse(row));
    }

    private static async Task<IResult> UpdateRelaySettingsAsync(
        UpdateRelaySettingsRequest request, AppDbContext db, TimeProvider time, CancellationToken ct)
    {
        if (request.RelayMode is { } mode && !RelayModes.IsValid(mode))
        {
            return Results.BadRequest(new { error = "relayMode must be one of: automatic, forceRelay, disableFallback." });
        }
        if (request.TurnUris is { } uris && uris.Any(string.IsNullOrWhiteSpace))
        {
            return Results.BadRequest(new { error = "turnUris entries must not be blank." });
        }

        var row = await db.RelaySettings.SingleOrDefaultAsync(x => x.Id == RelaySettings.SingletonId, ct);
        if (row is null)
        {
            row = new RelaySettings { Id = RelaySettings.SingletonId };
            db.RelaySettings.Add(row);
        }

        if (request.RelayMode is { } newMode) row.RelayMode = newMode;
        if (request.TurnUris is { } newUris) row.TurnUris = newUris.ToArray();
        if (request.TurnStaticAuthSecret is { } newSecret) row.TurnStaticAuthSecret = newSecret;
        row.UpdatedAt = time.GetUtcNow();

        await db.SaveChangesAsync(ct);
        return Results.Ok(ToResponse(row));
    }

    private static RelaySettingsResponse ToResponse(RelaySettings? row) => new(
        row?.RelayMode ?? RelayModes.Automatic,
        row?.TurnUris ?? [],
        !string.IsNullOrWhiteSpace(row?.TurnStaticAuthSecret));
}
```

- [ ] **Step 5: Register the endpoint group**

In `services/SonicRelay.Api/Program.cs`, next to the other `app.Map*Endpoints()` calls:

```csharp
app.MapDeviceIdentityEndpoints();
app.MapPairingEndpoints();
app.MapSessionEndpoints();
app.MapWebRtcEndpoints();
app.MapSettingsEndpoints();
```

- [ ] **Step 6: Run the tests and verify they pass**

Run: `dotnet test tests/SonicRelay.Api.IntegrationTests/SonicRelay.Api.IntegrationTests.csproj --filter FullyQualifiedName~SettingsEndpointsTests`
Expected: PASS.

- [ ] **Step 7: Run the whole integration test project**

Run: `dotnet test tests/SonicRelay.Api.IntegrationTests/SonicRelay.Api.IntegrationTests.csproj`
Expected: PASS, no regressions in any other test file.

- [ ] **Step 8: Commit**

```bash
git add services/SonicRelay.Api/Contracts/SettingsContracts.cs services/SonicRelay.Api/Endpoints/SettingsEndpoints.cs services/SonicRelay.Api/Program.cs tests/SonicRelay.Api.IntegrationTests/SettingsEndpointsTests.cs
git commit -m "Add GET/PUT /api/settings/relay"
```

---

## Self-review notes (already applied above)

- Spec coverage: singleton/global settings (Task 1), `device:manage` auth + partial PUT +
  secret never echoed (Task 3), `TurnCredentialService` field-by-field fallback +
  `disableFallback` omitting TURN (Task 2), no STUN override (not implemented anywhere above —
  confirmed absent by design). Migration (Task 1, Step 6). All backend items from the spec's
  "Backend: relay & coturn settings" section are covered; client-side consumption is out of
  scope for this plan (covered by the `windows_SonicRelay`/`flutter_SonicRelay` plans).
- No placeholders: every step above has literal file contents, not descriptions.
- Type consistency: `BuildAsync(string deviceId, CancellationToken)` is defined once in Task 2
  and its only call site is updated in the same task; `RelaySettingsResponse`/
  `UpdateRelaySettingsRequest` field names match between Task 3's contracts and its own tests.
