# Public Radio Room — Plan 2: Orchestration & Wiring

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking. **Depends on Plan 1 (`docs/superpowers/plans/2026-08-19-public-radio-room-1-audio-pipeline.md`) being complete** — this plan consumes `Mp3TrackSource`, `NLayerMp3Decoder`, and `VirtualPublisherPeerConnection` from it.

**Goal:** Wire the Plan 1 audio pipeline into `SonicRelay.Api` as an always-on, toggleable public radio room: a virtual publisher device, a well-known public `StreamSession`, a discovery endpoint that also auto-pairs the caller, and a `BackgroundService` that connects as publisher and streams the looping MP3 playlist to up to 20 viewers — all configured via docker-compose environment variables and a mounted volume.

**Architecture:** A new `BackgroundService` (`PublicRoomPublisherService`, same pattern as the existing `DataRetentionService`/`SessionCleanupService`) seeds a system `DeviceIdentity`, ensures a public `StreamSession` + publisher `SessionParticipant` row exist, and connects to `/ws/signaling` as a headless `ClientWebSocket` (same protocol every publisher already speaks — no changes to `SignalingWebSocketEndpoint` or `SessionEndpoints`). A new `GET /api/public-room` endpoint lets the Flutter app discover the session id and, as a side effect, auto-creates the `DevicePairing` that `JoinByIdAsync` already requires (see the spec's "Correção pós-design" note) — reusing the exact same authorization model as manual QR pairing, so nothing in the existing join/pairing code changes.

**Tech Stack:** .NET 10, EF Core (existing `AppDbContext`), `System.Net.WebSockets.ClientWebSocket`, xUnit + `Microsoft.AspNetCore.Mvc.Testing`.

**Spec:** `docs/superpowers/specs/2026-08-19-public-radio-room-design.md`

## Global Constraints

- Every new option is bound from `IConfiguration` once, read at `BackgroundService` startup — no `Microsoft.FeatureManagement`, no dynamic runtime toggling (see spec section 8).
- `PUBLICROOM__ENABLED=false` (or unset) must mean **zero work**: no DB writes, no WebSocket connection, no file reads. This is asserted directly in Task 3's test.
- All new env vars use the existing `Section__Key` double-underscore convention (`PUBLICROOM__ENABLED`, `PUBLICROOM__TRACKSPATH`, `PUBLICROOM__MAXVIEWERS`), matching `DeviceIdentity__*`, `DataRetention__*` already in the codebase.
- The public room's `StreamSession.SourceDeviceId` and the virtual publisher's `DeviceIdentity.Id` use a **fixed, well-known GUID** (not `Guid.NewGuid()` at every startup) so the session/device are stable across restarts and idempotent to re-seed.
- No changes to `SessionEndpoints.cs`, `PairingEndpoints.cs`, or `SignalingWebSocketEndpoint.cs` — everything here is additive, reusing those endpoints exactly as they exist today.

---

## File Structure

- Create: `services/SonicRelay.Api/Services/PublicRoomOptions.cs` — config binding
- Create: `services/SonicRelay.Api/Services/PublicRoomSeeder.cs` — ensures the virtual publisher `DeviceIdentity` + public `StreamSession` + publisher `SessionParticipant` exist (idempotent)
- Create: `services/SonicRelay.Api/Services/PublicRoomSignalingClient.cs` — headless `ClientWebSocket` publisher connection, adapted from `tools/SonicRelay.SignalingClient/SignalingClient.cs`'s connect/send/receive plumbing
- Create: `services/SonicRelay.Api/Services/PublicRoomPublisherService.cs` — the `BackgroundService`
- Create: `services/SonicRelay.Api/Endpoints/PublicRoomEndpoints.cs` — `GET /api/public-room`
- Create: `services/SonicRelay.Api/Contracts/PublicRoomContracts.cs` — response DTO
- Modify: `services/SonicRelay.Api/Program.cs` — register options + hosted service + endpoint mapping
- Modify: `deploy/docker-compose.prod.yml` — env vars + volume
- Create: `tests/SonicRelay.Api.IntegrationTests/PublicRoomSeederTests.cs`
- Create: `tests/SonicRelay.Api.IntegrationTests/PublicRoomEndpointsTests.cs`
- Create: `tests/SonicRelay.Api.IntegrationTests/PublicRoomPublisherServiceTests.cs`
- Modify: `services/SonicRelay.Api/SonicRelay.Api.csproj` — project reference to `SonicRelay.Infrastructure.VirtualPublisher`

---

### Task 1: `PublicRoomOptions`

**Files:**
- Create: `services/SonicRelay.Api/Services/PublicRoomOptions.cs`
- Test: `tests/SonicRelay.Api.IntegrationTests/PublicRoomOptionsTests.cs`

**Interfaces:**
- Produces: `PublicRoomOptions { bool Enabled; string TracksPath; int MaxViewers; }` bound from config section `"PublicRoom"`, with `MaxViewers` defaulting to 20 and clamped to `[1, 500]`. Consumed by every later task in this plan.

- [ ] **Step 1: Write the failing test**

```csharp
using Microsoft.Extensions.Configuration;
using SonicRelay.Api.Services;
using Xunit;

namespace SonicRelay.Api.IntegrationTests;

public sealed class PublicRoomOptionsTests
{
    [Fact]
    public void Binds_from_configuration_with_documented_defaults()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["PublicRoom:Enabled"] = "true",
            ["PublicRoom:TracksPath"] = "/app/tracks",
        }).Build();

        var options = new PublicRoomOptions();
        configuration.GetSection("PublicRoom").Bind(options);

        Assert.True(options.Enabled);
        Assert.Equal("/app/tracks", options.TracksPath);
        Assert.Equal(20, options.MaxViewers); // not set above -> default
    }

    [Fact]
    public void Defaults_to_disabled_when_the_section_is_absent()
    {
        var configuration = new ConfigurationBuilder().Build();

        var options = new PublicRoomOptions();
        configuration.GetSection("PublicRoom").Bind(options);

        Assert.False(options.Enabled);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1000, 500)]
    [InlineData(20, 20)]
    public void EffectiveMaxViewers_clamps_to_the_supported_range(int configured, int expected)
    {
        var options = new PublicRoomOptions { MaxViewers = configured };
        Assert.Equal(expected, options.EffectiveMaxViewers);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/SonicRelay.Api.IntegrationTests --filter PublicRoomOptionsTests`
Expected: FAIL to compile — `PublicRoomOptions` does not exist.

- [ ] **Step 3: Write the implementation**

```csharp
namespace SonicRelay.Api.Services;

/// <summary>
/// Public radio room configuration (docs/superpowers/specs/2026-08-19-public-radio-room-design.md).
/// Bound once at startup from the "PublicRoom" section / PUBLICROOM__* environment
/// variables — there is no runtime toggle, matching every other BackgroundService
/// option in this codebase.
/// </summary>
public sealed class PublicRoomOptions
{
    public const string SectionName = "PublicRoom";

    /// <summary>Master switch. False (the default) means the feature does nothing at all.</summary>
    public bool Enabled { get; set; }

    /// <summary>Directory (inside the container) containing the *.mp3 files to play, in order.</summary>
    public string TracksPath { get; set; } = "/app/tracks";

    public int MaxViewers { get; set; } = 20;

    public int EffectiveMaxViewers => Math.Clamp(MaxViewers, 1, 500);
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/SonicRelay.Api.IntegrationTests --filter PublicRoomOptionsTests`
Expected: PASS (4 tests).

- [ ] **Step 5: Commit**

```bash
git add services/SonicRelay.Api/Services/PublicRoomOptions.cs tests/SonicRelay.Api.IntegrationTests/PublicRoomOptionsTests.cs
git commit -m "Add PublicRoomOptions"
```

---

### Task 2: Reference `SonicRelay.Infrastructure.VirtualPublisher` from the API

**Files:**
- Modify: `services/SonicRelay.Api/SonicRelay.Api.csproj`

**Interfaces:**
- Produces: `SonicRelay.Api` can now reference types from `SonicRelay.Infrastructure.VirtualPublisher` (Plan 1).

- [ ] **Step 1: Add the project reference**

In `services/SonicRelay.Api/SonicRelay.Api.csproj`, inside the existing `<ItemGroup>` that has the `SonicRelay.Infrastructure` reference:

```xml
    <ProjectReference Include="..\..\src\SonicRelay.Infrastructure.VirtualPublisher\SonicRelay.Infrastructure.VirtualPublisher.csproj" />
```

- [ ] **Step 2: Verify it builds**

Run: `dotnet build services/SonicRelay.Api/SonicRelay.Api.csproj`
Expected: Build succeeded.

- [ ] **Step 3: Commit**

```bash
git add services/SonicRelay.Api/SonicRelay.Api.csproj
git commit -m "Reference SonicRelay.Infrastructure.VirtualPublisher from the API"
```

---

### Task 3: `PublicRoomSeeder` — idempotent device + session + publisher-participant setup

**Files:**
- Create: `services/SonicRelay.Api/Services/PublicRoomSeeder.cs`
- Test: `tests/SonicRelay.Api.IntegrationTests/PublicRoomSeederTests.cs`

**Interfaces:**
- Consumes: `AppDbContext` (existing), `PublicRoomOptions` (Task 1).
- Produces: `PublicRoomSeeder` with:
  - `public static readonly Guid VirtualPublisherDeviceId` — fixed constant.
  - `public static readonly Guid PublicSessionId` — fixed constant.
  - `Task<StreamSession> EnsureSeededAsync(AppDbContext db, TimeProvider time, CancellationToken ct)` — idempotent: creates the `DeviceIdentity`/`StreamSession`/publisher `SessionParticipant` rows on first call, returns the existing ones on subsequent calls, never throws on re-seed.
  Consumed by `PublicRoomPublisherService` (Task 6) and `PublicRoomEndpoints` (Task 7).

- [ ] **Step 1: Write the failing test**

```csharp
using Microsoft.EntityFrameworkCore;
using SonicRelay.Api.Services;
using SonicRelay.Domain.DeviceIdentities;
using SonicRelay.Domain.Sessions;
using SonicRelay.Infrastructure.Persistence;
using Xunit;

namespace SonicRelay.Api.IntegrationTests;

public sealed class PublicRoomSeederTests
{
    private static AppDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"public-room-seeder-{Guid.NewGuid()}").Options);

    [Fact]
    public async Task EnsureSeededAsync_creates_device_session_and_publisher_participant()
    {
        await using var db = CreateDb();

        var session = await new PublicRoomSeeder().EnsureSeededAsync(db, TimeProvider.System, CancellationToken.None);

        Assert.Equal(PublicRoomSeeder.PublicSessionId, session.Id);
        Assert.Equal(PublicRoomSeeder.VirtualPublisherDeviceId, session.SourceDeviceId);

        var device = await db.DeviceIdentities.SingleAsync(x => x.Id == PublicRoomSeeder.VirtualPublisherDeviceId);
        Assert.Equal(DeviceIdentityStatuses.Active, device.Status);

        var participant = await db.SessionParticipants.SingleAsync(x =>
            x.SessionId == session.Id && x.DeviceId == device.Id);
        Assert.Equal(ParticipantRoles.Publisher, participant.Role);
    }

    [Fact]
    public async Task EnsureSeededAsync_is_idempotent_across_repeated_calls()
    {
        await using var db = CreateDb();
        var seeder = new PublicRoomSeeder();

        await seeder.EnsureSeededAsync(db, TimeProvider.System, CancellationToken.None);
        var second = await seeder.EnsureSeededAsync(db, TimeProvider.System, CancellationToken.None);

        Assert.Equal(1, await db.DeviceIdentities.CountAsync(x => x.Id == PublicRoomSeeder.VirtualPublisherDeviceId));
        Assert.Equal(1, await db.StreamSessions.CountAsync(x => x.Id == PublicRoomSeeder.PublicSessionId));
        Assert.Equal(1, await db.SessionParticipants.CountAsync(x =>
            x.SessionId == PublicRoomSeeder.PublicSessionId && x.Role == ParticipantRoles.Publisher));
        Assert.Equal(PublicRoomSeeder.PublicSessionId, second.Id);
    }

    [Fact]
    public async Task EnsureSeededAsync_reactivates_a_revoked_device()
    {
        await using var db = CreateDb();
        var seeder = new PublicRoomSeeder();
        await seeder.EnsureSeededAsync(db, TimeProvider.System, CancellationToken.None);
        var device = await db.DeviceIdentities.SingleAsync(x => x.Id == PublicRoomSeeder.VirtualPublisherDeviceId);
        device.Status = DeviceIdentityStatuses.Revoked;
        await db.SaveChangesAsync();

        await seeder.EnsureSeededAsync(db, TimeProvider.System, CancellationToken.None);

        var reloaded = await db.DeviceIdentities.SingleAsync(x => x.Id == PublicRoomSeeder.VirtualPublisherDeviceId);
        Assert.Equal(DeviceIdentityStatuses.Active, reloaded.Status);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/SonicRelay.Api.IntegrationTests --filter PublicRoomSeederTests`
Expected: FAIL to compile.

- [ ] **Step 3: Write the implementation**

```csharp
using Microsoft.EntityFrameworkCore;
using SonicRelay.Domain.DeviceIdentities;
using SonicRelay.Domain.Devices;
using SonicRelay.Domain.Sessions;
using SonicRelay.Infrastructure.Persistence;

namespace SonicRelay.Api.Services;

/// <summary>
/// Idempotently ensures the public radio's virtual publisher device, its
/// well-known public session, and the publisher's own SessionParticipant row all
/// exist. Safe to call on every startup and does not throw when the rows are
/// already there (re-activates a revoked device rather than erroring).
/// </summary>
public sealed class PublicRoomSeeder
{
    public static readonly Guid VirtualPublisherDeviceId = new("0b1e5a7a-0000-4000-8000-000000000001");
    public static readonly Guid PublicSessionId = new("0b1e5a7a-0000-4000-8000-000000000002");

    public async Task<StreamSession> EnsureSeededAsync(AppDbContext db, TimeProvider time, CancellationToken ct)
    {
        var now = time.GetUtcNow();

        var device = await db.DeviceIdentities.SingleOrDefaultAsync(x => x.Id == VirtualPublisherDeviceId, ct);
        if (device is null)
        {
            device = new DeviceIdentity
            {
                Id = VirtualPublisherDeviceId,
                Name = "Public Radio (virtual publisher)",
                DeviceType = DeviceTypes.WindowsPublisher,
                Platform = DevicePlatforms.Windows,
                // Never used to obtain a token via /api/devices/token: PublicRoomPublisherService
                // mints the JWT directly via DeviceCredentialService.IssueAccessToken. The hash
                // still has to be non-empty to satisfy the column, so it is random and unused.
                CredentialSecretHash = Convert.ToHexString(Guid.NewGuid().ToByteArray()),
                CredentialVersion = 1,
                Status = DeviceIdentityStatuses.Active,
                CreatedAt = now
            };
            db.DeviceIdentities.Add(device);
        }
        else if (device.Status != DeviceIdentityStatuses.Active)
        {
            device.Status = DeviceIdentityStatuses.Active;
            device.RevokedAt = null;
        }

        var session = await db.StreamSessions.SingleOrDefaultAsync(x => x.Id == PublicSessionId, ct);
        if (session is null)
        {
            session = new StreamSession
            {
                Id = PublicSessionId,
                SourceDeviceId = VirtualPublisherDeviceId,
                Status = SessionStatuses.Active,
                MaxViewers = 20, // caller (PublicRoomPublisherService) overwrites from PublicRoomOptions
                CodeExpiresAt = DateTimeOffset.MaxValue, // never used: joins go through JoinByIdAsync, not the code path
                StartedAt = now,
                CreatedAt = now
            };
            db.StreamSessions.Add(session);
        }

        var publisherParticipant = await db.SessionParticipants.SingleOrDefaultAsync(x =>
            x.SessionId == PublicSessionId && x.DeviceId == VirtualPublisherDeviceId
            && x.Role == ParticipantRoles.Publisher, ct);
        if (publisherParticipant is null)
        {
            db.SessionParticipants.Add(new SessionParticipant
            {
                Id = Guid.NewGuid(),
                SessionId = PublicSessionId,
                DeviceId = VirtualPublisherDeviceId,
                Role = ParticipantRoles.Publisher,
                Status = ParticipantStatuses.Connected,
                JoinedAt = now
            });
        }

        await db.SaveChangesAsync(ct);
        return session;
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/SonicRelay.Api.IntegrationTests --filter PublicRoomSeederTests`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
git add services/SonicRelay.Api/Services/PublicRoomSeeder.cs tests/SonicRelay.Api.IntegrationTests/PublicRoomSeederTests.cs
git commit -m "Add PublicRoomSeeder (idempotent device/session/participant setup)"
```

---

### Task 4: `PublicRoomContracts` — discovery response DTO

**Files:**
- Create: `services/SonicRelay.Api/Contracts/PublicRoomContracts.cs`

**Interfaces:**
- Produces: `PublicRoomResponse(bool Enabled, Guid? SessionId, int MaxViewers)`. Consumed by `PublicRoomEndpoints` (Task 7).

- [ ] **Step 1: Write the implementation**

```csharp
namespace SonicRelay.Api.Contracts;

public sealed record PublicRoomResponse(bool Enabled, Guid? SessionId, int MaxViewers);
```

- [ ] **Step 2: Verify it builds**

Run: `dotnet build services/SonicRelay.Api/SonicRelay.Api.csproj`
Expected: Build succeeded.

- [ ] **Step 3: Commit**

```bash
git add services/SonicRelay.Api/Contracts/PublicRoomContracts.cs
git commit -m "Add PublicRoomResponse contract"
```

---

### Task 5: `GET /api/public-room` — discovery + auto-pairing

**Files:**
- Create: `services/SonicRelay.Api/Endpoints/PublicRoomEndpoints.cs`
- Modify: `services/SonicRelay.Api/Program.cs` — add `app.MapPublicRoomEndpoints();` and `builder.Services.Configure<PublicRoomOptions>(...)`
- Test: `tests/SonicRelay.Api.IntegrationTests/PublicRoomEndpointsTests.cs`

**Interfaces:**
- Consumes: `PublicRoomOptions` (Task 1), `PublicRoomSeeder` (Task 3), `DeviceIdentityEndpoints.RequireDeviceAsync` (existing).
- Produces: `GET /api/public-room` behind `RequireAuthorization("DeviceAuthenticated")`, returning `PublicRoomResponse`. When `PublicRoomOptions.Enabled` is false, returns `{ enabled: false, sessionId: null, maxViewers: 0 }` and does **not** touch the database (no seeding, no pairing) — the whole feature must stay inert while disabled. When enabled, seeds (Task 3) and get-or-creates an `Active` `DevicePairing(PublisherDeviceId = VirtualPublisherDeviceId, ViewerDeviceId = caller.Id)`.

- [ ] **Step 1: Write the failing tests**

```csharp
using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using SonicRelay.Api.Contracts;
using SonicRelay.Domain.DeviceIdentities;
using SonicRelay.Domain.Devices;
using SonicRelay.Infrastructure.Persistence;
using Xunit;

namespace SonicRelay.Api.IntegrationTests;

public sealed class PublicRoomEndpointsTests : IClassFixture<SonicRelayApiFactory>
{
    private readonly SonicRelayApiFactory _disabledFactory = new();

    [Fact]
    public async Task Returns_disabled_and_touches_nothing_when_the_feature_is_off()
    {
        var client = _disabledFactory.CreateClient();
        var session = await DeviceIdentityTestHelper.BootstrapAndAuthorizeAsync(
            client, DeviceTypes.FlutterViewer, DevicePlatforms.Android);

        var response = await client.GetAsync("/api/public-room");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<PublicRoomResponse>();
        Assert.False(body!.Enabled);
        Assert.Null(body.SessionId);

        using var scope = _disabledFactory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await db.StreamSessions.AnyAsync(x => x.Id == PublicRoomSeeder.PublicSessionId));
    }

    [Fact]
    public async Task Requires_authentication()
    {
        var response = await _disabledFactory.CreateClient().GetAsync("/api/public-room");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Returns_session_and_auto_pairs_when_enabled()
    {
        await using var factory = new SonicRelayApiFactory(new Dictionary<string, string?>
        {
            ["PublicRoom:Enabled"] = "true",
            ["PublicRoom:MaxViewers"] = "20",
        });
        var client = factory.CreateClient();
        var session = await DeviceIdentityTestHelper.BootstrapAndAuthorizeAsync(
            client, DeviceTypes.FlutterViewer, DevicePlatforms.Android);

        var response = await client.GetAsync("/api/public-room");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<PublicRoomResponse>();
        Assert.True(body!.Enabled);
        Assert.Equal(PublicRoomSeeder.PublicSessionId, body.SessionId);
        Assert.Equal(20, body.MaxViewers);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var pairing = await db.DevicePairings.SingleAsync(x =>
            x.PublisherDeviceId == PublicRoomSeeder.VirtualPublisherDeviceId && x.ViewerDeviceId == session.DeviceId);
        Assert.Equal(DevicePairingStatuses.Active, pairing.Status);
    }

    [Fact]
    public async Task Second_call_reuses_the_same_pairing_instead_of_duplicating_it()
    {
        await using var factory = new SonicRelayApiFactory(new Dictionary<string, string?>
        {
            ["PublicRoom:Enabled"] = "true",
        });
        var client = factory.CreateClient();
        var session = await DeviceIdentityTestHelper.BootstrapAndAuthorizeAsync(
            client, DeviceTypes.FlutterViewer, DevicePlatforms.Android);

        await client.GetAsync("/api/public-room");
        await client.GetAsync("/api/public-room");

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(1, await db.DevicePairings.CountAsync(x =>
            x.PublisherDeviceId == PublicRoomSeeder.VirtualPublisherDeviceId && x.ViewerDeviceId == session.DeviceId));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/SonicRelay.Api.IntegrationTests --filter PublicRoomEndpointsTests`
Expected: FAIL to compile.

- [ ] **Step 3: Write the endpoint implementation**

```csharp
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SonicRelay.Api.Contracts;
using SonicRelay.Api.Services;
using SonicRelay.Domain.DeviceIdentities;
using SonicRelay.Infrastructure.Persistence;

namespace SonicRelay.Api.Endpoints;

public static class PublicRoomEndpoints
{
    public static IEndpointRouteBuilder MapPublicRoomEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/public-room", GetAsync)
            .RequireAuthorization("DeviceAuthenticated")
            .WithTags("PublicRoom");
        return app;
    }

    private static async Task<IResult> GetAsync(
        ClaimsPrincipal principal, AppDbContext db, IOptions<PublicRoomOptions> options,
        PublicRoomSeeder seeder, TimeProvider time, CancellationToken ct)
    {
        var settings = options.Value;
        if (!settings.Enabled)
            return Results.Ok(new PublicRoomResponse(Enabled: false, SessionId: null, MaxViewers: 0));

        var device = await DeviceIdentityEndpoints.RequireDeviceAsync(principal, db, ct);
        if (device is null) return Results.Unauthorized();

        var session = await seeder.EnsureSeededAsync(db, time, ct);

        var hasActivePairing = await db.DevicePairings.AsNoTracking().AnyAsync(x =>
            x.PublisherDeviceId == PublicRoomSeeder.VirtualPublisherDeviceId
            && x.ViewerDeviceId == device.Id
            && x.Status == DevicePairingStatuses.Active, ct);
        if (!hasActivePairing)
        {
            db.DevicePairings.Add(new DevicePairing
            {
                Id = Guid.NewGuid(),
                PublisherDeviceId = PublicRoomSeeder.VirtualPublisherDeviceId,
                ViewerDeviceId = device.Id,
                Status = DevicePairingStatuses.Active,
                CreatedAt = time.GetUtcNow(),
                LastUsedAt = time.GetUtcNow()
            });
            await db.SaveChangesAsync(ct);
        }

        return Results.Ok(new PublicRoomResponse(Enabled: true, SessionId: session.Id, settings.EffectiveMaxViewers));
    }
}
```

- [ ] **Step 4: Register options and the endpoint in `Program.cs`**

Add near the other `builder.Services.Configure<...Options>` calls:

```csharp
builder.Services.Configure<PublicRoomOptions>(builder.Configuration.GetSection(PublicRoomOptions.SectionName));
builder.Services.AddSingleton<PublicRoomSeeder>();
```

Add alongside the other `app.Map...Endpoints()` calls (after `app.MapSignalingWebSocketEndpoint();`):

```csharp
app.MapPublicRoomEndpoints();
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/SonicRelay.Api.IntegrationTests --filter PublicRoomEndpointsTests`
Expected: PASS (4 tests).

- [ ] **Step 6: Commit**

```bash
git add services/SonicRelay.Api/Endpoints/PublicRoomEndpoints.cs services/SonicRelay.Api/Program.cs tests/SonicRelay.Api.IntegrationTests/PublicRoomEndpointsTests.cs
git commit -m "Add GET /api/public-room discovery + auto-pairing endpoint"
```

---

### Task 6: `PublicRoomSignalingClient` — headless publisher WebSocket

**Files:**
- Create: `services/SonicRelay.Api/Services/PublicRoomSignalingClient.cs`
- Test: none (a real WebSocket handshake against Kestrel's own `/ws/signaling` inside the same test process is exercised end-to-end by `PublicRoomPublisherServiceTests`, Task 8, via `SonicRelayApiFactory`'s in-memory `TestServer`; this class's job is thin protocol plumbing already covered indirectly there)

**Interfaces:**
- Produces: `PublicRoomSignalingClient(Uri baseUrl, string accessToken)` with:
  - `Task ConnectAsPublisherAsync(Guid sessionId, CancellationToken ct)` — opens the WebSocket, reads the `session.joined` envelope, returns the publisher's own `participantId` (stored internally).
  - `event Func<Guid viewerParticipantId, string type, System.Text.Json.JsonElement payload, CancellationToken, Task>? MessageReceived` — raised for every inbound envelope after `session.joined` (namely `viewer.ready`, `webrtc.answer`, `webrtc.ice_candidate`).
  - `Task SendAsync(Guid toParticipantId, string type, object payload, CancellationToken ct)`
  - `Task RunReceiveLoopAsync(CancellationToken ct)` — pumps inbound frames into `MessageReceived` until cancelled or the socket closes.
  - `IAsyncDisposable`
  Consumed by `PublicRoomPublisherService` (Task 7 in this plan — note the plan's own Task 7 is the endpoint above; `PublicRoomPublisherService` is Task 8 below).

- [ ] **Step 1: Write the implementation**

```csharp
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text.Json;

namespace SonicRelay.Api.Services;

/// <summary>
/// Headless signaling WebSocket client for the public radio's virtual publisher —
/// same wire protocol every real publisher device speaks against
/// SignalingWebSocketEndpoint (see tools/SonicRelay.SignalingClient for the
/// reference client this is adapted from), just used from inside the API process
/// itself instead of an external device.
/// </summary>
public sealed class PublicRoomSignalingClient(Uri baseUrl, string accessToken) : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ClientWebSocket socket = new();
    private Guid sessionId;
    private bool disposed;

    public Guid PublisherParticipantId { get; private set; }

    public event Func<Guid, string, JsonElement, CancellationToken, Task>? MessageReceived;

    public async Task ConnectAsPublisherAsync(Guid sessionIdToJoin, CancellationToken ct)
    {
        sessionId = sessionIdToJoin;
        socket.Options.SetRequestHeader("Authorization", new AuthenticationHeaderValue("Bearer", accessToken).ToString());
        var scheme = baseUrl.Scheme == Uri.UriSchemeHttps ? "wss" : "ws";
        var uri = new UriBuilder(new Uri(baseUrl, "ws/signaling"))
        {
            Scheme = scheme,
            Query = $"sessionId={sessionId}"
        }.Uri;
        await socket.ConnectAsync(uri, ct).ConfigureAwait(false);

        var envelope = await ReceiveAsync(ct).ConfigureAwait(false);
        if (envelope.GetProperty("type").GetString() != "session.joined")
            throw new InvalidOperationException("Expected session.joined as the first signaling message.");
        PublisherParticipantId = envelope.GetProperty("payload").GetProperty("participantId").GetGuid();
    }

    public async Task SendAsync(Guid toParticipantId, string type, object payload, CancellationToken ct)
    {
        var envelope = new
        {
            type,
            messageId = Guid.NewGuid(),
            to = toParticipantId,
            payload = JsonSerializer.SerializeToElement(payload, JsonOptions)
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, JsonOptions);
        await socket.SendAsync(bytes, WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
    }

    public async Task RunReceiveLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && socket.State == WebSocketState.Open)
        {
            JsonElement envelope;
            try
            {
                envelope = await ReceiveAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (WebSocketException)
            {
                return; // socket closed by the server or the network; caller reconnects
            }

            var type = envelope.GetProperty("type").GetString();
            if (type is null) continue;
            var from = envelope.TryGetProperty("from", out var fromProp) && fromProp.ValueKind == JsonValueKind.String
                ? fromProp.GetGuid()
                : Guid.Empty;
            var payload = envelope.TryGetProperty("payload", out var p) ? p : default;

            var handlers = MessageReceived;
            if (handlers is not null) await handlers.Invoke(from, type, payload, ct).ConfigureAwait(false);
        }
    }

    private async Task<JsonElement> ReceiveAsync(CancellationToken ct)
    {
        using var stream = new MemoryStream();
        var buffer = new byte[8192];
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer, ct).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
                throw new WebSocketException("Signaling socket closed by the server.");
            await stream.WriteAsync(buffer.AsMemory(0, result.Count), ct).ConfigureAwait(false);
        } while (!result.EndOfMessage);

        stream.Position = 0;
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        return document.RootElement.Clone();
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "shutting down", timeout.Token)
                    .ConfigureAwait(false);
            }
            catch
            {
                // Best-effort close; disposal below still releases local resources.
            }
        }
        socket.Dispose();
    }
}
```

- [ ] **Step 2: Verify it builds**

Run: `dotnet build services/SonicRelay.Api/SonicRelay.Api.csproj`
Expected: Build succeeded.

- [ ] **Step 3: Commit**

```bash
git add services/SonicRelay.Api/Services/PublicRoomSignalingClient.cs
git commit -m "Add PublicRoomSignalingClient (headless publisher WebSocket)"
```

---

### Task 7: Mint an access token for the virtual publisher device

**Files:**
- Modify: `services/SonicRelay.Api/Services/PublicRoomSeeder.cs`
- Test: `tests/SonicRelay.Api.IntegrationTests/PublicRoomSeederTests.cs` (add one test)

**Interfaces:**
- Consumes: `DeviceCredentialService.IssueAccessToken(DeviceIdentity)` (existing, from `services/SonicRelay.Api/Services/DeviceCredentialService.cs`).
- Produces: `PublicRoomSeeder.IssuePublisherTokenAsync(AppDbContext db, DeviceCredentialService credentials, TimeProvider time, CancellationToken ct)` returning `(string AccessToken, DateTimeOffset ExpiresAt)`, minted in-process (no HTTP round trip through `/api/devices/token`, since the seeded device's `CredentialSecretHash` is a random value nobody knows — see the comment added in Task 3). Consumed by `PublicRoomPublisherService` (Task 8).

- [ ] **Step 1: Add the failing test**

Append to `tests/SonicRelay.Api.IntegrationTests/PublicRoomSeederTests.cs`:

```csharp
    [Fact]
    public async Task IssuePublisherTokenAsync_mints_a_token_for_the_seeded_device()
    {
        await using var db = CreateDb();
        var seeder = new PublicRoomSeeder();
        await seeder.EnsureSeededAsync(db, TimeProvider.System, CancellationToken.None);
        var options = Microsoft.Extensions.Options.Options.Create(new DeviceIdentityOptions
        {
            TokenSigningKey = "integration-test-device-token-signing-key-32bytes-min",
            Issuer = "sonicrelay-tests",
            Audience = "sonicrelay-tests",
            AccessTokenMinutes = 60
        });
        var credentials = new DeviceCredentialService(options, TimeProvider.System);

        var (token, expiresAt) = await seeder.IssuePublisherTokenAsync(
            db, credentials, TimeProvider.System, CancellationToken.None);

        Assert.False(string.IsNullOrWhiteSpace(token));
        Assert.True(expiresAt > DateTimeOffset.UtcNow);
    }
```

(Add the corresponding `using SonicRelay.Api.Services;` and `using Microsoft.Extensions.Options;` — already present or added if missing.)

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/SonicRelay.Api.IntegrationTests --filter PublicRoomSeederTests`
Expected: FAIL to compile — `IssuePublisherTokenAsync` does not exist.

- [ ] **Step 3: Add the method to `PublicRoomSeeder`**

Add to the `PublicRoomSeeder` class body:

```csharp
    public async Task<(string AccessToken, DateTimeOffset ExpiresAt)> IssuePublisherTokenAsync(
        AppDbContext db, DeviceCredentialService credentials, TimeProvider time, CancellationToken ct)
    {
        var device = await db.DeviceIdentities.SingleAsync(x => x.Id == VirtualPublisherDeviceId, ct);
        return credentials.IssueAccessToken(device);
    }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/SonicRelay.Api.IntegrationTests --filter PublicRoomSeederTests`
Expected: PASS (4 tests).

- [ ] **Step 5: Commit**

```bash
git add services/SonicRelay.Api/Services/PublicRoomSeeder.cs tests/SonicRelay.Api.IntegrationTests/PublicRoomSeederTests.cs
git commit -m "Mint the virtual publisher's access token in-process"
```

---

### Task 8: `PublicRoomPublisherService` — the `BackgroundService`

**Files:**
- Create: `services/SonicRelay.Api/Services/PublicRoomPublisherService.cs`
- Modify: `services/SonicRelay.Api/Program.cs` — register as a hosted service
- Test: `tests/SonicRelay.Api.IntegrationTests/PublicRoomPublisherServiceTests.cs`

**Interfaces:**
- Consumes: `PublicRoomOptions` (Task 1), `PublicRoomSeeder` (Tasks 3/7), `Mp3TrackSource`/`NLayerMp3Decoder` (Plan 1 Tasks 8-9), `VirtualPublisherPeerConnection` (Plan 1 Task 11), `PublicRoomSignalingClient` (Task 6), `DeviceCredentialService` (existing).
- Produces: `PublicRoomPublisherService(IServiceScopeFactory, IOptions<PublicRoomOptions>, PublicRoomSeeder, DeviceCredentialService, IServer, TimeProvider, ILogger<PublicRoomPublisherService>) : BackgroundService`. When `Enabled=false`, `ExecuteAsync` returns immediately (same pattern as `DataRetentionService.ExecuteAsync`) — asserted directly. When enabled and `TracksPath` has no readable `*.mp3` files, logs a warning and stays idle without crashing.

- [ ] **Step 1: Write the failing tests**

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting.Server;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SonicRelay.Api.Services;
using Xunit;

namespace SonicRelay.Api.IntegrationTests;

public sealed class PublicRoomPublisherServiceTests
{
    [Fact]
    public async Task ExecuteAsync_does_nothing_when_disabled()
    {
        var factory = new SonicRelayApiFactory(new Dictionary<string, string?>
        {
            ["PublicRoom:Enabled"] = "false"
        });
        _ = factory.CreateClient(); // forces host startup, including hosted services

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SonicRelay.Infrastructure.Persistence.AppDbContext>();
        // A disabled publisher must never seed the session, matching Task 5's endpoint test.
        Assert.False(await db.StreamSessions.AnyAsync(x => x.Id == PublicRoomSeeder.PublicSessionId));
        await factory.DisposeAsync();
    }

    [Fact]
    public async Task ExecuteAsync_seeds_the_session_when_enabled_even_with_no_tracks()
    {
        var emptyTracksDir = Path.Combine(Path.GetTempPath(), "sonicrelay-public-room-empty-" + Guid.NewGuid());
        Directory.CreateDirectory(emptyTracksDir);
        var factory = new SonicRelayApiFactory(new Dictionary<string, string?>
        {
            ["PublicRoom:Enabled"] = "true",
            ["PublicRoom:TracksPath"] = emptyTracksDir,
        });
        _ = factory.CreateClient();

        // Give the hosted service's startup a moment to run its seeding step.
        await Task.Delay(TimeSpan.FromSeconds(1));

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SonicRelay.Infrastructure.Persistence.AppDbContext>();
        Assert.True(await db.StreamSessions.AnyAsync(x => x.Id == PublicRoomSeeder.PublicSessionId));
        await factory.DisposeAsync();
        Directory.Delete(emptyTracksDir, recursive: true);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/SonicRelay.Api.IntegrationTests --filter PublicRoomPublisherServiceTests`
Expected: FAIL — no hosted service registered yet, so the session is never seeded even when enabled (second test fails).

- [ ] **Step 3: Write the implementation**

```csharp
using Microsoft.Extensions.Hosting.Server;
using Microsoft.Extensions.Hosting.Server.Features;
using Microsoft.Extensions.Options;
using SonicRelay.Infrastructure.Persistence;
using SonicRelay.Infrastructure.VirtualPublisher.Audio;
using SonicRelay.Infrastructure.VirtualPublisher.WebRtc;
using SIPSorcery.Net;

namespace SonicRelay.Api.Services;

/// <summary>
/// Public radio room virtual publisher (docs/superpowers/specs/2026-08-19-public-radio-room-design.md):
/// seeds the well-known publisher device/session, connects to this API's own
/// /ws/signaling as a headless client, and streams the looping MP3 playlist to
/// every viewer who joins. Disabled by default; a false PublicRoom:Enabled means
/// this does nothing at all (same pattern as DataRetentionService).
/// </summary>
public sealed class PublicRoomPublisherService(
    IServiceScopeFactory scopeFactory,
    IOptions<PublicRoomOptions> options,
    PublicRoomSeeder seeder,
    DeviceCredentialService credentials,
    IServer server,
    TimeProvider time,
    ILogger<PublicRoomPublisherService> logger) : BackgroundService
{
    private readonly Dictionary<Guid, VirtualPublisherPeerConnection> peersByParticipantId = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
        {
            logger.LogInformation("Public radio room is disabled; PublicRoomPublisherService is a no-op");
            return;
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await seeder.EnsureSeededAsync(db, time, stoppingToken);
        var (token, _) = await seeder.IssuePublisherTokenAsync(db, credentials, time, stoppingToken);

        var baseUrl = ResolveLoopbackBaseUrl();
        await using var signaling = new PublicRoomSignalingClient(baseUrl, token);
        signaling.MessageReceived += (from, type, payload, ct) => HandleSignalingMessageAsync(signaling, from, type, payload, ct);

        try
        {
            await signaling.ConnectAsPublisherAsync(PublicRoomSeeder.PublicSessionId, stoppingToken);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Public radio room could not connect to its own signaling endpoint");
            return;
        }

        var decoder = new NLayerMp3Decoder();
        var trackLogger = scope.ServiceProvider.GetRequiredService<ILogger<Mp3TrackSource>>();
        var tracks = new Mp3TrackSource(options.Value.TracksPath, decoder, trackLogger);

        var receiveLoop = signaling.RunReceiveLoopAsync(stoppingToken);
        try
        {
            foreach (var frame in tracks.ReadForever(stoppingToken))
            {
                if (stoppingToken.IsCancellationRequested) break;
                var bytes = new byte[frame.Samples.Length * 2];
                Buffer.BlockCopy(frame.Samples, 0, bytes, 0, bytes.Length);
                // frame.SampleRate/Channels come from the MP3's real native format
                // (NLayerMp3Decoder reports it), not a hardcoded 48kHz/stereo assumption —
                // OpusFrameAccumulator (inside VirtualPublisherPeerConnection) resamples
                // and up/down-mixes from whatever this actually is.
                var audioFrame = new WebRtcAudioFrame(bytes, frame.SampleRate, frame.Channels);
                foreach (var peer in peersByParticipantId.Values.ToList())
                {
                    try
                    {
                        await peer.SendAudioFrameAsync(audioFrame, stoppingToken);
                    }
                    catch (Exception exception)
                    {
                        logger.LogWarning(exception,
                            "Failed to send an audio frame to public room viewer {ViewerParticipantId}",
                            peer.ViewerParticipantId);
                    }
                }
            }
        }
        finally
        {
            foreach (var peer in peersByParticipantId.Values) await peer.DisposeAsync();
            peersByParticipantId.Clear();
            await receiveLoop;
        }
    }

    private async Task HandleSignalingMessageAsync(
        PublicRoomSignalingClient signaling, Guid fromParticipantId, string type,
        System.Text.Json.JsonElement payload, CancellationToken ct)
    {
        try
        {
            switch (type)
            {
                case "viewer.ready":
                    await AddViewerAsync(signaling, fromParticipantId, ct);
                    break;
                case "webrtc.answer":
                    if (peersByParticipantId.TryGetValue(fromParticipantId, out var answerPeer))
                    {
                        var sdp = payload.GetProperty("sdp").GetString()!;
                        await answerPeer.ApplyAnswerAsync(new WebRtcSessionDescription("answer", sdp), ct);
                    }
                    break;
                case "webrtc.ice_candidate":
                    if (peersByParticipantId.TryGetValue(fromParticipantId, out var icePeer))
                    {
                        var candidate = payload.GetProperty("candidate").GetString()!;
                        var sdpMid = payload.TryGetProperty("sdpMid", out var m) ? m.GetString() : null;
                        var sdpMLineIndex = payload.TryGetProperty("sdpMLineIndex", out var idx) ? idx.GetInt32() : (int?)null;
                        await icePeer.AddRemoteIceCandidateAsync(
                            new WebRtcIceCandidate(candidate, sdpMid, sdpMLineIndex), ct);
                    }
                    break;
            }
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Error handling {MessageType} from viewer {ViewerParticipantId}",
                type, fromParticipantId);
        }
    }

    private async Task AddViewerAsync(PublicRoomSignalingClient signaling, Guid viewerParticipantId, CancellationToken ct)
    {
        if (peersByParticipantId.ContainsKey(viewerParticipantId)) return;
        if (peersByParticipantId.Count >= options.Value.EffectiveMaxViewers) return; // SessionEndpoints already enforced this at join time

        var connection = new RTCPeerConnection();
        var peer = new VirtualPublisherPeerConnection(viewerParticipantId.ToString(), connection);
        peer.LocalIceCandidateReady += async (candidate, candidateCt) =>
            await signaling.SendAsync(viewerParticipantId, "webrtc.ice_candidate",
                new { candidate = candidate.Candidate, sdpMid = candidate.SdpMid, sdpMLineIndex = candidate.SdpMLineIndex },
                candidateCt);

        peersByParticipantId[viewerParticipantId] = peer;
        var offer = await peer.CreateOfferAsync(ct);
        await signaling.SendAsync(viewerParticipantId, "webrtc.offer", new { sdp = offer.Sdp }, ct);
    }

    private Uri ResolveLoopbackBaseUrl()
    {
        var address = server.Features.Get<IServerAddressesFeature>()?.Addresses.FirstOrDefault();
        return address is not null ? new Uri(address) : new Uri("http://localhost:8080");
    }
}
```

- [ ] **Step 4: Register the hosted service in `Program.cs`**

Add near the other `AddSingleton<IHostedService>(...)` registrations:

```csharp
builder.Services.AddSingleton<PublicRoomPublisherService>();
builder.Services.AddSingleton<IHostedService>(services => services.GetRequiredService<PublicRoomPublisherService>());
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/SonicRelay.Api.IntegrationTests --filter PublicRoomPublisherServiceTests`
Expected: PASS (2 tests).

- [ ] **Step 6: Run the full test suite to check for regressions**

Run: `dotnet test`
Expected: All tests pass, including the pre-existing ones untouched by this plan.

- [ ] **Step 7: Commit**

```bash
git add services/SonicRelay.Api/Services/PublicRoomPublisherService.cs services/SonicRelay.Api/Program.cs tests/SonicRelay.Api.IntegrationTests/PublicRoomPublisherServiceTests.cs
git commit -m "Add PublicRoomPublisherService BackgroundService"
```

---

### Task 9: docker-compose — env vars + volume

**Files:**
- Modify: `deploy/docker-compose.prod.yml`

**Interfaces:**
- Produces: the `api` service gains `PUBLICROOM__ENABLED`, `PUBLICROOM__TRACKSPATH`, `PUBLICROOM__MAXVIEWERS` environment variables (sourced from `.env`, matching every other setting in this file) and a read-only bind mount from a VPS host path to `/app/tracks`.

- [ ] **Step 1: Edit `deploy/docker-compose.prod.yml`**

```yaml
services:
  api:
    image: ${IMAGE:-ghcr.io/vitorhugo-java/sonicrelay-api:latest}
    container_name: sonicrelay-api
    restart: unless-stopped
    env_file:
      - .env
    environment:
      PUBLICROOM__ENABLED: "${PUBLICROOM_ENABLED:-false}"
      PUBLICROOM__TRACKSPATH: "/app/tracks"
      PUBLICROOM__MAXVIEWERS: "${PUBLICROOM_MAXVIEWERS:-20}"
    expose:
      - "8080"
    volumes:
      - ${PUBLICROOM_TRACKS_HOST_PATH:-./tracks}:/app/tracks:ro
    networks:
      - infra_network

networks:
  infra_network:
    external: true
    name: infra_network
```

- [ ] **Step 2: Verify the compose file parses**

Run: `docker compose -f deploy/docker-compose.prod.yml config`
Expected: Prints the resolved config with no errors (missing `.env` values fall back to the defaults above, so this succeeds even without a real `.env` file present).

- [ ] **Step 3: Document the new `.env` keys**

If `deploy/` has an `.env.example` or similar template file, add:

```bash
PUBLICROOM_ENABLED=true
PUBLICROOM_MAXVIEWERS=20
PUBLICROOM_TRACKS_HOST_PATH=/srv/sonicrelay/tracks
```

If no such template exists in the repo, skip this step — there is nothing to update.

- [ ] **Step 4: Commit**

```bash
git add deploy/docker-compose.prod.yml
git commit -m "Wire the public radio room into docker-compose (env vars + volume)"
```

---

## Plan 2 Self-Review Notes

- **Spec coverage:** feature flag with zero-cost-when-disabled (Tasks 1, 5 endpoint test, 8 first test), fixed device/session seeding (Task 3), auto-pairing replacing the QR-code flow only for the public room (Task 5), headless signaling matching the existing wire protocol (Task 6), MaxViewers=20 reusing the existing `SessionEndpoints` enforcement rather than re-implementing it (noted in Task 8's `AddViewerAsync`), docker-compose env vars + host-path volume (Task 9).
- **No placeholders:** every task has complete, real code; the one deliberately untested class (`PublicRoomSignalingClient`, Task 6) states why (covered indirectly through the `BackgroundService` integration tests) rather than being silently skipped.
- **Type consistency:** `PublicRoomOptions.EffectiveMaxViewers` (Task 1) is what `PublicRoomEndpoints` (Task 5) and `PublicRoomPublisherService.AddViewerAsync` (Task 8) both read; `PublicRoomSeeder.VirtualPublisherDeviceId`/`PublicSessionId` (Task 3) are the exact constants every later task references — no ad hoc GUIDs re-declared elsewhere.
- **Correction carried from the spec:** the "not_paired" gap found while grounding this plan (see the spec's "Correção pós-design" note) is fully addressed by Task 5 — no changes to `SessionEndpoints.cs` were needed.

## Manual Verification (not automatable in this repo)

Per the spec's Testing section, after both plans are implemented:

1. `docker compose -f deploy/docker-compose.prod.yml up` locally with `PUBLICROOM_ENABLED=true`, `PUBLICROOM_TRACKS_HOST_PATH` pointing at a folder with a few real `.mp3` files.
2. Open the Flutter app, call `GET /api/public-room`, `joinById` the returned session, confirm continuous audio and that it keeps looping past the last track.
3. Join 21 devices (or fake 21 `SessionParticipant` rows) and confirm the 21st gets `409`.

## Execution Handoff

**Plan 1 and Plan 2 complete and saved to `docs/superpowers/plans/2026-08-19-public-radio-room-1-audio-pipeline.md` and `docs/superpowers/plans/2026-08-19-public-radio-room-2-orchestration.md`.** Two execution options:

**1. Subagent-Driven (recommended)** - I dispatch a fresh subagent per task, review between tasks, fast iteration

**2. Inline Execution** - Execute tasks in this session using executing-plans, batch execution with checkpoints

**Which approach?**
