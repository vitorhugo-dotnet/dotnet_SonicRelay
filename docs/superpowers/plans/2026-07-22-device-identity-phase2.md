# Device Identity Auth (Phase 2) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Migrate session ownership, WebSocket signaling, and TURN credential issuance from ASP.NET Core Identity (`ApplicationUser`) and the old owner-scoped `Device` entity to the Phase 1 `DeviceIdentity` model, making `DeviceBearer` the sole authentication path for sessions.

**Architecture:** `StreamSession`/`SessionParticipant` drop their `OwnerUserId`/`UserId` fields in favor of `DeviceIdentity`-backed `SourceDeviceId`/`DeviceId`. Session, signaling, and TURN endpoints resolve the caller via the existing `DeviceIdentityEndpoints.RequireDeviceAsync` helper and a new set of `DeviceBearer`-pinned authorization policies, reusing Phase 1's `DeviceScopeAuthorizationHandler` for live revocation checking everywhere — including a new scope-less mode of that handler for routes that need "any active device" rather than a specific capability.

**Tech Stack:** .NET 10, ASP.NET Core Minimal APIs, EF Core (Npgsql + InMemory for tests), JWT bearer auth (`Microsoft.AspNetCore.Authentication.JwtBearer`), xUnit integration tests.

## Global Constraints

- `DeviceIdentity:Enabled` continues to gate only the bootstrap/token/rotate-credential/revoke/pairing HTTP surface and their `device:*`/`pairing:*` policies. It must **not** gate sessions, signaling, or TURN — those require `DeviceBearer` unconditionally after this plan.
- The old `Device` entity, `DeviceEndpoints.cs`, and `DeviceAccess.cs` (owner-scoped Device CRUD) are **not** modified, and the `CanRegisterDevice` policy that gates `POST /api/devices` in that file must **not** be removed — it is unrelated to this migration.
- No EF-enforced foreign keys are added — match the existing bare-Guid-plus-app-check convention already used by `StreamSession`, `SessionParticipant`, and `Device`.
- Every device-facing route added or modified in this plan must reference a named authorization policy that pins the `DeviceBearer` scheme (`AddAuthenticationSchemes("DeviceBearer")`); none may rely on bare `RequireAuthorization()`, which authenticates against the app's default scheme (`Identity.Bearer`) instead.
- Rate-limit policies for authenticated `DeviceBearer` routes must use `IpLimit`, not `UserLimit` — `UserLimit` keys off `ClaimTypes.NameIdentifier`, which `DeviceBearer` tokens never populate (`MapInboundClaims = false`), so it silently degrades to an IP fallback anyway. This is the same issue Phase 1's final review found and fixed for the pairing endpoints; apply the same fix proactively here rather than reintroducing it.
- This is a breaking, non-additive schema migration with no data-preservation requirement for existing session rows — matches the issue's explicit MVP breaking-change preference (already confirmed with the user for this plan).

Full context: `docs/superpowers/specs/2026-07-22-device-identity-phase2-design.md`.

---

### Task 1: Shared device-identity test fixture

**Files:**
- Create: `tests/SonicRelay.Api.IntegrationTests/DeviceIdentityTestHelper.cs`
- Test: `tests/SonicRelay.Api.IntegrationTests/DeviceIdentityTestHelperTests.cs`

**Interfaces:**
- Consumes: existing Phase 1 endpoints `POST /api/devices/bootstrap`, `POST /api/devices/token`, `POST /api/devices/revoke`, and contracts `BootstrapDeviceRequest(string? Name, string? DeviceType, string? Platform)`, `BootstrapDeviceResponse(Guid DeviceId, string CredentialSecret, int CredentialVersion)`, `DeviceTokenRequest(Guid DeviceId, string CredentialSecret)`, `DeviceTokenResponse(string AccessToken, DateTimeOffset ExpiresAt, IReadOnlyList<string> Scopes)` (`services/SonicRelay.Api/Contracts/DeviceIdentityContracts.cs`).
- Produces: `DeviceIdentityTestHelper.BootstrapAndAuthorizeAsync(HttpClient client, string deviceType, string platform, string name = "Test device")` returning `Task<DeviceIdentitySession>`; `DeviceIdentitySession(Guid DeviceId, string AccessToken, HttpClient Client)`. Tasks 3, 4, and 5 depend on this exact signature.

This task does not depend on any later task's domain changes — it only calls Phase 1 endpoints that already exist and are unaffected by this plan, so it is independently buildable and testable right now.

- [ ] **Step 1: Write the fixture**

```csharp
// tests/SonicRelay.Api.IntegrationTests/DeviceIdentityTestHelper.cs
using System.Net.Http.Headers;
using System.Net.Http.Json;
using SonicRelay.Api.Contracts;

namespace SonicRelay.Api.IntegrationTests;

public sealed record DeviceIdentitySession(Guid DeviceId, string AccessToken, HttpClient Client);

public static class DeviceIdentityTestHelper
{
    public static async Task<DeviceIdentitySession> BootstrapAndAuthorizeAsync(
        HttpClient client, string deviceType, string platform, string name = "Test device")
    {
        var bootstrapResponse = await client.PostAsJsonAsync("/api/devices/bootstrap",
            new BootstrapDeviceRequest(name, deviceType, platform));
        bootstrapResponse.EnsureSuccessStatusCode();
        var bootstrap = await bootstrapResponse.Content.ReadFromJsonAsync<BootstrapDeviceResponse>();

        var tokenResponse = await client.PostAsJsonAsync("/api/devices/token",
            new DeviceTokenRequest(bootstrap!.DeviceId, bootstrap.CredentialSecret));
        tokenResponse.EnsureSuccessStatusCode();
        var token = await tokenResponse.Content.ReadFromJsonAsync<DeviceTokenResponse>();

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token!.AccessToken);
        return new DeviceIdentitySession(bootstrap.DeviceId, token.AccessToken, client);
    }
}
```

- [ ] **Step 2: Write the test**

```csharp
// tests/SonicRelay.Api.IntegrationTests/DeviceIdentityTestHelperTests.cs
using System.Net;
using SonicRelay.Domain.Devices;
using Xunit;

namespace SonicRelay.Api.IntegrationTests;

public sealed class DeviceIdentityTestHelperTests : IClassFixture<SonicRelayApiFactory>
{
    private readonly SonicRelayApiFactory _factory;

    public DeviceIdentityTestHelperTests(SonicRelayApiFactory factory) => _factory = factory;

    [Fact]
    public async Task BootstrapAndAuthorizeAsync_Returns_A_Usable_DeviceBearer_Token()
    {
        var client = _factory.CreateClient();

        var session = await DeviceIdentityTestHelper.BootstrapAndAuthorizeAsync(
            client, DeviceTypes.WindowsPublisher, DevicePlatforms.Windows);

        Assert.NotEqual(Guid.Empty, session.DeviceId);
        Assert.False(string.IsNullOrWhiteSpace(session.AccessToken));

        // device:manage is granted to every device type, so a successful call here proves the
        // returned access token is a genuinely valid, scoped DeviceBearer credential — not just
        // a non-empty string.
        var revokeResponse = await client.PostAsync("/api/devices/revoke", null);
        Assert.Equal(HttpStatusCode.NoContent, revokeResponse.StatusCode);
    }
}
```

- [ ] **Step 3: Run the test to verify it passes**

Run: `dotnet test tests/SonicRelay.Api.IntegrationTests --filter DeviceIdentityTestHelperTests`
Expected: `Passed! - Failed: 0, Passed: 1, Skipped: 0, Total: 1`

- [ ] **Step 4: Commit**

```bash
git add tests/SonicRelay.Api.IntegrationTests/DeviceIdentityTestHelper.cs tests/SonicRelay.Api.IntegrationTests/DeviceIdentityTestHelperTests.cs
git commit -m "Add shared device-identity bootstrap-and-token test fixture"
```

---

### Task 2: Domain model, persistence, and auth infrastructure migration

**Files:**
- Modify: `src/SonicRelay.Domain/Sessions/StreamSession.cs`
- Modify: `src/SonicRelay.Application/Abstractions/IConnectionRegistry.cs`
- Modify: `src/SonicRelay.Infrastructure/Persistence/AppDbContext.cs`
- Create: EF migration under `src/SonicRelay.Infrastructure/Persistence/Migrations/`
- Modify: `services/SonicRelay.Api/Services/DeviceCredentialService.cs`
- Modify: `services/SonicRelay.Api/Authorization/DeviceScopeRequirement.cs`
- Modify: `services/SonicRelay.Api/Authorization/DeviceScopeAuthorizationHandler.cs`
- Modify: `services/SonicRelay.Api/Program.cs`
- Modify: `services/SonicRelay.Api/Endpoints/SessionEndpoints.cs`
- Modify: `services/SonicRelay.Api/Endpoints/SignalingWebSocketEndpoint.cs`
- Modify: `services/SonicRelay.Api/Services/TurnCredentialService.cs`
- Modify: `services/SonicRelay.Api/Endpoints/WebRtcEndpoints.cs`

**Interfaces:**
- Consumes: `DeviceIdentityEndpoints.RequireDeviceAsync(ClaimsPrincipal, AppDbContext, CancellationToken)` (`services/SonicRelay.Api/Endpoints/DeviceIdentityEndpoints.cs`, internal, same assembly — unchanged from Phase 1); `DeviceIdentity` (`src/SonicRelay.Domain/DeviceIdentities/DeviceIdentity.cs`, unchanged).
- Produces: `StreamSession { Id, SourceDeviceId, Status, MaxViewers, CodeExpiresAt, StartedAt, EndedAt, CreatedAt }` (no `OwnerUserId`); `SessionParticipant { Id, SessionId, DeviceId, Role, ConnectionId, Status, JoinedAt, LeftAt }` (no `UserId`); `ConnectionDescriptor(string ConnectionId, Guid SessionId, Guid ParticipantId, Guid DeviceId, string Role, DateTimeOffset ConnectedAt, Func<...> SendAsync)` (no `UserId`); authorization policies `DeviceAuthenticated`, `session:create`, `session:join`, `session:end`, `signaling:connect`, `turn:credentials` (all `DeviceBearer`-pinned, registered unconditionally); `DeviceScopeRequirement(string? scope = null)`. Tasks 3, 4, and 5 depend on all of these exact shapes and policy names.

**Why this task is unusually large:** C# requires a whole project to compile together. `StreamSession`/`SessionParticipant`/`ConnectionDescriptor` are referenced by `SessionEndpoints.cs`, `SignalingWebSocketEndpoint.cs`, and `WebRtcEndpoints.cs` today via the fields this task removes, so all three endpoint files must be rewritten in the same task as the domain change for `services/SonicRelay.Api` to compile at all — there is no smaller increment that leaves that project buildable. The integration test project references the same removed fields in four files; fixing those is Task 3. **This task's own verification is therefore project-scoped `dotnet build`, not `dotnet test`** — the test project will not compile again until Task 3 is done. This is expected; do not treat it as a defect of this task.

- [ ] **Step 1: Update the domain model**

```csharp
// src/SonicRelay.Domain/Sessions/StreamSession.cs
namespace SonicRelay.Domain.Sessions;

public sealed class StreamSession
{
    public Guid Id { get; set; }
    public Guid SourceDeviceId { get; set; }
    public string Status { get; set; } = SessionStatuses.Waiting;
    public int MaxViewers { get; set; }
    public DateTimeOffset CodeExpiresAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class SessionParticipant
{
    public Guid Id { get; set; }
    public Guid SessionId { get; set; }
    public Guid DeviceId { get; set; }
    public string Role { get; set; } = ParticipantRoles.Viewer;
    public string? ConnectionId { get; set; }
    public string Status { get; set; } = ParticipantStatuses.Connected;
    public DateTimeOffset JoinedAt { get; set; }
    public DateTimeOffset? LeftAt { get; set; }
}

public static class SessionStatuses
{
    public const string Waiting = "waiting";
    public const string Active = "active";
    public const string Ended = "ended";
    public const string Expired = "expired";
}

public static class ParticipantRoles
{
    public const string Publisher = "publisher";
    public const string Viewer = "viewer";
}

public static class ParticipantStatuses
{
    public const string Connected = "connected";
    public const string Disconnected = "disconnected";

    /// <summary>
    /// The participant's socket dropped but the reconnect grace period has not elapsed yet.
    /// The participant is still considered part of the session and can resume without
    /// creating a duplicate row.
    /// </summary>
    public const string Reconnecting = "reconnecting";
}
```

- [ ] **Step 2: Drop `UserId` from `ConnectionDescriptor`**

```csharp
// src/SonicRelay.Application/Abstractions/IConnectionRegistry.cs
namespace SonicRelay.Application.Abstractions;

public sealed record ConnectionDescriptor(
    string ConnectionId,
    Guid SessionId,
    Guid ParticipantId,
    Guid DeviceId,
    string Role,
    DateTimeOffset ConnectedAt,
    Func<ReadOnlyMemory<byte>, CancellationToken, Task> SendAsync);

public interface IConnectionRegistry
{
    Task RegisterAsync(ConnectionDescriptor connection, CancellationToken ct);
    Task UnregisterAsync(string connectionId, CancellationToken ct);
    Task<ConnectionDescriptor?> FindByParticipantAsync(Guid participantId, CancellationToken ct);
    Task<IReadOnlyList<ConnectionDescriptor>> ListBySessionAsync(Guid sessionId, CancellationToken ct);
    Task<bool> SendToParticipantAsync(Guid sessionId, Guid participantId, ReadOnlyMemory<byte> message, CancellationToken ct);
}
```

- [ ] **Step 3: Build the domain and application projects to verify Steps 1-2 compile**

Run: `dotnet build src/SonicRelay.Domain/SonicRelay.Domain.csproj && dotnet build src/SonicRelay.Application/SonicRelay.Application.csproj`
Expected: `Build succeeded.` for both (0 errors — `SonicRelay.Application` doesn't reference the fields removed from `StreamSession`/`SessionParticipant`, only `ConnectionDescriptor`, which this step already updated).

- [ ] **Step 4: Update `AppDbContext`'s indexes for the two changed entities**

In `src/SonicRelay.Infrastructure/Persistence/AppDbContext.cs`, replace the `StreamSession` and `SessionParticipant` `OnModelCreating` blocks:

```csharp
        modelBuilder.Entity<StreamSession>(entity =>
        {
            entity.ToTable("stream_sessions");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Status).HasMaxLength(32).IsRequired();
            entity.HasIndex(x => new { x.SourceDeviceId, x.Status }).HasDatabaseName("ix_stream_sessions_source_device_status");
        });

        modelBuilder.Entity<SessionParticipant>(entity =>
        {
            entity.ToTable("session_participants");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Role).HasMaxLength(32).IsRequired();
            entity.Property(x => x.Status).HasMaxLength(32).IsRequired();
            entity.HasIndex(x => new { x.SessionId, x.Role }).HasDatabaseName("ix_session_participants_session_role");
        });
```

(This removes `ix_stream_sessions_owner_status`; `ix_session_participants_session_role` is unchanged since it never referenced `UserId`. All other entity configuration blocks in this file — `ApplicationUser`, `Device`, `SignalingEvent`, `DeviceIdentity`, `PairingChallenge`, `DevicePairing` — are untouched.)

- [ ] **Step 5: Generate the EF migration**

Run:
```bash
dotnet ef migrations add MigrateSessionOwnershipToDeviceIdentity \
  --project src/SonicRelay.Infrastructure/SonicRelay.Infrastructure.csproj \
  --startup-project services/SonicRelay.Api/SonicRelay.Api.csproj
```
Expected: a new migration file under `src/SonicRelay.Infrastructure/Persistence/Migrations/` whose `Up` drops the `owner_user_id` column and `ix_stream_sessions_owner_status` index from `stream_sessions`, and drops the `user_id` column from `session_participants`; `Down` re-adds both as nullable `uuid` columns (data is not preserved across a `Down`, which matches this being a one-way breaking change — do not hand-edit `Down` to attempt data preservation).

- [ ] **Step 6: Build the infrastructure project to verify the migration compiles**

Run: `dotnet build src/SonicRelay.Infrastructure/SonicRelay.Infrastructure.csproj`
Expected: `Build succeeded.`

- [ ] **Step 7: Add the new scopes**

In `services/SonicRelay.Api/Services/DeviceCredentialService.cs`, replace `ScopesFor`:

```csharp
    public static IReadOnlyList<string> ScopesFor(string deviceType) => deviceType switch
    {
        DeviceTypes.WindowsPublisher =>
        [
            "device:read", "device:manage", "pairing:create", "pairing:revoke",
            "session:create", "session:end", "signaling:connect", "turn:credentials"
        ],
        DeviceTypes.FlutterViewer =>
        [
            "device:read", "device:manage", "pairing:complete", "pairing:revoke",
            "session:join", "signaling:connect", "turn:credentials"
        ],
        _ => []
    };
```

- [ ] **Step 8: Make `DeviceScopeRequirement`'s scope optional**

```csharp
// services/SonicRelay.Api/Authorization/DeviceScopeRequirement.cs
using Microsoft.AspNetCore.Authorization;

namespace SonicRelay.Api.Authorization;

public sealed class DeviceScopeRequirement(string? scope = null) : IAuthorizationRequirement
{
    public string? Scope { get; } = scope;
}
```

- [ ] **Step 9: Make `DeviceScopeAuthorizationHandler` skip the scope check when `Scope` is null, but always run the live status/credential-version check**

```csharp
// services/SonicRelay.Api/Authorization/DeviceScopeAuthorizationHandler.cs
using System.IdentityModel.Tokens.Jwt;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using SonicRelay.Domain.DeviceIdentities;
using SonicRelay.Infrastructure.Persistence;

namespace SonicRelay.Api.Authorization;

public sealed class DeviceScopeAuthorizationHandler(AppDbContext db) : AuthorizationHandler<DeviceScopeRequirement>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context, DeviceScopeRequirement requirement)
    {
        if (requirement.Scope is not null)
        {
            var scopes = context.User.FindFirstValue("scope")?.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                ?? [];
            if (!scopes.Contains(requirement.Scope)) return;
        }

        if (!Guid.TryParse(context.User.FindFirstValue(JwtRegisteredClaimNames.Sub), out var deviceId)) return;
        if (!int.TryParse(context.User.FindFirstValue("cv"), out var tokenCredentialVersion)) return;

        var device = await db.DeviceIdentities.AsNoTracking()
            .Where(x => x.Id == deviceId)
            .Select(x => new { x.Status, x.CredentialVersion })
            .SingleOrDefaultAsync();

        if (device is null) return;
        if (device.Status != DeviceIdentityStatuses.Active) return;
        if (device.CredentialVersion != tokenCredentialVersion) return;

        context.Succeed(requirement);
    }
}
```

(`context.User.FindFirstValue` is an extension method on `ClaimsPrincipal` from `System.Security.Claims`, already implicitly available via the ASP.NET Core authorization types in scope — no new `using` needed beyond what already existed.)

- [ ] **Step 10: Update `Program.cs` — unconditional `DeviceBearer` scheme, new policies, rate-limit key fix**

In `services/SonicRelay.Api/Program.cs`, unwrap the `AddJwtBearer` registration from its `if (deviceIdentityEnabled)` block (the block becomes empty and is removed — `deviceIdentityEnabled` is still used later, so the variable declaration at the top of the file stays):

```csharp
builder.Services.Configure<DeviceIdentityOptions>(builder.Configuration.GetSection("DeviceIdentity"));
builder.Services.AddSingleton<DeviceCredentialService>();
builder.Services.AddSingleton<PairingChallengeService>();
builder.Services.AddScoped<IAuthorizationHandler, DeviceScopeAuthorizationHandler>();

builder.Services.AddAuthentication().AddJwtBearer("DeviceBearer", jwtOptions =>
{
    // Keep claim types as issued (e.g. "sub", not ClaimTypes.NameIdentifier) so
    // downstream code reading JwtRegisteredClaimNames.Sub/"cv"/"scope" matches
    // what DeviceCredentialService.IssueAccessToken actually put in the token.
    jwtOptions.MapInboundClaims = false;
    var deviceOptions = builder.Configuration.GetSection("DeviceIdentity").Get<DeviceIdentityOptions>()
        ?? new DeviceIdentityOptions();
    jwtOptions.TokenValidationParameters = new TokenValidationParameters
    {
        ValidIssuer = deviceOptions.Issuer,
        ValidAudience = deviceOptions.Audience,
        IssuerSigningKey = new SymmetricSecurityKey(
            Encoding.UTF8.GetBytes(deviceOptions.TokenSigningKey ?? string.Empty)),
        ValidateIssuer = true,
        ValidateAudience = true,
        ValidateLifetime = true,
        ValidateIssuerSigningKey = true,
        ClockSkew = TimeSpan.FromSeconds(30)
    };
});
```

Update the rate-limit policies (`create-session`, `join-session`, `rotate-code` move from `UserLimit` to `IpLimit`; `device-bootstrap`/`device-token`/`pairing-create`/`pairing-complete` are unchanged):

```csharp
    options.AddPolicy("login", context => IpLimit(context, "RateLimits:Login", 5));
    options.AddPolicy("refresh", context => IpLimit(context, "RateLimits:Refresh", 5));
    options.AddPolicy("create-session", context => IpLimit(context, "RateLimits:CreateSession", 10));
    options.AddPolicy("join-session", context => IpLimit(context, "RateLimits:JoinSession", 10));
    options.AddPolicy("rotate-code", context => IpLimit(context, "RateLimits:RotateCode", 5));
    options.AddPolicy("device-bootstrap", context => IpLimit(context, "RateLimits:DeviceBootstrap", 10));
    options.AddPolicy("device-token", context => IpLimit(context, "RateLimits:DeviceToken", 10));
    options.AddPolicy("pairing-create", context => IpLimit(context, "RateLimits:PairingCreate", 10));
    options.AddPolicy("pairing-complete", context => IpLimit(context, "RateLimits:PairingComplete", 10));
```

Replace the `AddAuthorization` block:

```csharp
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("AuthenticatedUser", policy => policy.RequireAuthenticatedUser());
    options.AddPolicy("CanRegisterDevice", policy => policy.RequireAuthenticatedUser());
    options.AddPolicy("AdminOnly", policy => policy.RequireRole("admin"));

    options.AddPolicy("DeviceAuthenticated", policy =>
    {
        policy.AddAuthenticationSchemes("DeviceBearer");
        policy.RequireAuthenticatedUser();
        policy.Requirements.Add(new DeviceScopeRequirement());
    });

    foreach (var scope in new[]
        { "session:create", "session:join", "session:end", "signaling:connect", "turn:credentials" })
    {
        options.AddPolicy(scope, policy =>
        {
            policy.AddAuthenticationSchemes("DeviceBearer");
            policy.RequireAuthenticatedUser();
            policy.Requirements.Add(new DeviceScopeRequirement(scope));
        });
    }

    if (deviceIdentityEnabled)
    {
        foreach (var scope in new[] { "device:read", "device:manage", "pairing:create", "pairing:complete", "pairing:revoke" })
        {
            options.AddPolicy(scope, policy =>
            {
                policy.AddAuthenticationSchemes("DeviceBearer");
                policy.RequireAuthenticatedUser();
                policy.Requirements.Add(new DeviceScopeRequirement(scope));
            });
        }
    }
});
```

(`CanCreateSession`, `CanJoinSession`, `CanPublishSession`, `CanViewSession` are deleted — `CanRegisterDevice` and `AuthenticatedUser` are kept: `CanRegisterDevice` still gates `POST /api/devices` in the untouched `DeviceEndpoints.cs:15`.)

The middleware pipeline (`UseWebSockets(); UseAuthentication(); UseRateLimiter(); UseAuthorization();`) and the endpoint-mapping section (`MapDeviceEndpoints()` unconditional; `MapDeviceIdentityEndpoints()`/`MapPairingEndpoints()` inside `if (deviceIdentityEnabled)`; `MapSessionEndpoints()`, `MapWebRtcEndpoints()`, `MapSignalingWebSocketEndpoint()` unconditional) are unchanged — do not touch them.

- [ ] **Step 11: Rewrite `SessionEndpoints.cs`**

```csharp
// services/SonicRelay.Api/Endpoints/SessionEndpoints.cs
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using SonicRelay.Application.Abstractions;
using SonicRelay.Domain.Sessions;
using SonicRelay.Infrastructure.Persistence;

namespace SonicRelay.Api.Endpoints;

public static class SessionEndpoints
{
    public static IEndpointRouteBuilder MapSessionEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/sessions").WithTags("Sessions");
        group.MapPost("/", CreateAsync).RequireAuthorization("session:create").RequireRateLimiting("create-session");
        group.MapGet("/active", GetActiveAsync).RequireAuthorization("DeviceAuthenticated");
        group.MapGet("/{sessionId:guid}", GetAsync).RequireAuthorization("DeviceAuthenticated");
        group.MapPost("/{sessionId:guid}/end", EndAsync).RequireAuthorization("session:end");
        group.MapPost("/{sessionId:guid}/rotate-code", RotateCodeAsync).RequireAuthorization("session:end").RequireRateLimiting("rotate-code");
        group.MapPost("/join", JoinAsync).RequireAuthorization("session:join").RequireRateLimiting("join-session");
        return app;
    }

    private static async Task<IResult> CreateAsync(CreateSessionRequest request,
        ClaimsPrincipal principal, AppDbContext db, ISessionCodeStore codeStore, IConfiguration configuration,
        ILoggerFactory loggerFactory, CancellationToken ct)
    {
        var device = await DeviceIdentityEndpoints.RequireDeviceAsync(principal, db, ct);
        if (device is null) return Results.Unauthorized();
        var maxViewers = request.MaxViewers ?? configuration.GetValue("Sessions:MaxViewersPerSession", 3);
        if (maxViewers < 1) return Results.BadRequest(new { error = "MaxViewers must be at least one." });

        var now = DateTimeOffset.UtcNow;
        var ttl = CodeTtl(configuration);
        var session = new StreamSession
        {
            Id = Guid.NewGuid(),
            SourceDeviceId = device.Id,
            MaxViewers = maxViewers,
            CodeExpiresAt = now.Add(ttl),
            CreatedAt = now
        };
        db.StreamSessions.Add(session);
        db.SessionParticipants.Add(new SessionParticipant
        {
            Id = Guid.NewGuid(),
            SessionId = session.Id,
            DeviceId = device.Id,
            Role = ParticipantRoles.Publisher,
            Status = ParticipantStatuses.Connected,
            JoinedAt = now
        });
        await db.SaveChangesAsync(ct);

        var code = GenerateCode();
        await codeStore.StoreAsync(HashCode(code, configuration), session.Id, ttl, ct);
        loggerFactory.CreateLogger("SonicRelay.Sessions").LogInformation(
            "Created session {SessionId} from device {DeviceId}", session.Id, device.Id);
        return Results.Created($"/api/sessions/{session.Id}", ToResponse(session, code));
    }

    private static async Task<IResult> GetActiveAsync(ClaimsPrincipal principal, AppDbContext db, CancellationToken ct)
    {
        var device = await DeviceIdentityEndpoints.RequireDeviceAsync(principal, db, ct);
        if (device is null) return Results.Unauthorized();
        var sessions = await db.StreamSessions
            .Where(x => (x.Status == SessionStatuses.Waiting || x.Status == SessionStatuses.Active)
                && (x.SourceDeviceId == device.Id || db.SessionParticipants.Any(p => p.SessionId == x.Id && p.DeviceId == device.Id)))
            .OrderByDescending(x => x.CreatedAt)
            .Select(x => new
            {
                x.Id,
                x.SourceDeviceId,
                x.Status,
                x.MaxViewers,
                x.CodeExpiresAt,
                x.StartedAt,
                x.EndedAt,
                x.CreatedAt,
                ViewerCount = db.SessionParticipants.Count(p => p.SessionId == x.Id && p.Role == ParticipantRoles.Viewer
                    && p.Status == ParticipantStatuses.Connected)
            }).ToListAsync(ct);
        return Results.Ok(sessions);
    }

    private static async Task<IResult> GetAsync(Guid sessionId, ClaimsPrincipal principal, AppDbContext db, CancellationToken ct)
    {
        var device = await DeviceIdentityEndpoints.RequireDeviceAsync(principal, db, ct);
        if (device is null) return Results.Unauthorized();
        var session = await db.StreamSessions.SingleOrDefaultAsync(x => x.Id == sessionId, ct);
        if (session is null) return Results.NotFound();
        var canAccess = session.SourceDeviceId == device.Id
            || await db.SessionParticipants.AnyAsync(x => x.SessionId == sessionId && x.DeviceId == device.Id, ct);
        return canAccess ? Results.Ok(ToResponse(session)) : Results.NotFound();
    }

    private static async Task<IResult> EndAsync(Guid sessionId, ClaimsPrincipal principal, AppDbContext db,
        ISessionCodeStore codeStore, IParticipantReconnectTracker reconnectTracker, ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var device = await DeviceIdentityEndpoints.RequireDeviceAsync(principal, db, ct);
        if (device is null) return Results.Unauthorized();
        var session = await db.StreamSessions.SingleOrDefaultAsync(x => x.Id == sessionId && x.SourceDeviceId == device.Id, ct);
        if (session is null) return Results.NotFound();
        if (session.Status != SessionStatuses.Ended)
        {
            var now = DateTimeOffset.UtcNow;
            session.Status = SessionStatuses.Ended;
            session.EndedAt = now;
            // Includes participants mid-reconnect-grace-period: an owner-initiated end must win
            // immediately over a pending grace timer, which we also cancel so it can't fire a
            // stale "session.left" broadcast afterwards.
            var connected = await db.SessionParticipants.Where(x => x.SessionId == sessionId
                && (x.Status == ParticipantStatuses.Connected || x.Status == ParticipantStatuses.Reconnecting))
                .ToListAsync(ct);
            foreach (var participant in connected)
            {
                participant.Status = ParticipantStatuses.Disconnected;
                participant.ConnectionId = null;
                participant.LeftAt = now;
                reconnectTracker.TryCancelGracePeriod(participant.Id);
            }
            await db.SaveChangesAsync(ct);
            await codeStore.RemoveAsync(sessionId, ct);
            loggerFactory.CreateLogger("SonicRelay.Sessions").LogInformation(
                "Ended session {SessionId} from device {DeviceId}; disconnected {ParticipantCount} participants",
                sessionId, device.Id, connected.Count);
        }
        return Results.Ok(ToResponse(session));
    }

    private static async Task<IResult> RotateCodeAsync(Guid sessionId, ClaimsPrincipal principal, AppDbContext db,
        ISessionCodeStore codeStore, IConfiguration configuration, ILoggerFactory loggerFactory, CancellationToken ct)
    {
        var device = await DeviceIdentityEndpoints.RequireDeviceAsync(principal, db, ct);
        if (device is null) return Results.Unauthorized();
        var session = await db.StreamSessions.SingleOrDefaultAsync(x => x.Id == sessionId && x.SourceDeviceId == device.Id, ct);
        if (session is null) return Results.NotFound();
        if (session.Status is SessionStatuses.Ended or SessionStatuses.Expired) return Results.Conflict();

        var code = GenerateCode();
        var ttl = CodeTtl(configuration);
        session.CodeExpiresAt = DateTimeOffset.UtcNow.Add(ttl);
        await db.SaveChangesAsync(ct);
        await codeStore.StoreAsync(HashCode(code, configuration), session.Id, ttl, ct);
        loggerFactory.CreateLogger("SonicRelay.Sessions").LogInformation(
            "Rotated join code for session {SessionId} from device {DeviceId}", session.Id, device.Id);
        return Results.Ok(ToResponse(session, code));
    }

    private static async Task<IResult> JoinAsync(JoinSessionRequest request, ClaimsPrincipal principal, AppDbContext db,
        ISessionCodeStore codeStore, IConfiguration configuration, ILoggerFactory loggerFactory, CancellationToken ct)
    {
        var device = await DeviceIdentityEndpoints.RequireDeviceAsync(principal, db, ct);
        if (device is null) return Results.Unauthorized();
        var normalizedCode = request.Code?.Trim().ToUpperInvariant() ?? string.Empty;
        if (normalizedCode.Length != 6 || normalizedCode.Any(c => !char.IsAsciiLetterOrDigit(c)))
            return InvalidCode();

        var sessionId = await codeStore.RedeemAsync(HashCode(normalizedCode, configuration), ct);
        if (sessionId is null) return InvalidCode();
        var session = await db.StreamSessions.SingleOrDefaultAsync(x => x.Id == sessionId.Value, ct);
        var now = DateTimeOffset.UtcNow;
        if (session is null || session.CodeExpiresAt <= now
            || session.Status is SessionStatuses.Ended or SessionStatuses.Expired)
        {
            if (session is not null && session.CodeExpiresAt <= now && session.Status != SessionStatuses.Ended)
            {
                session.Status = SessionStatuses.Expired;
                await db.SaveChangesAsync(ct);
                await codeStore.RemoveAsync(session.Id, ct);
                loggerFactory.CreateLogger("SonicRelay.Sessions").LogInformation(
                    "Marked session {SessionId} expired during join", session.Id);
            }
            return InvalidCode();
        }

        var existing = await db.SessionParticipants.SingleOrDefaultAsync(x => x.SessionId == session.Id
            && x.DeviceId == device.Id && x.Role == ParticipantRoles.Viewer, ct);
        if (existing is not null)
        {
            existing.Status = ParticipantStatuses.Connected;
            existing.LeftAt = null;
            await db.SaveChangesAsync(ct);
            loggerFactory.CreateLogger("SonicRelay.Sessions").LogInformation(
                "Reconnected participant {ParticipantId} to session {SessionId} from device {DeviceId}",
                existing.Id, session.Id, device.Id);
            return Results.Ok(ToResponse(session));
        }

        // Viewers mid-reconnect-grace-period still hold their slot, otherwise a new viewer
        // could take it during the grace window and leave a maxViewers=1 session with two
        // viewers once the original one's WebSocket reconnects.
        var viewerCount = await db.SessionParticipants.CountAsync(x => x.SessionId == session.Id
            && x.Role == ParticipantRoles.Viewer
            && (x.Status == ParticipantStatuses.Connected || x.Status == ParticipantStatuses.Reconnecting), ct);
        if (viewerCount >= session.MaxViewers) return Results.Conflict(new { error = "Session viewer limit reached." });

        var participant = new SessionParticipant
        {
            Id = Guid.NewGuid(),
            SessionId = session.Id,
            DeviceId = device.Id,
            Role = ParticipantRoles.Viewer,
            Status = ParticipantStatuses.Connected,
            JoinedAt = now
        };
        db.SessionParticipants.Add(participant);
        if (session.Status == SessionStatuses.Waiting)
        {
            session.Status = SessionStatuses.Active;
            session.StartedAt = now;
        }
        await db.SaveChangesAsync(ct);
        loggerFactory.CreateLogger("SonicRelay.Sessions").LogInformation(
            "Joined session {SessionId} as participant {ParticipantId} from device {DeviceId}",
            session.Id, participant.Id, device.Id);
        return Results.Ok(ToResponse(session));
    }

    private static IResult InvalidCode() => Results.NotFound(new { error = "Invalid or expired session code." });

    private static object ToResponse(StreamSession session, string? code = null) => new
    {
        session.Id,
        session.SourceDeviceId,
        session.Status,
        session.MaxViewers,
        session.CodeExpiresAt,
        session.StartedAt,
        session.EndedAt,
        session.CreatedAt,
        code
    };

    private static TimeSpan CodeTtl(IConfiguration configuration) =>
        TimeSpan.FromMinutes(configuration.GetValue("Sessions:CodeTtlMinutes", 10));

    private static string HashCode(string code, IConfiguration configuration)
    {
        var key = configuration["Sessions:CodeHmacKey"];
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("Sessions:CodeHmacKey must be configured.");
        return Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), Encoding.ASCII.GetBytes(code)));
    }

    private static string GenerateCode()
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
        return string.Create(6, alphabet, static (span, chars) =>
        {
            for (var i = 0; i < span.Length; i++) span[i] = chars[RandomNumberGenerator.GetInt32(chars.Length)];
        });
    }

    // SourceDeviceId/DeviceId are no longer client-supplied: the caller's own device identity
    // (from the DeviceBearer token) is always the publisher of a created session and always the
    // viewer that joins, so there is nothing left for the client to assert about which device it is.
    private sealed record CreateSessionRequest(int? MaxViewers);
    private sealed record JoinSessionRequest(string Code);
}
```

- [ ] **Step 12: Rewrite `SignalingWebSocketEndpoint.cs`**

```csharp
// services/SonicRelay.Api/Endpoints/SignalingWebSocketEndpoint.cs
using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SonicRelay.Application.Abstractions;
using SonicRelay.Api.Services;
using SonicRelay.Domain.Sessions;
using SonicRelay.Infrastructure.Persistence;

namespace SonicRelay.Api.Endpoints;

public static class SignalingWebSocketEndpoint
{
    private const int MaxMessageBytes = 64 * 1024;
    private static readonly HashSet<string> RoutedMessageTypes =
    [
        "publisher.ready", "viewer.ready", "webrtc.offer", "webrtc.answer",
        "webrtc.ice_candidate", "pong"
    ];

    public static IEndpointRouteBuilder MapSignalingWebSocketEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet("/ws/signaling", HandleAsync)
            .RequireAuthorization("signaling:connect");
        return app;
    }

    private static async Task HandleAsync(HttpContext context, AppDbContext db, IConnectionRegistry registry,
        IParticipantReconnectTracker reconnectTracker, IServiceScopeFactory scopeFactory, IConfiguration configuration,
        ILoggerFactory loggerFactory, Observability.SonicRelayMetrics metrics)
    {
        var logger = loggerFactory.CreateLogger("SonicRelay.Signaling");
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        if (!Guid.TryParse(context.Request.Query["sessionId"], out var sessionId))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var device = await DeviceIdentityEndpoints.RequireDeviceAsync(context.User, db, context.RequestAborted);
        if (device is null)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        var session = await db.StreamSessions.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == sessionId, context.RequestAborted);
        if (session is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        if (session.Status is SessionStatuses.Ended or SessionStatuses.Expired
            || session.CodeExpiresAt <= DateTimeOffset.UtcNow)
        {
            logger.LogInformation("Rejected signaling connection to terminal session {SessionId} with status {SessionStatus}",
                sessionId, session.Status);
            context.Response.StatusCode = StatusCodes.Status410Gone;
            return;
        }

        var participant = await db.SessionParticipants.SingleOrDefaultAsync(x =>
            x.SessionId == sessionId && x.DeviceId == device.Id,
            context.RequestAborted);
        if (participant is null)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        using var socketSendLock = new SemaphoreSlim(1, 1);
        async Task SendFrameAsync(ReadOnlyMemory<byte> message, CancellationToken ct)
        {
            await socketSendLock.WaitAsync(ct);
            try
            {
                await socket.SendAsync(message, WebSocketMessageType.Text, true, ct);
            }
            finally
            {
                socketSendLock.Release();
            }
        }
        // A reconnect within the grace period reuses the same participant row (no duplicate),
        // so peers should be told this is a resumed connection rather than a brand-new join.
        var isGracePeriodReconnect = reconnectTracker.TryCancelGracePeriod(participant.Id);

        var connectionId = Guid.NewGuid().ToString("N");
        await registry.RegisterAsync(new ConnectionDescriptor(
            connectionId, sessionId, participant.Id, device.Id, participant.Role,
            DateTimeOffset.UtcNow,
            SendFrameAsync),
            context.RequestAborted);

        participant.ConnectionId = connectionId;
        participant.Status = ParticipantStatuses.Connected;
        participant.LeftAt = null;
        await db.SaveChangesAsync(context.RequestAborted);
        metrics.ConnectionOpened(sessionId);
        logger.LogInformation(
            "Connected signaling participant {ParticipantId} to session {SessionId} with connection {ConnectionId} (graceReconnect={IsGraceReconnect})",
            participant.Id, sessionId, connectionId, isGracePeriodReconnect);

        try
        {
            await SendEnvelopeAsync(SendFrameAsync, "session.joined", sessionId, null, participant.Id,
                new { participantId = participant.Id, role = participant.Role }, context.RequestAborted);
            var peerAnnouncementType = isGracePeriodReconnect ? "participant.reconnected" : "session.joined";
            await BroadcastAsync(registry, sessionId, participant.Id, peerAnnouncementType, participant.Id,
                new { participantId = participant.Id, role = participant.Role }, context.RequestAborted);
            await ReceiveLoopAsync(socket, SendFrameAsync, sessionId, participant.Id, db, registry, logger, metrics,
                context.RequestAborted);
        }
        finally
        {
            metrics.ConnectionClosed(sessionId);
            await registry.UnregisterAsync(connectionId, CancellationToken.None);
            await HandleDisconnectAsync(db, registry, reconnectTracker, scopeFactory, configuration, logger,
                sessionId, participant.Id, connectionId);

            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                try
                {
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "signaling closed", CancellationToken.None);
                }
                catch (WebSocketException)
                {
                    // The peer disconnected before completing the close handshake.
                }
            }
        }
    }

    /// <summary>
    /// On socket drop, a still-live session gets a reconnect grace period: peers are told the
    /// participant is transiently disconnected (not gone), and "session.left" only fires if the
    /// participant hasn't reconnected once the grace period elapses. A terminal session (or a
    /// zero/negative grace period) finalizes immediately, matching the prior behavior.
    /// </summary>
    private static async Task HandleDisconnectAsync(AppDbContext db, IConnectionRegistry registry,
        IParticipantReconnectTracker reconnectTracker, IServiceScopeFactory scopeFactory,
        IConfiguration configuration, ILogger logger, Guid sessionId, Guid participantId, string connectionId)
    {
        var sessionStillLive = !await SessionEndedAsync(db, sessionId, CancellationToken.None);
        var graceDuration = TimeSpan.FromSeconds(
            Math.Max(0, configuration.GetValue("Sessions:ParticipantDisconnectGraceSeconds", 15)));

        if (!sessionStillLive || graceDuration <= TimeSpan.Zero)
        {
            await FinalizeDisconnectAsync(db, participantId, connectionId);
            logger.LogInformation(
                "Disconnected signaling participant {ParticipantId} from session {SessionId} with connection {ConnectionId}",
                participantId, sessionId, connectionId);
            await BroadcastAsync(registry, sessionId, participantId, "session.left", participantId,
                new { participantId }, CancellationToken.None);
            return;
        }

        await MarkReconnectingAsync(db, participantId, connectionId);
        logger.LogInformation(
            "Participant {ParticipantId} disconnected from session {SessionId} with connection {ConnectionId}; starting a {GraceSeconds}s reconnect grace period",
            participantId, sessionId, connectionId, graceDuration.TotalSeconds);
        await BroadcastAsync(registry, sessionId, participantId, "participant.disconnected", participantId,
            new { participantId }, CancellationToken.None);

        reconnectTracker.BeginGracePeriod(participantId, graceDuration, async () =>
        {
            // The request's scoped AppDbContext is disposed by the time this fires, so a fresh
            // scope is required; the connection registry is a singleton and safe to reuse.
            await using var scope = scopeFactory.CreateAsyncScope();
            var scopedDb = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var finalized = await FinalizeDisconnectAsync(scopedDb, participantId, connectionId);
            if (!finalized) return;

            logger.LogInformation(
                "Participant {ParticipantId} did not reconnect within the grace period for session {SessionId}; marking as left",
                participantId, sessionId);
            await BroadcastAsync(registry, sessionId, participantId, "session.left", participantId,
                new { participantId }, CancellationToken.None);
        });
    }

    private static async Task ReceiveLoopAsync(WebSocket socket,
        Func<ReadOnlyMemory<byte>, CancellationToken, Task> sendAsync, Guid sessionId, Guid participantId,
        AppDbContext db, IConnectionRegistry registry, ILogger logger, Observability.SonicRelayMetrics metrics,
        CancellationToken ct)
    {
        while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            using var receiveCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var receiveTask = ReceiveMessageAsync(socket, receiveCancellation.Token);
            while (!receiveTask.IsCompleted)
            {
                var completed = await Task.WhenAny(receiveTask, Task.Delay(TimeSpan.FromSeconds(1), ct));
                if (completed != receiveTask && await SessionEndedAsync(db, sessionId, ct))
                {
                    await SendEnvelopeAsync(sendAsync, "session.ended", sessionId, null, participantId, null, ct);
                    await receiveCancellation.CancelAsync();
                    try { await receiveTask; } catch (OperationCanceledException) { }
                    return;
                }
            }

            var message = await receiveTask;
            if (message is null) return;
            if (!await HandleMessageAsync(sendAsync, sessionId, participantId, message, db, registry, logger, metrics, ct))
                return;
        }
    }

    private static async Task<bool> HandleMessageAsync(Func<ReadOnlyMemory<byte>, CancellationToken, Task> sendAsync,
        Guid sessionId, Guid participantId, byte[] message, AppDbContext db, IConnectionRegistry registry, ILogger logger,
        Observability.SonicRelayMetrics metrics, CancellationToken ct)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(message);
        }
        catch (JsonException)
        {
            await SendErrorAsync(metrics, sendAsync, sessionId, participantId, "invalid_message", ct);
            return true;
        }

        using (document)
        {
            var root = document.RootElement;
            if (!root.TryGetProperty("type", out var typeElement) || typeElement.ValueKind != JsonValueKind.String)
            {
                await SendErrorAsync(metrics, sendAsync, sessionId, participantId, "invalid_message", ct);
                return true;
            }

            var type = typeElement.GetString()!;
            if (type == "ping")
            {
                metrics.RecordMessage("ping");
                await SendEnvelopeAsync(sendAsync, "pong", sessionId, null, participantId, null, ct);
                return true;
            }
            if (!RoutedMessageTypes.Contains(type))
            {
                await SendErrorAsync(metrics, sendAsync, sessionId, participantId, "unsupported_message_type", ct);
                return true;
            }
            // `type` is now guaranteed to be one of the bounded RoutedMessageTypes, so it is
            // safe to use as a metric label without risking cardinality blow-up.
            metrics.RecordMessage(type);
            if (!root.TryGetProperty("to", out var toElement) || !toElement.TryGetGuid(out var toParticipantId))
            {
                await SendErrorAsync(metrics, sendAsync, sessionId, participantId, "invalid_recipient", ct);
                return true;
            }

            if (await SessionEndedAsync(db, sessionId, ct))
            {
                logger.LogInformation("Closing signaling for terminal session {SessionId} and participant {ParticipantId}",
                    sessionId, participantId);
                await SendEnvelopeAsync(sendAsync, "session.ended", sessionId, null, participantId, null, ct);
                return false;
            }

            var messageId = root.TryGetProperty("messageId", out var messageIdElement)
                && messageIdElement.TryGetGuid(out var suppliedMessageId)
                ? suppliedMessageId
                : Guid.NewGuid();
            // SDP and ICE are intentionally opaque here: SDP describes the peer session, while ICE candidates
            // are network paths discovered by the peers. The signaling server only forwards this JSON.
            var payload = root.TryGetProperty("payload", out var payloadElement)
                ? payloadElement.Clone()
                : (JsonElement?)null;
            // The authenticated socket owns the sender identity, so client-supplied `from` is overwritten.
            var routed = SerializeEnvelope(type, messageId, sessionId, participantId, toParticipantId, payload);
            var delivered = await registry.SendToParticipantAsync(sessionId, toParticipantId, routed, ct);
            if (!delivered)
            {
                await SendErrorAsync(metrics, sendAsync, sessionId, participantId, "participant_not_found", ct);
                return true;
            }

            // SDP and ICE can expose media and network details, so only envelope metadata is logged.
            logger.LogDebug("Routed signaling message {MessageType} in session {SessionId} from {FromParticipantId} to {ToParticipantId} with message {MessageId}",
                type, sessionId, participantId, toParticipantId, messageId);
            return true;
        }
    }

    private static async Task<byte[]?> ReceiveMessageAsync(WebSocket socket, CancellationToken ct)
    {
        using var stream = new MemoryStream();
        var buffer = new byte[4096];
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            if (result.MessageType != WebSocketMessageType.Text)
                throw new WebSocketException(WebSocketError.InvalidMessageType);
            if (stream.Length + result.Count > MaxMessageBytes)
                throw new WebSocketException(WebSocketError.HeaderError, "Signaling message is too large.");
            await stream.WriteAsync(buffer.AsMemory(0, result.Count), ct);
            if (result.EndOfMessage) return stream.ToArray();
        }
    }

    private static async Task<bool> SessionEndedAsync(AppDbContext db, Guid sessionId, CancellationToken ct)
    {
        var state = await db.StreamSessions.AsNoTracking()
            .Where(x => x.Id == sessionId)
            .Select(x => new { x.Status, x.CodeExpiresAt })
            .SingleOrDefaultAsync(ct);
        return state is null
            || state.Status is SessionStatuses.Ended or SessionStatuses.Expired
            || state.CodeExpiresAt <= DateTimeOffset.UtcNow;
    }

    private static async Task MarkReconnectingAsync(AppDbContext db, Guid participantId, string connectionId)
    {
        try
        {
            var participant = await db.SessionParticipants.SingleOrDefaultAsync(x => x.Id == participantId);
            if (participant?.ConnectionId != connectionId) return;
            participant.ConnectionId = null;
            participant.Status = ParticipantStatuses.Reconnecting;
            await db.SaveChangesAsync();
        }
        catch (Exception exception) when (exception is DbUpdateException or OperationCanceledException)
        {
            // Connection cleanup must not mask the socket termination.
        }
    }

    /// <summary>Marks a participant as finally left. Returns false if a newer connection already claimed the row.</summary>
    private static async Task<bool> FinalizeDisconnectAsync(AppDbContext db, Guid participantId, string connectionId)
    {
        try
        {
            var participant = await db.SessionParticipants.SingleOrDefaultAsync(x => x.Id == participantId);
            if (participant is null) return false;
            // Already finalized (idempotent) or a different, still-live connection has since
            // claimed this participant; that connection's own lifecycle now owns its fate. A
            // null ConnectionId does NOT count as "claimed" here: the HTTP rejoin path
            // (SessionEndpoints.JoinAsync) sets Status back to Connected without touching
            // ConnectionId, so a client that rejoins over HTTP but crashes before reopening the
            // WebSocket would otherwise look "claimed" forever and never get finalized.
            if (participant.Status == ParticipantStatuses.Disconnected) return false;
            if (participant.Status == ParticipantStatuses.Connected
                && participant.ConnectionId is not null && participant.ConnectionId != connectionId)
                return false;
            participant.ConnectionId = null;
            participant.Status = ParticipantStatuses.Disconnected;
            participant.LeftAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
            return true;
        }
        catch (Exception exception) when (exception is DbUpdateException or OperationCanceledException)
        {
            // Connection cleanup must not mask the socket termination.
            return false;
        }
    }

    private static async Task BroadcastAsync(IConnectionRegistry registry, Guid sessionId, Guid excludedParticipantId,
        string type, Guid? fromParticipantId, object? payload, CancellationToken ct)
    {
        var connections = await registry.ListBySessionAsync(sessionId, ct);
        foreach (var connection in connections.Where(x => x.ParticipantId != excludedParticipantId))
        {
            try
            {
                var bytes = SerializeEnvelope(type, Guid.NewGuid(), sessionId, fromParticipantId,
                    connection.ParticipantId, payload);
                await registry.SendToParticipantAsync(sessionId, connection.ParticipantId, bytes, ct);
            }
            catch (WebSocketException)
            {
                // A concurrent disconnect will be cleaned up by that connection's handler.
            }
        }
    }

    private static Task SendErrorAsync(Observability.SonicRelayMetrics metrics,
        Func<ReadOnlyMemory<byte>, CancellationToken, Task> sendAsync,
        Guid sessionId, Guid participantId, string code, CancellationToken ct)
    {
        metrics.RecordError(code);
        return SendEnvelopeAsync(sendAsync, "error", sessionId, null, participantId, new { code }, ct);
    }

    private static Task SendEnvelopeAsync(Func<ReadOnlyMemory<byte>, CancellationToken, Task> sendAsync,
        string type, Guid sessionId, Guid? fromParticipantId, Guid? toParticipantId, object? payload,
        CancellationToken ct) =>
        sendAsync(SerializeEnvelope(type, Guid.NewGuid(), sessionId, fromParticipantId, toParticipantId, payload), ct);

    private static byte[] SerializeEnvelope(string type, Guid messageId, Guid sessionId, Guid? fromParticipantId,
        Guid? toParticipantId, object? payload) => JsonSerializer.SerializeToUtf8Bytes(new
        {
            type,
            messageId,
            sessionId,
            from = fromParticipantId,
            to = toParticipantId,
            timestamp = DateTimeOffset.UtcNow,
            payload
        });
}
```

(Removed: the `deviceId` query parameter — the device now comes from the authenticated token's `sub` claim via `RequireDeviceAsync` — and the `DeviceAccess.CheckAsync` re-validation block, since `DeviceScopeAuthorizationHandler` already re-checks the caller's `DeviceIdentity.Status`/`CredentialVersion` live on every request as part of the `signaling:connect` policy, before this handler even runs.)

- [ ] **Step 13: Rename `TurnCredentialService.Build`'s parameter for accuracy**

```csharp
// services/SonicRelay.Api/Services/TurnCredentialService.cs
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace SonicRelay.Api.Services;

/// <summary>
/// ICE server configuration handed to WebRTC clients. TURN entries carry
/// time-limited credentials computed with coturn's REST-API convention
/// (`--use-auth-secret`): username is "&lt;unix expiry&gt;:&lt;device id&gt;" and the
/// credential is Base64(HMAC-SHA1(static secret, username)).
/// </summary>
public sealed class TurnOptions
{
    public string? StaticAuthSecret { get; set; }
    public string[] TurnUris { get; set; } = [];
    public string[] StunUris { get; set; } = ["stun:stun.l.google.com:19302"];
    public int CredentialTtlSeconds { get; set; } = 3600;
}

public sealed record IceServerEntry(IReadOnlyList<string> Urls, string? Username = null, string? Credential = null);

public sealed record IceServersResponse(IReadOnlyList<IceServerEntry> IceServers, int TtlSeconds);

public sealed class TurnCredentialService(IOptions<TurnOptions> options, TimeProvider time)
{
    public IceServersResponse Build(string deviceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        var settings = options.Value;
        var servers = new List<IceServerEntry>();
        if (settings.StunUris.Length > 0)
        {
            servers.Add(new IceServerEntry(settings.StunUris));
        }

        if (!string.IsNullOrWhiteSpace(settings.StaticAuthSecret) && settings.TurnUris.Length > 0)
        {
            var expiry = time.GetUtcNow().ToUnixTimeSeconds() + settings.CredentialTtlSeconds;
            var username = FormattableString.Invariant($"{expiry}:{deviceId}");
            var credential = Convert.ToBase64String(HMACSHA1.HashData(
                Encoding.UTF8.GetBytes(settings.StaticAuthSecret),
                Encoding.UTF8.GetBytes(username)));
            servers.Add(new IceServerEntry(settings.TurnUris, username, credential));
        }

        return new IceServersResponse(servers, settings.CredentialTtlSeconds);
    }
}
```

- [ ] **Step 14: Rewrite `WebRtcEndpoints.cs`**

```csharp
// services/SonicRelay.Api/Endpoints/WebRtcEndpoints.cs
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using SonicRelay.Api.Contracts;
using SonicRelay.Api.Observability;
using SonicRelay.Api.Services;
using SonicRelay.Infrastructure.Persistence;

namespace SonicRelay.Api.Endpoints;

public static class WebRtcEndpoints
{
    public static IEndpointRouteBuilder MapWebRtcEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/webrtc").WithTags("WebRTC");
        group.MapGet("/ice-servers", GetIceServersAsync).RequireAuthorization("turn:credentials");
        group.MapPost("/stats", ReportStatsAsync).RequireAuthorization("DeviceAuthenticated").WithName("ReportWebRtcStats");
        return app;
    }

    private static async Task<IResult> GetIceServersAsync(ClaimsPrincipal principal, AppDbContext db,
        TurnCredentialService credentials, CancellationToken ct)
    {
        var device = await DeviceIdentityEndpoints.RequireDeviceAsync(principal, db, ct);
        if (device is null) return Results.Unauthorized();
        return Results.Ok(credentials.Build(device.Id.ToString("D")));
    }

    private static async Task<IResult> ReportStatsAsync(
        WebRtcStatsReport report,
        ClaimsPrincipal principal,
        AppDbContext db,
        SonicRelayMetrics metrics,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var device = await DeviceIdentityEndpoints.RequireDeviceAsync(principal, db, ct);
        if (device is null) return Results.Unauthorized();

        // Only a participant of the session may report stats for it, so an authenticated
        // device cannot inject metrics for arbitrary sessions.
        var isParticipant = await db.SessionParticipants.AsNoTracking()
            .AnyAsync(x => x.SessionId == report.SessionId && x.DeviceId == device.Id, ct);
        if (!isParticipant) return Results.Forbid();

        var role = report.Role is "publisher" or "viewer" ? report.Role : "unknown";
        var transportMode = DeriveTransportMode(report.SelectedCandidatePair);
        var packetLossRatio = DerivePacketLossRatio(report.InboundAudio);
        var jitterMs = report.InboundAudio?.Jitter is { } jitter ? jitter * 1000.0 : (double?)null;
        var rttMs = report.CandidatePair?.CurrentRoundTripTime is { } rtt ? rtt * 1000.0 : (double?)null;

        metrics.RecordStats(role, transportMode, packetLossRatio, jitterMs, rttMs, report.IceRestart);

        // Structured, low-cardinality audit line. The session id is hashed so logs can be
        // correlated to a session without exposing the real id, and no SDP/ICE is logged.
        var logger = loggerFactory.CreateLogger("SonicRelay.WebRtcStats");
        logger.LogInformation(
            "WebRTC stats: session={SessionHash} role={Role} transport={TransportMode} ice={IceState} iceRestart={IceRestart} lossRatio={LossRatio} jitterMs={JitterMs} rttMs={RttMs}",
            HashSession(report.SessionId), role, transportMode ?? "unknown",
            report.IceConnectionState ?? "unknown", report.IceRestart,
            packetLossRatio, jitterMs, rttMs);

        return Results.Accepted();
    }

    // Maps the selected candidate pair to the bounded transport mode used in metrics.
    private static string? DeriveTransportMode(SelectedCandidatePairReport? pair)
    {
        if (pair is null) return null;
        var remote = pair.RemoteCandidateType?.ToLowerInvariant();
        var local = pair.LocalCandidateType?.ToLowerInvariant();
        if (remote == "relay" || local == "relay")
        {
            return (pair.RelayProtocol?.ToLowerInvariant()) switch
            {
                "tls" => "turn_tls",
                "tcp" => "turn_tcp",
                _ => "turn_udp"
            };
        }
        if (remote == "srflx" || local == "srflx" || remote == "prflx" || local == "prflx")
        {
            return "stun";
        }
        if (remote == "host" || local == "host")
        {
            return "direct";
        }
        return null;
    }

    private static double? DerivePacketLossRatio(InboundAudioReport? inbound)
    {
        if (inbound?.PacketsLost is not { } lost || inbound.PacketsReceived is not { } received) return null;
        var total = lost + received;
        return total <= 0 ? 0 : (double)lost / total;
    }

    // Short, stable, non-reversible tag for correlation without exposing the session id.
    private static string HashSession(Guid sessionId)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(sessionId.ToString("N")));
        return Convert.ToHexString(hash.AsSpan(0, 6)).ToLowerInvariant();
    }
}
```

- [ ] **Step 15: Build the API project to verify everything compiles**

Run: `dotnet build services/SonicRelay.Api/SonicRelay.Api.csproj`
Expected: `Build succeeded.` (0 errors). This confirms the domain change, `Program.cs`, and all three rewritten endpoint files are internally consistent. `dotnet test` will still fail to build at this point — that is expected and resolved in Task 3.

- [ ] **Step 16: Commit**

```bash
git add src/SonicRelay.Domain/Sessions/StreamSession.cs \
  src/SonicRelay.Application/Abstractions/IConnectionRegistry.cs \
  src/SonicRelay.Infrastructure/Persistence/AppDbContext.cs \
  src/SonicRelay.Infrastructure/Persistence/Migrations/ \
  services/SonicRelay.Api/Services/DeviceCredentialService.cs \
  services/SonicRelay.Api/Authorization/DeviceScopeRequirement.cs \
  services/SonicRelay.Api/Authorization/DeviceScopeAuthorizationHandler.cs \
  services/SonicRelay.Api/Program.cs \
  services/SonicRelay.Api/Endpoints/SessionEndpoints.cs \
  services/SonicRelay.Api/Endpoints/SignalingWebSocketEndpoint.cs \
  services/SonicRelay.Api/Services/TurnCredentialService.cs \
  services/SonicRelay.Api/Endpoints/WebRtcEndpoints.cs
git commit -m "Migrate sessions, signaling, and TURN credentials to device identity"
```

---

### Task 3: Rewrite the four affected integration test files

**Files:**
- Modify: `tests/SonicRelay.Api.IntegrationTests/SessionEndpointsTests.cs`
- Modify: `tests/SonicRelay.Api.IntegrationTests/SignalingWebSocketTests.cs`
- Modify: `tests/SonicRelay.Api.IntegrationTests/WebRtcEndpointsTests.cs`
- Modify: `tests/SonicRelay.Api.IntegrationTests/WebRtcObservabilityTests.cs`

**Interfaces:**
- Consumes: `DeviceIdentityTestHelper.BootstrapAndAuthorizeAsync` (Task 1); the production shapes from Task 2 (`StreamSession`, `SessionParticipant` without `OwnerUserId`/`UserId`; `session:create`/`session:join`/`session:end`/`DeviceAuthenticated`/`turn:credentials` policies; `CreateSessionRequest(int? MaxViewers)`/`JoinSessionRequest(string Code)` request shapes with no device-id field).

All four test files reference the removed `OwnerUserId`/`UserId` fields today, and they share one test project (`SonicRelay.Api.IntegrationTests.csproj`), so none of them compiles in isolation from the others once Task 2 lands — this task must update all four together for `dotnet test` to run at all. This is the point where the whole solution becomes buildable and testable again.

- [ ] **Step 1: Rewrite `SessionEndpointsTests.cs`**

```csharp
// tests/SonicRelay.Api.IntegrationTests/SessionEndpointsTests.cs
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SonicRelay.Domain.Devices;
using SonicRelay.Domain.Sessions;
using SonicRelay.Infrastructure.Persistence;
using Xunit;

namespace SonicRelay.Api.IntegrationTests;

public sealed class SessionEndpointsTests : IClassFixture<SonicRelayApiFactory>
{
    private readonly SonicRelayApiFactory _factory;

    public SessionEndpointsTests(SonicRelayApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Create_from_a_publisher_device_creates_a_session_and_adds_it_as_publisher()
    {
        var (client, deviceId) = await BootstrapAsync(DeviceTypes.WindowsPublisher, DevicePlatforms.Windows);

        var response = await client.PostAsJsonAsync("/api/sessions", new { maxViewers = 2 });
        var body = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Matches("^[A-Z0-9]{6}$", body.GetProperty("code").GetString()!);
        Assert.Equal(deviceId, body.GetProperty("sourceDeviceId").GetGuid());
        var sessionId = body.GetProperty("id").GetGuid();
        await using var scope = _factory.Services.CreateAsyncScope();
        var participant = await scope.ServiceProvider.GetRequiredService<AppDbContext>().SessionParticipants.SingleAsync(x => x.SessionId == sessionId);
        Assert.Equal(ParticipantRoles.Publisher, participant.Role);
        Assert.Equal(deviceId, participant.DeviceId);
    }

    [Fact]
    public async Task Create_rejects_a_viewer_device_that_lacks_the_session_create_scope()
    {
        var (client, _) = await BootstrapAsync(DeviceTypes.FlutterViewer, DevicePlatforms.Android);

        var response = await client.PostAsJsonAsync("/api/sessions", new { maxViewers = 2 });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Create_rejects_a_revoked_publisher_device_even_with_a_still_unexpired_token()
    {
        var (client, deviceId) = await BootstrapAsync(DeviceTypes.WindowsPublisher, DevicePlatforms.Windows);
        await RevokeDeviceAsync(deviceId);

        var response = await client.PostAsJsonAsync("/api/sessions", new { maxViewers = 2 });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Join_from_a_viewer_device_validates_the_code_and_adds_it_as_viewer()
    {
        var (_, sessionId, code) = await CreateSessionAsync();
        var (viewerClient, viewerDeviceId) = await BootstrapAsync(DeviceTypes.FlutterViewer, DevicePlatforms.Android);

        var response = await viewerClient.PostAsJsonAsync("/api/sessions/join", new { code });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using var scope = _factory.Services.CreateAsyncScope();
        var participant = await scope.ServiceProvider.GetRequiredService<AppDbContext>().SessionParticipants
            .SingleAsync(x => x.SessionId == sessionId && x.Role == ParticipantRoles.Viewer);
        Assert.Equal(viewerDeviceId, participant.DeviceId);
    }

    [Fact]
    public async Task Join_rejects_a_publisher_device_that_lacks_the_session_join_scope()
    {
        var (_, _, code) = await CreateSessionAsync();
        var (publisherClient, _) = await BootstrapAsync(DeviceTypes.WindowsPublisher, DevicePlatforms.Windows);

        var response = await publisherClient.PostAsJsonAsync("/api/sessions/join", new { code });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Join_rejects_a_revoked_viewer_device_even_with_a_still_unexpired_token()
    {
        var (_, _, code) = await CreateSessionAsync();
        var (viewerClient, viewerDeviceId) = await BootstrapAsync(DeviceTypes.FlutterViewer, DevicePlatforms.Android);
        await RevokeDeviceAsync(viewerDeviceId);

        var response = await viewerClient.PostAsJsonAsync("/api/sessions/join", new { code });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Wrong_and_expired_codes_have_the_same_response()
    {
        var (_, sessionId, code) = await CreateSessionAsync();
        var (viewerClient, _) = await BootstrapAsync(DeviceTypes.FlutterViewer, DevicePlatforms.Android);
        await SetSessionExpiryAsync(sessionId, DateTimeOffset.UtcNow.AddMinutes(-1));

        var expired = await viewerClient.PostAsJsonAsync("/api/sessions/join", new { code });
        var wrong = await viewerClient.PostAsJsonAsync("/api/sessions/join", new { code = "ZZZZZZ" });

        Assert.Equal(HttpStatusCode.NotFound, expired.StatusCode);
        Assert.Equal(wrong.StatusCode, expired.StatusCode);
        Assert.Equal(await wrong.Content.ReadAsStringAsync(), await expired.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Rotate_invalidates_old_code_and_returns_a_new_code()
    {
        var (ownerClient, sessionId, oldCode) = await CreateSessionAsync();

        var rotated = await ownerClient.PostAsync($"/api/sessions/{sessionId}/rotate-code", null);
        var body = await ReadJsonAsync(rotated);
        var newCode = body.GetProperty("code").GetString()!;

        Assert.Equal(HttpStatusCode.OK, rotated.StatusCode);
        Assert.Matches("^[A-Z0-9]{6}$", newCode);
        Assert.NotEqual(oldCode, newCode);

        var (viewerClient, _) = await BootstrapAsync(DeviceTypes.FlutterViewer, DevicePlatforms.Android);
        var oldJoin = await viewerClient.PostAsJsonAsync("/api/sessions/join", new { code = oldCode });
        var newJoin = await viewerClient.PostAsJsonAsync("/api/sessions/join", new { code = newCode });
        Assert.Equal(HttpStatusCode.NotFound, oldJoin.StatusCode);
        Assert.Equal(HttpStatusCode.OK, newJoin.StatusCode);
    }

    [Fact]
    public async Task Join_rejects_viewers_beyond_the_limit()
    {
        var (_, _, code) = await CreateSessionAsync(maxViewers: 1);
        var (firstClient, _) = await BootstrapAsync(DeviceTypes.FlutterViewer, DevicePlatforms.Android);
        var (secondClient, _) = await BootstrapAsync(DeviceTypes.FlutterViewer, DevicePlatforms.Android);

        var accepted = await firstClient.PostAsJsonAsync("/api/sessions/join", new { code });
        var rejected = await secondClient.PostAsJsonAsync("/api/sessions/join", new { code });

        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);
    }

    [Fact]
    public async Task Join_rejects_a_second_viewer_while_the_only_slot_is_mid_reconnect_grace_period()
    {
        var (_, sessionId, code) = await CreateSessionAsync(maxViewers: 1);
        var (firstClient, _) = await BootstrapAsync(DeviceTypes.FlutterViewer, DevicePlatforms.Android);
        var joined = await firstClient.PostAsJsonAsync("/api/sessions/join", new { code });
        Assert.Equal(HttpStatusCode.OK, joined.StatusCode);

        // Simulate the first viewer's WebSocket dropping mid-session: the signaling endpoint
        // moves it to Reconnecting (not Disconnected) while the backend's grace period runs.
        // It must still hold its slot, otherwise a second viewer could take it here and the
        // first one could then also reconnect, leaving two viewers in a maxViewers=1 session.
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var participant = await db.SessionParticipants
                .SingleAsync(x => x.SessionId == sessionId && x.Role == ParticipantRoles.Viewer);
            participant.Status = ParticipantStatuses.Reconnecting;
            participant.ConnectionId = null;
            await db.SaveChangesAsync();
        }

        var (secondClient, _) = await BootstrapAsync(DeviceTypes.FlutterViewer, DevicePlatforms.Android);
        var rejected = await secondClient.PostAsJsonAsync("/api/sessions/join", new { code });

        Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);
    }

    [Fact]
    public async Task Create_is_rate_limited_by_ip_regardless_of_which_device_calls()
    {
        await using var factory = new SonicRelayApiFactory(new Dictionary<string, string?>
        {
            ["RateLimits:CreateSession:PermitLimit"] = "1"
        });
        var (firstClient, _) = await BootstrapAsync(DeviceTypes.WindowsPublisher, DevicePlatforms.Windows, factory);
        var (secondClient, _) = await BootstrapAsync(DeviceTypes.WindowsPublisher, DevicePlatforms.Windows, factory);

        var accepted = await firstClient.PostAsJsonAsync("/api/sessions", new { maxViewers = 2 });
        // create-session is IP-keyed (DeviceBearer tokens carry no ClaimTypes.NameIdentifier, so a
        // per-user limiter would silently fall back to IP anyway) — a second, unrelated device
        // hitting the same test host shares the same quota.
        var rejected = await secondClient.PostAsJsonAsync("/api/sessions", new { maxViewers = 2 });

        Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
    }

    [Fact]
    public async Task Join_is_rate_limited_by_ip_regardless_of_which_device_calls()
    {
        await using var factory = new SonicRelayApiFactory(new Dictionary<string, string?>
        {
            ["RateLimits:JoinSession:PermitLimit"] = "1"
        });
        var (_, _, code) = await CreateSessionAsync(factory: factory, maxViewers: 3);
        var (firstClient, _) = await BootstrapAsync(DeviceTypes.FlutterViewer, DevicePlatforms.Android, factory);
        var (secondClient, _) = await BootstrapAsync(DeviceTypes.FlutterViewer, DevicePlatforms.Android, factory);

        var accepted = await firstClient.PostAsJsonAsync("/api/sessions/join", new { code });
        var rejected = await secondClient.PostAsJsonAsync("/api/sessions/join", new { code });

        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
    }

    [Fact]
    public async Task Rotate_code_is_rate_limited()
    {
        await using var factory = new SonicRelayApiFactory(new Dictionary<string, string?>
        {
            ["RateLimits:RotateCode:PermitLimit"] = "1"
        });
        var (ownerClient, sessionId, _) = await CreateSessionAsync(factory);

        var accepted = await ownerClient.PostAsync($"/api/sessions/{sessionId}/rotate-code", null);
        var rejected = await ownerClient.PostAsync($"/api/sessions/{sessionId}/rotate-code", null);

        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
    }

    [Fact]
    public async Task Cleanup_expires_sessions_and_removes_only_stale_disconnected_participants()
    {
        await using var factory = new SonicRelayApiFactory(new Dictionary<string, string?>
        {
            ["Sessions:DisconnectedParticipantRetentionHours"] = "24"
        });
        var (_, sessionId, _) = await CreateSessionAsync(factory);
        var staleId = Guid.NewGuid();
        var recentId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await using (var seedScope = factory.Services.CreateAsyncScope())
        {
            var db = seedScope.ServiceProvider.GetRequiredService<AppDbContext>();
            var session = await db.StreamSessions.SingleAsync(x => x.Id == sessionId);
            session.CodeExpiresAt = now.AddMinutes(-1);
            db.SessionParticipants.AddRange(
                new SessionParticipant
                {
                    Id = staleId,
                    SessionId = sessionId,
                    DeviceId = session.SourceDeviceId,
                    Role = ParticipantRoles.Viewer,
                    Status = ParticipantStatuses.Disconnected,
                    JoinedAt = now.AddDays(-3),
                    LeftAt = now.AddDays(-2)
                },
                new SessionParticipant
                {
                    Id = recentId,
                    SessionId = sessionId,
                    DeviceId = session.SourceDeviceId,
                    Role = ParticipantRoles.Viewer,
                    Status = ParticipantStatuses.Disconnected,
                    JoinedAt = now.AddHours(-2),
                    LeftAt = now.AddHours(-1)
                });
            await db.SaveChangesAsync();
        }

        await factory.Services.GetRequiredService<SessionCleanupService>().CleanupOnceAsync(CancellationToken.None);

        await using var assertScope = factory.Services.CreateAsyncScope();
        var assertDb = assertScope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(SessionStatuses.Expired,
            (await assertDb.StreamSessions.SingleAsync(x => x.Id == sessionId)).Status);
        Assert.False(await assertDb.SessionParticipants.AnyAsync(x => x.Id == staleId));
        Assert.True(await assertDb.SessionParticipants.AnyAsync(x => x.Id == recentId));
        Assert.True(await assertDb.SessionParticipants.AnyAsync(x => x.SessionId == sessionId
            && x.Status == ParticipantStatuses.Connected));
    }

    [Fact]
    public async Task Owner_can_list_get_and_end_a_session()
    {
        var (ownerClient, sessionId, _) = await CreateSessionAsync();

        var active = await ownerClient.GetFromJsonAsync<JsonElement>("/api/sessions/active");
        Assert.Contains(active.EnumerateArray(), x => x.GetProperty("id").GetGuid() == sessionId);
        var get = await ownerClient.GetAsync($"/api/sessions/{sessionId}");
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);

        var end = await ownerClient.PostAsync($"/api/sessions/{sessionId}/end", null);
        Assert.Equal(HttpStatusCode.OK, end.StatusCode);
        var ended = await ReadJsonAsync(end);
        Assert.Equal(SessionStatuses.Ended, ended.GetProperty("status").GetString());
    }

    [Fact]
    public async Task A_viewer_device_cannot_end_or_rotate_a_session_it_does_not_own()
    {
        var (_, sessionId, _) = await CreateSessionAsync();
        var (viewerClient, _) = await BootstrapAsync(DeviceTypes.FlutterViewer, DevicePlatforms.Android);

        var end = await viewerClient.PostAsync($"/api/sessions/{sessionId}/end", null);
        var rotate = await viewerClient.PostAsync($"/api/sessions/{sessionId}/rotate-code", null);

        Assert.Equal(HttpStatusCode.NotFound, end.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, rotate.StatusCode);
    }

    [Fact]
    public async Task Two_independent_device_pairs_do_not_see_each_others_sessions()
    {
        var (firstOwnerClient, firstSessionId, _) = await CreateSessionAsync();
        var (secondOwnerClient, secondSessionId, _) = await CreateSessionAsync();

        var firstActive = await firstOwnerClient.GetFromJsonAsync<JsonElement>("/api/sessions/active");
        var secondActive = await secondOwnerClient.GetFromJsonAsync<JsonElement>("/api/sessions/active");

        Assert.Contains(firstActive.EnumerateArray(), x => x.GetProperty("id").GetGuid() == firstSessionId);
        Assert.DoesNotContain(firstActive.EnumerateArray(), x => x.GetProperty("id").GetGuid() == secondSessionId);
        Assert.Contains(secondActive.EnumerateArray(), x => x.GetProperty("id").GetGuid() == secondSessionId);
        Assert.DoesNotContain(secondActive.EnumerateArray(), x => x.GetProperty("id").GetGuid() == firstSessionId);

        var crossGet = await secondOwnerClient.GetAsync($"/api/sessions/{firstSessionId}");
        Assert.Equal(HttpStatusCode.NotFound, crossGet.StatusCode);
    }

    [Fact]
    public async Task Ending_a_session_finalizes_participants_mid_reconnect_grace_period()
    {
        var (ownerClient, sessionId, _) = await CreateSessionAsync();
        var participantId = Guid.NewGuid();
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var session = await db.StreamSessions.SingleAsync(x => x.Id == sessionId);
            db.SessionParticipants.Add(new SessionParticipant
            {
                Id = participantId,
                SessionId = sessionId,
                DeviceId = session.SourceDeviceId,
                Role = ParticipantRoles.Viewer,
                ConnectionId = null,
                Status = ParticipantStatuses.Reconnecting,
                JoinedAt = DateTimeOffset.UtcNow.AddSeconds(-5)
            });
            await db.SaveChangesAsync();
        }

        var end = await ownerClient.PostAsync($"/api/sessions/{sessionId}/end", null);
        Assert.Equal(HttpStatusCode.OK, end.StatusCode);

        await using var assertScope = _factory.Services.CreateAsyncScope();
        var assertDb = assertScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var participant = await assertDb.SessionParticipants.SingleAsync(x => x.Id == participantId);
        Assert.Equal(ParticipantStatuses.Disconnected, participant.Status);
        Assert.NotNull(participant.LeftAt);
    }

    private async Task<(HttpClient Client, Guid DeviceId)> BootstrapAsync(string deviceType, string platform,
        SonicRelayApiFactory? factory = null)
    {
        var client = (factory ?? _factory).CreateClient();
        var session = await DeviceIdentityTestHelper.BootstrapAndAuthorizeAsync(client, deviceType, platform);
        return (client, session.DeviceId);
    }

    private async Task<(HttpClient Owner, Guid SessionId, string Code)> CreateSessionAsync(
        SonicRelayApiFactory? factory = null, int maxViewers = 2)
    {
        var (client, _) = await BootstrapAsync(DeviceTypes.WindowsPublisher, DevicePlatforms.Windows, factory);
        var response = await client.PostAsJsonAsync("/api/sessions", new { maxViewers });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await ReadJsonAsync(response);
        return (client, body.GetProperty("id").GetGuid(), body.GetProperty("code").GetString()!);
    }

    private async Task RevokeDeviceAsync(Guid deviceId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var device = await db.DeviceIdentities.SingleAsync(x => x.Id == deviceId);
        device.Status = SonicRelay.Domain.DeviceIdentities.DeviceIdentityStatuses.Revoked;
        await db.SaveChangesAsync();
    }

    private async Task SetSessionExpiryAsync(Guid sessionId, DateTimeOffset expiry)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var session = await db.StreamSessions.SingleAsync(x => x.Id == sessionId);
        session.CodeExpiresAt = expiry;
        await db.SaveChangesAsync();
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        return document.RootElement.Clone();
    }
}
```

- [ ] **Step 2: Rewrite `SignalingWebSocketTests.cs`**

```csharp
// tests/SonicRelay.Api.IntegrationTests/SignalingWebSocketTests.cs
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SonicRelay.Domain.DeviceIdentities;
using SonicRelay.Domain.Devices;
using SonicRelay.Domain.Sessions;
using SonicRelay.Infrastructure.Persistence;
using Xunit;

namespace SonicRelay.Api.IntegrationTests;

public sealed class SignalingWebSocketTests : IClassFixture<SonicRelayApiFactory>
{
    private readonly SonicRelayApiFactory _factory;

    public SignalingWebSocketTests(SonicRelayApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Signaling_rejects_an_unauthenticated_websocket_upgrade()
    {
        var client = _factory.Server.CreateWebSocketClient();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => client.ConnectAsync(
            new Uri($"ws://localhost/ws/signaling?sessionId={Guid.NewGuid()}"),
            CancellationToken.None));

        Assert.Contains("401", exception.Message);
    }

    [Fact]
    public async Task Signaling_does_not_route_to_a_participant_in_another_session()
    {
        var sender = await CreateParticipantAsync("sender");
        var receiver = await CreateParticipantAsync("receiver");
        using var senderSocket = await ConnectAsync(sender);
        using var receiverSocket = await ConnectAsync(receiver);
        await ReceiveAsync(senderSocket);
        await ReceiveAsync(receiverSocket);

        await SendAsync(senderSocket, new
        {
            type = "webrtc.offer",
            to = receiver.ParticipantId,
            payload = new { sdp = "sensitive-test-sdp" }
        });

        var error = await ReceiveAsync(senderSocket);
        Assert.Equal("error", error.GetProperty("type").GetString());
        Assert.Equal("participant_not_found", error.GetProperty("payload").GetProperty("code").GetString());

        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ReceiveAsync(receiverSocket, timeout.Token));
    }

    [Fact]
    public async Task Signaling_rejects_an_unknown_session()
    {
        var participant = await CreateParticipantAsync("invalid-admission");
        var client = _factory.Server.CreateWebSocketClient();
        client.ConfigureRequest = request => request.Headers.Authorization = $"Bearer {participant.AccessToken}";

        var missingSession = await Assert.ThrowsAsync<InvalidOperationException>(() => client.ConnectAsync(
            new Uri($"ws://localhost/ws/signaling?sessionId={Guid.NewGuid()}"),
            CancellationToken.None));
        Assert.Contains("404", missingSession.Message);
    }

    [Fact]
    public async Task Signaling_rejects_a_device_that_is_not_a_participant_of_the_session()
    {
        var publisher = await CreateParticipantAsync("not-a-participant-publisher");
        var stranger = await CreateParticipantAsync("not-a-participant-stranger");
        var client = _factory.Server.CreateWebSocketClient();
        client.ConfigureRequest = request => request.Headers.Authorization = $"Bearer {stranger.AccessToken}";

        var forbidden = await Assert.ThrowsAsync<InvalidOperationException>(() => client.ConnectAsync(
            new Uri($"ws://localhost/ws/signaling?sessionId={publisher.SessionId}"),
            CancellationToken.None));
        Assert.Contains("403", forbidden.Message);
    }

    [Fact]
    public async Task Signaling_normalizes_the_envelope_and_overwrites_client_metadata()
    {
        var participant = await CreateParticipantAsync("normalized");
        using var socket = await ConnectAsync(participant);
        var joined = await ReceiveAsync(socket);
        AssertEnvelope(joined, "session.joined", participant.SessionId);

        var messageId = Guid.NewGuid();
        await SendAsync(socket, new
        {
            type = "webrtc.offer",
            messageId,
            sessionId = Guid.NewGuid(),
            from = Guid.NewGuid(),
            to = participant.ParticipantId,
            timestamp = DateTimeOffset.UnixEpoch,
            payload = new { sdp = "opaque-sdp" }
        });

        var routed = await ReceiveAsync(socket);
        AssertEnvelope(routed, "webrtc.offer", participant.SessionId);
        Assert.Equal(messageId, routed.GetProperty("messageId").GetGuid());
        Assert.Equal(participant.ParticipantId, routed.GetProperty("from").GetGuid());
        Assert.Equal(participant.ParticipantId, routed.GetProperty("to").GetGuid());
        Assert.Equal("opaque-sdp", routed.GetProperty("payload").GetProperty("sdp").GetString());
        Assert.NotEqual(DateTimeOffset.UnixEpoch, routed.GetProperty("timestamp").GetDateTimeOffset());
    }

    [Fact]
    public async Task Signaling_announces_a_new_participant_to_existing_session_peers()
    {
        var publisher = await CreateParticipantAsync("join-announcement-publisher");
        var viewer = await CreateViewerAsync(publisher, "join-announcement-viewer");
        using var publisherSocket = await ConnectAsync(publisher);
        await ReceiveAsync(publisherSocket);

        using var viewerSocket = await ConnectAsync(viewer);
        var viewerJoined = await ReceiveAsync(viewerSocket);
        Assert.Equal(viewer.ParticipantId, viewerJoined.GetProperty("payload").GetProperty("participantId").GetGuid());
        Assert.Equal(ParticipantRoles.Viewer, viewerJoined.GetProperty("payload").GetProperty("role").GetString());

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var announced = await ReceiveAsync(publisherSocket, timeout.Token);
        AssertEnvelope(announced, "session.joined", publisher.SessionId);
        Assert.Equal(viewer.ParticipantId, announced.GetProperty("from").GetGuid());
        Assert.Equal(publisher.ParticipantId, announced.GetProperty("to").GetGuid());
        Assert.Equal(viewer.ParticipantId, announced.GetProperty("payload").GetProperty("participantId").GetGuid());
        Assert.Equal(ParticipantRoles.Viewer, announced.GetProperty("payload").GetProperty("role").GetString());
    }

    [Fact]
    public async Task Signaling_reconnecting_within_the_grace_period_reports_reconnection_not_departure()
    {
        await using var factory = new SonicRelayApiFactory(new Dictionary<string, string?>
        {
            ["Sessions:ParticipantDisconnectGraceSeconds"] = "10"
        });
        var publisher = await CreateParticipantAsync("grace-reconnect-publisher", factory);
        var viewer = await CreateViewerAsync(publisher, "grace-reconnect-viewer", factory);
        using var publisherSocket = await ConnectAsync(publisher, factory);
        await ReceiveAsync(publisherSocket);

        var viewerSocket = await ConnectAsync(viewer, factory);
        await ReceiveAsync(viewerSocket);
        var joinAnnouncement = await ReceiveAsync(publisherSocket);
        Assert.Equal("session.joined", joinAnnouncement.GetProperty("type").GetString());

        viewerSocket.Dispose();

        using var disconnectTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var disconnected = await ReceiveAsync(publisherSocket, disconnectTimeout.Token);
        AssertEnvelope(disconnected, "participant.disconnected", publisher.SessionId);
        Assert.Equal(viewer.ParticipantId, disconnected.GetProperty("payload").GetProperty("participantId").GetGuid());

        using var reconnectedViewerSocket = await ConnectAsync(viewer, factory);
        await ReceiveAsync(reconnectedViewerSocket);

        using var reconnectTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var reconnected = await ReceiveAsync(publisherSocket, reconnectTimeout.Token);
        AssertEnvelope(reconnected, "participant.reconnected", publisher.SessionId);
        Assert.Equal(viewer.ParticipantId, reconnected.GetProperty("payload").GetProperty("participantId").GetGuid());

        await using var assertScope = factory.Services.CreateAsyncScope();
        var db = assertScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var participantRow = await db.SessionParticipants.SingleAsync(x => x.Id == viewer.ParticipantId);
        Assert.Equal(ParticipantStatuses.Connected, participantRow.Status);
    }

    [Fact]
    public async Task Signaling_finalizes_as_left_after_the_grace_period_elapses_without_reconnecting()
    {
        await using var factory = new SonicRelayApiFactory(new Dictionary<string, string?>
        {
            ["Sessions:ParticipantDisconnectGraceSeconds"] = "1"
        });
        var publisher = await CreateParticipantAsync("grace-expiry-publisher", factory);
        var viewer = await CreateViewerAsync(publisher, "grace-expiry-viewer", factory);
        using var publisherSocket = await ConnectAsync(publisher, factory);
        await ReceiveAsync(publisherSocket);

        var viewerSocket = await ConnectAsync(viewer, factory);
        await ReceiveAsync(viewerSocket);
        await ReceiveAsync(publisherSocket); // session.joined announcement

        viewerSocket.Dispose();

        using var disconnectTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var disconnected = await ReceiveAsync(publisherSocket, disconnectTimeout.Token);
        AssertEnvelope(disconnected, "participant.disconnected", publisher.SessionId);

        using var leftTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var left = await ReceiveAsync(publisherSocket, leftTimeout.Token);
        AssertEnvelope(left, "session.left", publisher.SessionId);
        Assert.Equal(viewer.ParticipantId, left.GetProperty("payload").GetProperty("participantId").GetGuid());

        await using var assertScope = factory.Services.CreateAsyncScope();
        var db = assertScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var participantRow = await db.SessionParticipants.SingleAsync(x => x.Id == viewer.ParticipantId);
        Assert.Equal(ParticipantStatuses.Disconnected, participantRow.Status);
        Assert.NotNull(participantRow.LeftAt);
    }

    [Fact]
    public async Task Signaling_finalizes_a_participant_that_rejoined_over_http_but_never_reopened_the_socket()
    {
        await using var factory = new SonicRelayApiFactory(new Dictionary<string, string?>
        {
            ["Sessions:ParticipantDisconnectGraceSeconds"] = "1"
        });
        var publisher = await CreateParticipantAsync("http-rejoin-publisher", factory);
        var viewer = await CreateViewerAsync(publisher, "http-rejoin-viewer", factory);
        using var publisherSocket = await ConnectAsync(publisher, factory);
        await ReceiveAsync(publisherSocket);

        var viewerSocket = await ConnectAsync(viewer, factory);
        await ReceiveAsync(viewerSocket);
        await ReceiveAsync(publisherSocket); // session.joined announcement

        viewerSocket.Dispose();
        using var disconnectTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await ReceiveAsync(publisherSocket, disconnectTimeout.Token); // participant.disconnected

        // The documented full-reconnect flow calls POST /api/sessions/join before reopening the
        // WebSocket (SessionEndpoints.JoinAsync's existing-viewer branch sets Status back to
        // Connected without touching ConnectionId). Simulate that HTTP leg completing while the
        // client crashes before ever reopening the socket, so ConnectionId stays null.
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var participantRow = await db.SessionParticipants.SingleAsync(x => x.Id == viewer.ParticipantId);
            participantRow.Status = ParticipantStatuses.Connected;
            participantRow.LeftAt = null;
            await db.SaveChangesAsync();
        }

        // The grace timer must still finalize this participant once it expires — a null
        // ConnectionId is not a live socket that "claimed" the row.
        using var leftTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var left = await ReceiveAsync(publisherSocket, leftTimeout.Token);
        AssertEnvelope(left, "session.left", publisher.SessionId);
        Assert.Equal(viewer.ParticipantId, left.GetProperty("payload").GetProperty("participantId").GetGuid());

        await using var assertScope = factory.Services.CreateAsyncScope();
        var assertDb = assertScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var finalRow = await assertDb.SessionParticipants.SingleAsync(x => x.Id == viewer.ParticipantId);
        Assert.Equal(ParticipantStatuses.Disconnected, finalRow.Status);
    }

    [Theory]
    [InlineData("unsupported.type", "unsupported_message_type")]
    [InlineData("session.ended", "unsupported_message_type")]
    public async Task Signaling_returns_a_structured_error_for_unsupported_client_types(string type, string code)
    {
        var participant = await CreateParticipantAsync($"unsupported-{Guid.NewGuid():N}");
        using var socket = await ConnectAsync(participant);
        await ReceiveAsync(socket);

        await SendAsync(socket, new { type });

        var error = await ReceiveAsync(socket);
        AssertEnvelope(error, "error", participant.SessionId);
        Assert.Equal(participant.ParticipantId, error.GetProperty("to").GetGuid());
        Assert.Equal(code, error.GetProperty("payload").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Signaling_returns_a_structured_error_for_invalid_json()
    {
        var participant = await CreateParticipantAsync("invalid-json");
        using var socket = await ConnectAsync(participant);
        await ReceiveAsync(socket);

        await SendTextAsync(socket, "{not-json");

        var error = await ReceiveAsync(socket);
        AssertEnvelope(error, "error", participant.SessionId);
        Assert.Equal("invalid_message", error.GetProperty("payload").GetProperty("code").GetString());
    }

    [Theory]
    [InlineData(SessionStatuses.Ended, false)]
    [InlineData(SessionStatuses.Expired, false)]
    [InlineData(SessionStatuses.Active, true)]
    public async Task Signaling_rejects_terminal_or_elapsed_sessions(string status, bool elapsed)
    {
        var participant = await CreateParticipantAsync($"terminal-{status}-{elapsed}");
        await SetSessionStateAsync(participant.SessionId, status,
            elapsed ? DateTimeOffset.UtcNow.AddMinutes(-1) : DateTimeOffset.UtcNow.AddMinutes(5));
        var client = _factory.Server.CreateWebSocketClient();
        client.ConfigureRequest = request => request.Headers.Authorization = $"Bearer {participant.AccessToken}";

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => client.ConnectAsync(
            new Uri($"ws://localhost/ws/signaling?sessionId={participant.SessionId}"),
            CancellationToken.None));

        Assert.Contains("410", exception.Message);
    }

    [Fact]
    public async Task Signaling_does_not_route_a_frame_after_the_session_ends()
    {
        var participant = await CreateParticipantAsync("terminal-route");
        using var socket = await ConnectAsync(participant);
        await ReceiveAsync(socket);
        await SetSessionStateAsync(participant.SessionId, SessionStatuses.Ended, DateTimeOffset.UtcNow.AddMinutes(5));

        await SendAsync(socket, new
        {
            type = "webrtc.offer",
            to = participant.ParticipantId,
            payload = new { sdp = "must-not-route" }
        });

        var response = await ReceiveAsync(socket);
        Assert.Equal("session.ended", response.GetProperty("type").GetString());
        var buffer = new byte[64];
        var close = await socket.ReceiveAsync(buffer, CancellationToken.None);
        Assert.Equal(WebSocketMessageType.Close, close.MessageType);
    }

    [Fact]
    public async Task Signaling_rejects_reconnecting_with_a_token_from_a_device_revoked_after_the_first_connection()
    {
        var participant = await CreateParticipantAsync("revoked-mid-session");
        using (var firstSocket = await ConnectAsync(participant))
        {
            await ReceiveAsync(firstSocket);
        }
        await SetDeviceRevokedAsync(participant.DeviceId);
        var client = _factory.Server.CreateWebSocketClient();
        client.ConfigureRequest = request => request.Headers.Authorization = $"Bearer {participant.AccessToken}";

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => client.ConnectAsync(
            new Uri($"ws://localhost/ws/signaling?sessionId={participant.SessionId}"),
            CancellationToken.None));

        Assert.Contains("401", exception.Message);
    }

    private async Task<TestParticipant> CreateParticipantAsync(string prefix, SonicRelayApiFactory? factory = null)
    {
        factory ??= _factory;
        var http = factory.CreateClient();
        var session = await DeviceIdentityTestHelper.BootstrapAndAuthorizeAsync(
            http, DeviceTypes.WindowsPublisher, DevicePlatforms.Windows, $"{prefix} device");

        var sessionId = Guid.NewGuid();
        var participantId = Guid.NewGuid();
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.StreamSessions.Add(new StreamSession
        {
            Id = sessionId,
            SourceDeviceId = session.DeviceId,
            Status = SessionStatuses.Active,
            MaxViewers = 1,
            CodeExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
            CreatedAt = DateTimeOffset.UtcNow
        });
        db.SessionParticipants.Add(new SessionParticipant
        {
            Id = participantId,
            SessionId = sessionId,
            DeviceId = session.DeviceId,
            Role = ParticipantRoles.Publisher,
            Status = ParticipantStatuses.Connected,
            JoinedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
        return new TestParticipant(session.AccessToken, sessionId, session.DeviceId, participantId);
    }

    private async Task<TestParticipant> CreateViewerAsync(TestParticipant publisher, string prefix,
        SonicRelayApiFactory? factory = null)
    {
        factory ??= _factory;
        var http = factory.CreateClient();
        var session = await DeviceIdentityTestHelper.BootstrapAndAuthorizeAsync(
            http, DeviceTypes.FlutterViewer, DevicePlatforms.Android, $"{prefix} device");

        var participantId = Guid.NewGuid();
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.SessionParticipants.Add(new SessionParticipant
        {
            Id = participantId,
            SessionId = publisher.SessionId,
            DeviceId = session.DeviceId,
            Role = ParticipantRoles.Viewer,
            Status = ParticipantStatuses.Connected,
            JoinedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
        return new TestParticipant(session.AccessToken, publisher.SessionId, session.DeviceId, participantId);
    }

    private async Task<WebSocket> ConnectAsync(TestParticipant participant, SonicRelayApiFactory? factory = null)
    {
        var client = (factory ?? _factory).Server.CreateWebSocketClient();
        client.ConfigureRequest = request =>
            request.Headers.Authorization = $"Bearer {participant.AccessToken}";
        return await client.ConnectAsync(
            new Uri($"ws://localhost/ws/signaling?sessionId={participant.SessionId}"),
            CancellationToken.None);
    }

    private async Task SetSessionStateAsync(Guid sessionId, string status, DateTimeOffset codeExpiresAt)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var session = await db.StreamSessions.SingleAsync(x => x.Id == sessionId);
        session.Status = status;
        session.CodeExpiresAt = codeExpiresAt;
        await db.SaveChangesAsync();
    }

    private async Task SetDeviceRevokedAsync(Guid deviceId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var device = await db.DeviceIdentities.SingleAsync(x => x.Id == deviceId);
        device.Status = DeviceIdentityStatuses.Revoked;
        await db.SaveChangesAsync();
    }

    private static async Task SendAsync(WebSocket socket, object message)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message);
        await socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
    }

    private static async Task SendTextAsync(WebSocket socket, string message)
    {
        var bytes = Encoding.UTF8.GetBytes(message);
        await socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
    }

    private static void AssertEnvelope(JsonElement message, string type, Guid sessionId)
    {
        Assert.Equal(type, message.GetProperty("type").GetString());
        Assert.NotEqual(Guid.Empty, message.GetProperty("messageId").GetGuid());
        Assert.Equal(sessionId, message.GetProperty("sessionId").GetGuid());
        Assert.Equal(JsonValueKind.String, message.GetProperty("timestamp").ValueKind);
        Assert.True(message.TryGetProperty("from", out _));
        Assert.True(message.TryGetProperty("to", out _));
        Assert.True(message.TryGetProperty("payload", out _));
    }

    private static async Task<JsonElement> ReceiveAsync(WebSocket socket, CancellationToken ct = default)
    {
        var buffer = new byte[4096];
        var result = await socket.ReceiveAsync(buffer, ct);
        Assert.Equal(WebSocketMessageType.Text, result.MessageType);
        using var document = JsonDocument.Parse(buffer.AsMemory(0, result.Count));
        return document.RootElement.Clone();
    }

    private sealed record TestParticipant(string AccessToken, Guid SessionId, Guid DeviceId, Guid ParticipantId);
}
```

- [ ] **Step 3: Rewrite `WebRtcEndpointsTests.cs`**

```csharp
// tests/SonicRelay.Api.IntegrationTests/WebRtcEndpointsTests.cs
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SonicRelay.Domain.Devices;
using Xunit;

namespace SonicRelay.Api.IntegrationTests;

public sealed class WebRtcEndpointsTests : IClassFixture<SonicRelayApiFactory>
{
    private readonly SonicRelayApiFactory _factory;

    public WebRtcEndpointsTests(SonicRelayApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Ice_servers_requires_authentication()
    {
        var response = await _factory.CreateClient().GetAsync("/api/webrtc/ice-servers");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Ice_servers_returns_stun_only_when_turn_is_not_configured()
    {
        var (client, _) = await BootstrapAsync(_factory);

        var body = await GetIceServersAsync(client);

        var servers = body.GetProperty("iceServers").EnumerateArray().ToList();
        var entry = Assert.Single(servers);
        Assert.Equal("stun:stun.l.google.com:19302", entry.GetProperty("urls")[0].GetString());
        Assert.False(TryGetNonNull(entry, "username", out _));
        Assert.False(TryGetNonNull(entry, "credential", out _));
    }

    [Fact]
    public async Task Ice_servers_returns_turn_entry_with_coturn_rest_credentials()
    {
        const string secret = "integration-turn-secret";
        await using var factory = new SonicRelayApiFactory(new Dictionary<string, string?>
        {
            ["Turn:StaticAuthSecret"] = secret,
            ["Turn:TurnUris:0"] = "turn:relay.example.com:3478?transport=udp",
            ["Turn:TurnUris:1"] = "turns:relay.example.com:5349?transport=tcp",
            ["Turn:CredentialTtlSeconds"] = "600"
        });
        var (client, deviceId) = await BootstrapAsync(factory);
        var before = DateTimeOffset.UtcNow;

        var body = await GetIceServersAsync(client);

        Assert.Equal(600, body.GetProperty("ttlSeconds").GetInt32());
        var servers = body.GetProperty("iceServers").EnumerateArray().ToList();
        Assert.Equal(2, servers.Count);
        var turn = servers.Single(item => item.GetProperty("urls")[0].GetString()!.StartsWith("turn:", StringComparison.Ordinal));
        Assert.Equal("turns:relay.example.com:5349?transport=tcp", turn.GetProperty("urls")[1].GetString());

        var username = turn.GetProperty("username").GetString()!;
        var parts = username.Split(':', 2);
        var expiry = DateTimeOffset.FromUnixTimeSeconds(long.Parse(parts[0]));
        Assert.Equal(deviceId.ToString("D"), parts[1]);
        Assert.InRange(expiry, before.AddSeconds(600).AddSeconds(-30), before.AddSeconds(600).AddSeconds(30));

        var expected = Convert.ToBase64String(HMACSHA1.HashData(
            Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(username)));
        Assert.Equal(expected, turn.GetProperty("credential").GetString());
    }

    [Fact]
    public async Task Ice_servers_accepts_flat_environment_style_configuration()
    {
        await using var factory = new SonicRelayApiFactory(new Dictionary<string, string?>
        {
            ["TURN_STATIC_AUTH_SECRET"] = "flat-env-secret",
            ["TURN_URIS"] = "turn:relay.example.com:3478?transport=udp, turn:relay.example.com:3478?transport=tcp",
            ["TURN_CREDENTIAL_TTL_SECONDS"] = "1200"
        });
        var (client, _) = await BootstrapAsync(factory);

        var body = await GetIceServersAsync(client);

        Assert.Equal(1200, body.GetProperty("ttlSeconds").GetInt32());
        var turn = body.GetProperty("iceServers").EnumerateArray()
            .Single(item => item.GetProperty("urls")[0].GetString()!.StartsWith("turn:", StringComparison.Ordinal));
        Assert.Equal(2, turn.GetProperty("urls").GetArrayLength());
        Assert.Equal("turn:relay.example.com:3478?transport=tcp", turn.GetProperty("urls")[1].GetString());
        Assert.True(TryGetNonNull(turn, "credential", out _));
    }

    [Fact]
    public async Task Ice_servers_derives_turn_and_stun_uris_from_the_public_host()
    {
        await using var factory = new SonicRelayApiFactory(new Dictionary<string, string?>
        {
            ["TURN_STATIC_AUTH_SECRET"] = "derived-host-secret",
            ["TURN_PUBLIC_HOST"] = "turn.example.com"
        });
        var (client, _) = await BootstrapAsync(factory);

        var body = await GetIceServersAsync(client);

        var servers = body.GetProperty("iceServers").EnumerateArray().ToList();
        Assert.Equal(2, servers.Count);
        Assert.Equal("stun:turn.example.com:3478", servers[0].GetProperty("urls")[0].GetString());
        var turn = servers[1];
        Assert.Equal("turn:turn.example.com:3478?transport=udp", turn.GetProperty("urls")[0].GetString());
        Assert.Equal("turn:turn.example.com:3478?transport=tcp", turn.GetProperty("urls")[1].GetString());
        Assert.True(TryGetNonNull(turn, "username", out _));
        Assert.True(TryGetNonNull(turn, "credential", out _));
    }

    [Fact]
    public async Task Ice_servers_prefers_explicit_turn_uris_over_the_derived_ones()
    {
        await using var factory = new SonicRelayApiFactory(new Dictionary<string, string?>
        {
            ["TURN_STATIC_AUTH_SECRET"] = "explicit-over-derived",
            ["TURN_PUBLIC_HOST"] = "turn.example.com",
            ["TURN_URIS"] = "turns:turn.example.com:5349?transport=tcp",
            ["STUN_URIS"] = "stun:stun.example.com:3478"
        });
        var (client, _) = await BootstrapAsync(factory);

        var body = await GetIceServersAsync(client);

        var servers = body.GetProperty("iceServers").EnumerateArray().ToList();
        Assert.Equal("stun:stun.example.com:3478", servers[0].GetProperty("urls")[0].GetString());
        var turn = servers[1];
        Assert.Equal(1, turn.GetProperty("urls").GetArrayLength());
        Assert.Equal("turns:turn.example.com:5349?transport=tcp", turn.GetProperty("urls")[0].GetString());
    }

    private static async Task<JsonElement> GetIceServersAsync(HttpClient client)
    {
        var response = await client.GetAsync("/api/webrtc/ice-servers");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        return document.RootElement.Clone();
    }

    private static bool TryGetNonNull(JsonElement element, string property, out JsonElement value)
    {
        value = default;
        if (!element.TryGetProperty(property, out var found) || found.ValueKind == JsonValueKind.Null) return false;
        value = found;
        return true;
    }

    private static async Task<(HttpClient Client, Guid DeviceId)> BootstrapAsync(SonicRelayApiFactory factory)
    {
        var client = factory.CreateClient();
        var session = await DeviceIdentityTestHelper.BootstrapAndAuthorizeAsync(
            client, DeviceTypes.WindowsPublisher, DevicePlatforms.Windows);
        return (client, session.DeviceId);
    }
}
```

- [ ] **Step 4: Rewrite `WebRtcObservabilityTests.cs`**

```csharp
// tests/SonicRelay.Api.IntegrationTests/WebRtcObservabilityTests.cs
using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SonicRelay.Domain.Devices;
using SonicRelay.Domain.Sessions;
using SonicRelay.Infrastructure.Persistence;
using Xunit;

namespace SonicRelay.Api.IntegrationTests;

public sealed class WebRtcObservabilityTests : IClassFixture<SonicRelayApiFactory>
{
    private readonly SonicRelayApiFactory _factory;

    public WebRtcObservabilityTests(SonicRelayApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Metrics_endpoint_is_anonymous_and_exposes_sonicrelay_series()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/metrics");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("sonicrelay_signaling_connections_active", body);
        Assert.Contains("sonicrelay_sessions_active", body);
    }

    [Fact]
    public async Task Stats_requires_authentication()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/webrtc/stats", new { sessionId = Guid.NewGuid(), role = "viewer" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Stats_forbidden_for_non_participant()
    {
        var client = _factory.CreateClient();
        await DeviceIdentityTestHelper.BootstrapAndAuthorizeAsync(client, DeviceTypes.FlutterViewer, DevicePlatforms.Android);

        var response = await client.PostAsJsonAsync("/api/webrtc/stats", new
        {
            sessionId = Guid.NewGuid(),
            role = "viewer"
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Stats_from_participant_are_accepted_and_recorded()
    {
        var client = _factory.CreateClient();
        var session = await DeviceIdentityTestHelper.BootstrapAndAuthorizeAsync(
            client, DeviceTypes.FlutterViewer, DevicePlatforms.Android);
        var sessionId = await SeedParticipantAsync(session.DeviceId);

        var response = await client.PostAsJsonAsync("/api/webrtc/stats", new
        {
            sessionId,
            role = "viewer",
            iceConnectionState = "connected",
            selectedCandidatePair = new
            {
                localCandidateType = "relay",
                remoteCandidateType = "relay",
                protocol = "udp",
                relayProtocol = "udp"
            },
            inboundAudio = new { packetsReceived = 990, packetsLost = 10, jitter = 0.012 },
            candidatePair = new { currentRoundTripTime = 0.08 }
        });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var metrics = await client.GetStringAsync("/metrics");
        Assert.Contains("sonicrelay_session_transport_mode_total{mode=\"turn_udp\"}", metrics);
        Assert.Contains("sonicrelay_session_rtt_ms", metrics);
    }

    private async Task<Guid> SeedParticipantAsync(Guid deviceId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var sessionId = Guid.NewGuid();
        db.StreamSessions.Add(new StreamSession
        {
            Id = sessionId,
            SourceDeviceId = Guid.NewGuid(),
            Status = SessionStatuses.Active,
            MaxViewers = 3,
            CodeExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10),
            CreatedAt = DateTimeOffset.UtcNow
        });
        db.SessionParticipants.Add(new SessionParticipant
        {
            Id = Guid.NewGuid(),
            SessionId = sessionId,
            DeviceId = deviceId,
            Role = ParticipantRoles.Viewer,
            Status = ParticipantStatuses.Connected,
            JoinedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
        return sessionId;
    }
}
```

- [ ] **Step 5: Run the full integration test suite**

Run: `dotnet test tests/SonicRelay.Api.IntegrationTests`
Expected: all tests pass, 0 failures (this includes every test in the four rewritten files, Task 1's fixture test, every untouched Phase 1 device-identity/pairing test, `DeviceIdentityFeatureFlagTests`, `DeviceEndpointsTests`, `AuthEndpointsTests`, `AccountDeletionTests`, and `ParticipantReconnectTrackerTests`).

- [ ] **Step 6: Build the whole solution to confirm nothing else regressed**

Run: `dotnet build SonicRelay.sln`
Expected: `Build succeeded.` 0 warnings, 0 errors.

- [ ] **Step 7: Commit**

```bash
git add tests/SonicRelay.Api.IntegrationTests/SessionEndpointsTests.cs \
  tests/SonicRelay.Api.IntegrationTests/SignalingWebSocketTests.cs \
  tests/SonicRelay.Api.IntegrationTests/WebRtcEndpointsTests.cs \
  tests/SonicRelay.Api.IntegrationTests/WebRtcObservabilityTests.cs
git commit -m "Rewrite session, signaling, and WebRTC tests for device-identity auth"
```

---

### Task 4: Documentation

**Files:**
- Modify: `docs/device-identity.md`
- Modify: `docs/security.md`
- Modify: `docs/architecture.md`
- Modify: `README.md`

**Interfaces:**
- Consumes: nothing from earlier tasks beyond their already-committed behavior; this task only describes it.

- [ ] **Step 1: Update `docs/device-identity.md`**

Read the existing file first (it was created in Phase 1 and describes bootstrap/token/rotation/revocation/pairing). Append a new `## Sessions, signaling, and TURN (Phase 2)` section documenting:
- `session:create`/`session:join`/`session:end`/`signaling:connect`/`turn:credentials` scopes and which device type gets each.
- Session creation/joining no longer take a client-supplied device id — the caller's own authenticated device is always the actor.
- The WebSocket handshake now takes only `sessionId` as a query parameter; the device comes from the bearer token.
- The corrected, narrower meaning of `DeviceIdentity:Enabled`: it gates only bootstrap/token/rotate/revoke/pairing; sessions/signaling/TURN always require `DeviceBearer`.
- `create-session`/`join-session`/`rotate-code` rate limits are IP-keyed, same documented caveat as `pairing-create`/`pairing-complete` from Phase 1 (DeviceBearer tokens carry no claim a per-user limiter can key on).

- [ ] **Step 2: Update `docs/security.md`**

Find the existing "Device identity credentials (Phase 1 of issue #26)" subsection (added in Phase 1) and append a paragraph noting sessions, signaling, and TURN now authenticate exclusively via `DeviceBearer`; the old `ApplicationUser`-based session ownership path no longer exists. Note the `DeviceScopeAuthorizationHandler` live status/credential-version check now also protects the three read-only routes (`GET /api/sessions/active`, `GET /api/sessions/{id}`, `POST /api/webrtc/stats`) via a scope-less `DeviceAuthenticated` policy, not just capability-scoped ones.

- [ ] **Step 3: Update `docs/architecture.md`**

Find the Phase 1 ADR 0005 bullet and the Domain component description sentence added in Phase 1. Add one sentence noting `StreamSession`/`SessionParticipant` now reference `DeviceIdentity` rather than `ApplicationUser`/the old `Device` entity, and that the old `Device` entity is no longer part of the session/signaling/TURN path (kept only for its own unrelated CRUD feature, pending Phase 4 cleanup).

- [ ] **Step 4: Update `README.md`**

Find the `DeviceIdentity:*` configuration section added in Phase 1. Add a note that `DeviceIdentity:TokenSigningKey` (and the other `DeviceIdentity:*` keys) are effectively required in any real deployment now, since sessions/signaling/TURN have no fallback authentication path — `DeviceIdentity:Enabled=false` no longer provides a way to run the product without them.

- [ ] **Step 5: Commit**

```bash
git add docs/device-identity.md docs/security.md docs/architecture.md README.md
git commit -m "Document Phase 2 device-identity migration for sessions, signaling, and TURN"
```

---

## Post-plan verification (final task, whole-branch)

After Task 4, run the same verification sweep Phase 1 used before opening its PR:

```bash
dotnet build SonicRelay.sln
dotnet test tests/SonicRelay.Api.IntegrationTests
dotnet format --verify-no-changes
git diff --check
```

Expected: 0 build warnings/errors; all tests passing; `dotnet format` clean except the same 10 pre-existing, untouched `SignalingMessageTests.cs` errors Phase 1 already found and left out of scope (confirm via `git diff origin/main -- tests/SonicRelay.Api.IntegrationTests/SignalingMessageTests.cs` showing no changes); no whitespace errors from `git diff --check`.
