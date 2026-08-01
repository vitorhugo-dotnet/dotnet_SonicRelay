# Device Identity Auth (Phase 1) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add device-credential bootstrap, token issuance, rotation, revocation, and secure pairing as a new, additive authentication flow that runs in parallel to the existing ASP.NET Core Identity login, gated by a feature flag.

**Architecture:** New domain entities (`DeviceIdentity`, `PairingChallenge`, `DevicePairing`) live in their own `SonicRelay.Domain.DeviceIdentities` namespace, untouched by the existing owner-scoped `Device`/`StreamSession` model. A new `DeviceBearer` JWT scheme authenticates devices independently of `Identity.Bearer`. A custom authorization requirement re-checks device status/credential version against the database on every request so rotation and revocation take effect without a token blocklist.

**Tech Stack:** .NET 10, ASP.NET Core Minimal APIs, `Microsoft.AspNetCore.Authentication.JwtBearer`, EF Core/Npgsql, xUnit, `WebApplicationFactory` with EF Core InMemory.

## Global Constraints

- Do not modify `ApplicationUser`, `SonicRelay.Domain.Devices.Device`, `StreamSession`, `SessionParticipant`, signaling, or TURN credential code — Phase 2 of issue #26, not this plan.
- Never log a credential secret, pairing code, JWT, or QR payload — only IDs and outcomes (matches existing signaling-log convention in `docs/security.md`).
- Reuse `SonicRelay.Domain.Devices.DeviceTypes` / `DevicePlatforms` constants for device type/platform values instead of redefining them.
- All new tests run against EF Core InMemory + in-memory distributed cache via `SonicRelayApiFactory` — no Docker/Postgres/Redis required, matching the existing test convention.
- `DeviceIdentity:Enabled` (default `true`) must fully gate the `DeviceBearer` scheme registration and the new endpoint groups; setting it `false` removes this feature's HTTP surface with no other code changes.

---

### Task 1: Domain entities, persistence, and migration

**Files:**
- Create: `src/SonicRelay.Domain/DeviceIdentities/DeviceIdentity.cs`
- Create: `src/SonicRelay.Domain/DeviceIdentities/PairingChallenge.cs`
- Create: `src/SonicRelay.Domain/DeviceIdentities/DevicePairing.cs`
- Modify: `src/SonicRelay.Infrastructure/Persistence/AppDbContext.cs`
- Test: `tests/SonicRelay.Api.IntegrationTests/DeviceIdentityPersistenceTests.cs`
- Create (generated): `src/SonicRelay.Infrastructure/Persistence/Migrations/*AddDeviceIdentity*`

**Interfaces:**
- Produces: `DeviceIdentity { Id, Name, DeviceType, Platform, CredentialSecretHash, CredentialVersion, Status, CreatedAt, LastSeenAt, RevokedAt }`, `DeviceIdentityStatuses.Active`/`.Revoked`, `PairingChallenge { Id, PublisherDeviceId, CodeHash, ExpiresAt, MaxAttempts, AttemptCount, ConsumedAt, CreatedAt }`, `DevicePairing { Id, PublisherDeviceId, ViewerDeviceId, Status, CreatedAt, LastUsedAt, RevokedAt }`, `DevicePairingStatuses.Active`/`.Revoked`, and `AppDbContext.DeviceIdentities` / `.PairingChallenges` / `.DevicePairings`. All later tasks depend on these exact names.

- [ ] **Step 1: Write the failing persistence test**

```csharp
using Microsoft.EntityFrameworkCore;
using SonicRelay.Domain.DeviceIdentities;
using SonicRelay.Domain.Devices;
using SonicRelay.Infrastructure.Persistence;
using Xunit;

namespace SonicRelay.Api.IntegrationTests;

public sealed class DeviceIdentityPersistenceTests
{
    [Fact]
    public async Task RoundTrips_DeviceIdentity_PairingChallenge_And_DevicePairing()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"device-identity-persistence-{Guid.NewGuid()}")
            .Options;
        await using var db = new AppDbContext(options);

        var publisher = new DeviceIdentity
        {
            Id = Guid.NewGuid(),
            Name = "Living Room PC",
            DeviceType = DeviceTypes.WindowsPublisher,
            Platform = DevicePlatforms.Windows,
            CredentialSecretHash = "hash",
            CredentialVersion = 1,
            Status = DeviceIdentityStatuses.Active,
            CreatedAt = DateTimeOffset.UtcNow
        };
        var viewer = new DeviceIdentity
        {
            Id = Guid.NewGuid(),
            Name = "Phone",
            DeviceType = DeviceTypes.FlutterViewer,
            Platform = DevicePlatforms.Android,
            CredentialSecretHash = "hash2",
            CredentialVersion = 1,
            Status = DeviceIdentityStatuses.Active,
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.DeviceIdentities.AddRange(publisher, viewer);

        var challenge = new PairingChallenge
        {
            Id = Guid.NewGuid(),
            PublisherDeviceId = publisher.Id,
            CodeHash = "code-hash",
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
            MaxAttempts = 5,
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.PairingChallenges.Add(challenge);

        var pairing = new DevicePairing
        {
            Id = Guid.NewGuid(),
            PublisherDeviceId = publisher.Id,
            ViewerDeviceId = viewer.Id,
            Status = DevicePairingStatuses.Active,
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.DevicePairings.Add(pairing);

        await db.SaveChangesAsync();

        Assert.Equal(2, await db.DeviceIdentities.CountAsync());
        Assert.Equal(1, await db.PairingChallenges.CountAsync());
        Assert.Equal(1, await db.DevicePairings.CountAsync());
    }
}
```

- [ ] **Step 2: Run the test and verify it fails to compile**

Run: `dotnet test tests/SonicRelay.Api.IntegrationTests/SonicRelay.Api.IntegrationTests.csproj --filter FullyQualifiedName~DeviceIdentityPersistenceTests`
Expected: build error — `DeviceIdentity`, `PairingChallenge`, `DevicePairing`, and the new `DbSet`s do not exist yet.

- [ ] **Step 3: Add the domain entities**

`src/SonicRelay.Domain/DeviceIdentities/DeviceIdentity.cs`:

```csharp
namespace SonicRelay.Domain.DeviceIdentities;

public sealed class DeviceIdentity
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string DeviceType { get; set; } = Devices.DeviceTypes.FlutterViewer;
    public string Platform { get; set; } = Devices.DevicePlatforms.Android;
    public string CredentialSecretHash { get; set; } = string.Empty;
    public int CredentialVersion { get; set; } = 1;
    public string Status { get; set; } = DeviceIdentityStatuses.Active;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastSeenAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}

public static class DeviceIdentityStatuses
{
    public const string Active = "active";
    public const string Revoked = "revoked";
}
```

`src/SonicRelay.Domain/DeviceIdentities/PairingChallenge.cs`:

```csharp
namespace SonicRelay.Domain.DeviceIdentities;

public sealed class PairingChallenge
{
    public Guid Id { get; set; }
    public Guid PublisherDeviceId { get; set; }
    public string CodeHash { get; set; } = string.Empty;
    public DateTimeOffset ExpiresAt { get; set; }
    public int MaxAttempts { get; set; }
    public int AttemptCount { get; set; }
    public DateTimeOffset? ConsumedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
```

`src/SonicRelay.Domain/DeviceIdentities/DevicePairing.cs`:

```csharp
namespace SonicRelay.Domain.DeviceIdentities;

public sealed class DevicePairing
{
    public Guid Id { get; set; }
    public Guid PublisherDeviceId { get; set; }
    public Guid ViewerDeviceId { get; set; }
    public string Status { get; set; } = DevicePairingStatuses.Active;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}

public static class DevicePairingStatuses
{
    public const string Active = "active";
    public const string Revoked = "revoked";
}
```

- [ ] **Step 4: Wire the new entities into `AppDbContext`**

In `src/SonicRelay.Infrastructure/Persistence/AppDbContext.cs`, add `using SonicRelay.Domain.DeviceIdentities;` and three `DbSet` properties next to the existing ones:

```csharp
public DbSet<DeviceIdentity> DeviceIdentities => Set<DeviceIdentity>();
public DbSet<PairingChallenge> PairingChallenges => Set<PairingChallenge>();
public DbSet<DevicePairing> DevicePairings => Set<DevicePairing>();
```

Add to `OnModelCreating`, after the existing `SignalingEvent` block:

```csharp
modelBuilder.Entity<DeviceIdentity>(entity =>
{
    entity.ToTable("device_identities");
    entity.HasKey(x => x.Id);
    entity.Property(x => x.Name).HasMaxLength(120).IsRequired();
    entity.Property(x => x.DeviceType).HasMaxLength(40).IsRequired();
    entity.Property(x => x.Platform).HasMaxLength(40).IsRequired();
    entity.Property(x => x.CredentialSecretHash).HasMaxLength(128).IsRequired();
    entity.Property(x => x.Status).HasMaxLength(16).IsRequired();
    entity.HasIndex(x => x.Status).HasDatabaseName("ix_device_identities_status");
});

modelBuilder.Entity<PairingChallenge>(entity =>
{
    entity.ToTable("pairing_challenges");
    entity.HasKey(x => x.Id);
    entity.Property(x => x.CodeHash).HasMaxLength(128).IsRequired();
    entity.HasIndex(x => x.PublisherDeviceId).HasDatabaseName("ix_pairing_challenges_publisher_device_id");
    entity.HasIndex(x => x.ExpiresAt).HasDatabaseName("ix_pairing_challenges_expires_at");
});

modelBuilder.Entity<DevicePairing>(entity =>
{
    entity.ToTable("device_pairings");
    entity.HasKey(x => x.Id);
    entity.Property(x => x.Status).HasMaxLength(16).IsRequired();
    entity.HasIndex(x => x.PublisherDeviceId).HasDatabaseName("ix_device_pairings_publisher_device_id");
    entity.HasIndex(x => x.ViewerDeviceId).HasDatabaseName("ix_device_pairings_viewer_device_id");
});
```

- [ ] **Step 5: Run the test and verify it passes**

Run: `dotnet test tests/SonicRelay.Api.IntegrationTests/SonicRelay.Api.IntegrationTests.csproj --filter FullyQualifiedName~DeviceIdentityPersistenceTests`
Expected: PASS.

- [ ] **Step 6: Generate the EF Core migration**

Install the EF tool if `dotnet ef` is not already on `PATH`:

Run: `dotnet tool install --global dotnet-ef` (skip if it prints "already installed"; run `export PATH="$PATH:$HOME/.dotnet/tools"` if the command isn't found afterward).

Run:
```bash
dotnet ef migrations add AddDeviceIdentity \
  --project src/SonicRelay.Infrastructure/SonicRelay.Infrastructure.csproj \
  --startup-project services/SonicRelay.Api/SonicRelay.Api.csproj \
  --output-dir Persistence/Migrations
```

Expected: a new `Migrations/*_AddDeviceIdentity.cs` + `.Designer.cs` and an updated `AppDbContextModelSnapshot.cs` appear under `src/SonicRelay.Infrastructure/Persistence/Migrations/`, creating `device_identities`, `pairing_challenges`, and `device_pairings`. This command uses `AppDbContextFactory`'s design-time connection string and does not require a reachable database.

- [ ] **Step 7: Commit**

```bash
git add src/SonicRelay.Domain/DeviceIdentities src/SonicRelay.Infrastructure/Persistence tests/SonicRelay.Api.IntegrationTests/DeviceIdentityPersistenceTests.cs
git commit -m "Add DeviceIdentity, PairingChallenge, and DevicePairing entities"
```

---

### Task 2: Device credential hashing service

**Files:**
- Create: `services/SonicRelay.Api/Services/DeviceIdentityOptions.cs`
- Create: `services/SonicRelay.Api/Services/DeviceCredentialService.cs`
- Test: `tests/SonicRelay.Api.IntegrationTests/DeviceCredentialServiceTests.cs`

**Interfaces:**
- Consumes: `DeviceIdentity.DeviceType`, `DeviceTypes.WindowsPublisher`/`.FlutterViewer` (Task 1 / existing `Devices.Device`).
- Produces: `DeviceIdentityOptions { Enabled, CredentialHmacKey, PairingCodeHmacKey, TokenSigningKey, Issuer, Audience, AccessTokenMinutes, PairingCodeTtlMinutes, PairingMaxAttempts }` and `DeviceCredentialService.GenerateCredential()`, `.HashSecret(string)`, `.VerifySecret(string,string)`, `.ScopesFor(string deviceType)` (static) — used by Tasks 3, 4, and 5.

- [ ] **Step 1: Write the failing unit test**

```csharp
using Microsoft.Extensions.Options;
using SonicRelay.Api.Services;
using SonicRelay.Domain.Devices;
using Xunit;

namespace SonicRelay.Api.IntegrationTests;

public sealed class DeviceCredentialServiceTests
{
    private static DeviceCredentialService CreateService() => new(
        Options.Create(new DeviceIdentityOptions
        {
            CredentialHmacKey = "unit-test-credential-hmac-key",
            TokenSigningKey = "unit-test-token-signing-key-needs-32-bytes-min"
        }),
        TimeProvider.System);

    [Fact]
    public void GenerateCredential_Produces_DistinctSecrets_And_VerifiableHash()
    {
        var service = CreateService();
        var (secretA, hashA) = service.GenerateCredential();
        var (secretB, _) = service.GenerateCredential();

        Assert.NotEqual(secretA, secretB);
        Assert.True(service.VerifySecret(secretA, hashA));
    }

    [Fact]
    public void VerifySecret_Rejects_WrongSecret()
    {
        var service = CreateService();
        var (_, hash) = service.GenerateCredential();

        Assert.False(service.VerifySecret("not-the-secret", hash));
    }

    [Theory]
    [InlineData(DeviceTypes.WindowsPublisher, "pairing:create")]
    [InlineData(DeviceTypes.FlutterViewer, "pairing:complete")]
    public void ScopesFor_Grants_TypeSpecificScope(string deviceType, string expectedScope)
    {
        var scopes = DeviceCredentialService.ScopesFor(deviceType);

        Assert.Contains(expectedScope, scopes);
        Assert.Contains("device:read", scopes);
        Assert.Contains("device:manage", scopes);
    }
}
```

- [ ] **Step 2: Run and verify it fails to compile**

Run: `dotnet test tests/SonicRelay.Api.IntegrationTests/SonicRelay.Api.IntegrationTests.csproj --filter FullyQualifiedName~DeviceCredentialServiceTests`
Expected: build error — `DeviceIdentityOptions`/`DeviceCredentialService` do not exist.

- [ ] **Step 3: Implement `DeviceIdentityOptions`**

```csharp
namespace SonicRelay.Api.Services;

public sealed class DeviceIdentityOptions
{
    public bool Enabled { get; set; } = true;
    public string? CredentialHmacKey { get; set; }
    public string? PairingCodeHmacKey { get; set; }
    public string? TokenSigningKey { get; set; }
    public string Issuer { get; set; } = "sonicrelay";
    public string Audience { get; set; } = "sonicrelay-devices";
    public int AccessTokenMinutes { get; set; } = 5;
    public int PairingCodeTtlMinutes { get; set; } = 5;
    public int PairingMaxAttempts { get; set; } = 5;
}
```

- [ ] **Step 4: Implement `DeviceCredentialService`**

```csharp
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using SonicRelay.Domain.Devices;

namespace SonicRelay.Api.Services;

public sealed class DeviceCredentialService(IOptions<DeviceIdentityOptions> options, TimeProvider time)
{
    public (string PlaintextSecret, string SecretHash) GenerateCredential()
    {
        var plaintext = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        return (plaintext, HashSecret(plaintext));
    }

    public string HashSecret(string plaintextSecret)
    {
        var key = RequireKey(options.Value.CredentialHmacKey, nameof(DeviceIdentityOptions.CredentialHmacKey));
        var hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(plaintextSecret));
        return Convert.ToHexString(hash);
    }

    public bool VerifySecret(string plaintextSecret, string secretHash)
    {
        var computed = Convert.FromHexString(HashSecret(plaintextSecret));
        var expected = Convert.FromHexString(secretHash);
        return CryptographicOperations.FixedTimeEquals(computed, expected);
    }

    public static IReadOnlyList<string> ScopesFor(string deviceType) => deviceType switch
    {
        DeviceTypes.WindowsPublisher => ["device:read", "device:manage", "pairing:create", "pairing:revoke"],
        DeviceTypes.FlutterViewer => ["device:read", "device:manage", "pairing:complete", "pairing:revoke"],
        _ => []
    };

    internal static string RequireKey(string? value, string name) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException($"DeviceIdentity:{name} must be configured.")
            : value;
}
```

- [ ] **Step 5: Run and verify it passes**

Run: `dotnet test tests/SonicRelay.Api.IntegrationTests/SonicRelay.Api.IntegrationTests.csproj --filter FullyQualifiedName~DeviceCredentialServiceTests`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add services/SonicRelay.Api/Services/DeviceIdentityOptions.cs services/SonicRelay.Api/Services/DeviceCredentialService.cs tests/SonicRelay.Api.IntegrationTests/DeviceCredentialServiceTests.cs
git commit -m "Add device credential hashing and scope service"
```

---

### Task 3: Bootstrap and token endpoints with the DeviceBearer JWT scheme

**Files:**
- Modify: `services/SonicRelay.Api/SonicRelay.Api.csproj`
- Create: `services/SonicRelay.Api/Contracts/DeviceIdentityContracts.cs`
- Create: `services/SonicRelay.Api/Endpoints/DeviceIdentityEndpoints.cs`
- Modify: `services/SonicRelay.Api/Services/DeviceCredentialService.cs`
- Modify: `services/SonicRelay.Api/Program.cs`
- Modify: `tests/SonicRelay.Api.IntegrationTests/SonicRelayApiFactory.cs`
- Test: `tests/SonicRelay.Api.IntegrationTests/DeviceBootstrapAndTokenTests.cs`

**Interfaces:**
- Consumes: `DeviceIdentity`, `DeviceIdentityStatuses` (Task 1); `DeviceIdentityOptions`, `DeviceCredentialService.GenerateCredential/VerifySecret/ScopesFor` (Task 2).
- Produces: `DeviceCredentialService.IssueAccessToken(DeviceIdentity)`, `POST /api/devices/bootstrap`, `POST /api/devices/token`, and the `"DeviceBearer"` authentication scheme name — consumed by Tasks 4–6.

- [ ] **Step 1: Add the JWT bearer package**

In `services/SonicRelay.Api/SonicRelay.Api.csproj`, add next to the existing `Microsoft.EntityFrameworkCore.Design` reference:

```xml
<PackageReference Include="Microsoft.AspNetCore.Authentication.JwtBearer" Version="10.0.0" />
```

Run: `dotnet restore services/SonicRelay.Api/SonicRelay.Api.csproj`
Expected: restores successfully.

- [ ] **Step 2: Write the failing integration test**

```csharp
using System.Net;
using System.Net.Http.Json;
using SonicRelay.Api.Contracts;
using Xunit;

namespace SonicRelay.Api.IntegrationTests;

public sealed class DeviceBootstrapAndTokenTests : IClassFixture<SonicRelayApiFactory>
{
    private readonly HttpClient _client;

    public DeviceBootstrapAndTokenTests(SonicRelayApiFactory factory) => _client = factory.CreateClient();

    [Fact]
    public async Task Bootstrap_Then_Token_Issues_AccessToken_With_TypeScopes()
    {
        var bootstrapResponse = await _client.PostAsJsonAsync("/api/devices/bootstrap",
            new BootstrapDeviceRequest("Living Room PC", "windows_publisher", "windows"));
        Assert.Equal(HttpStatusCode.Created, bootstrapResponse.StatusCode);
        var bootstrap = await bootstrapResponse.Content.ReadFromJsonAsync<BootstrapDeviceResponse>();
        Assert.NotNull(bootstrap);

        var tokenResponse = await _client.PostAsJsonAsync("/api/devices/token",
            new DeviceTokenRequest(bootstrap!.DeviceId, bootstrap.CredentialSecret));
        Assert.Equal(HttpStatusCode.OK, tokenResponse.StatusCode);
        var token = await tokenResponse.Content.ReadFromJsonAsync<DeviceTokenResponse>();
        Assert.NotNull(token);
        Assert.False(string.IsNullOrWhiteSpace(token!.AccessToken));
        Assert.Contains("pairing:create", token.Scopes);
    }

    [Fact]
    public async Task Token_Rejects_WrongSecret()
    {
        var bootstrapResponse = await _client.PostAsJsonAsync("/api/devices/bootstrap",
            new BootstrapDeviceRequest("Phone", "flutter_viewer", "android"));
        var bootstrap = await bootstrapResponse.Content.ReadFromJsonAsync<BootstrapDeviceResponse>();

        var tokenResponse = await _client.PostAsJsonAsync("/api/devices/token",
            new DeviceTokenRequest(bootstrap!.DeviceId, "wrong-secret"));

        Assert.Equal(HttpStatusCode.Unauthorized, tokenResponse.StatusCode);
    }

    [Fact]
    public async Task Token_Rejects_UnknownDevice_With_SameStatusCode_As_WrongSecret()
    {
        var response = await _client.PostAsJsonAsync("/api/devices/token",
            new DeviceTokenRequest(Guid.NewGuid(), "does-not-matter"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Bootstrap_Rejects_Invalid_TypePlatform_Combination()
    {
        var response = await _client.PostAsJsonAsync("/api/devices/bootstrap",
            new BootstrapDeviceRequest("Bad", "windows_publisher", "android"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
```

- [ ] **Step 3: Run and verify it fails**

Run: `dotnet test tests/SonicRelay.Api.IntegrationTests/SonicRelay.Api.IntegrationTests.csproj --filter FullyQualifiedName~DeviceBootstrapAndTokenTests`
Expected: compile failure (contracts/endpoints do not exist yet).

- [ ] **Step 4: Add contracts**

`services/SonicRelay.Api/Contracts/DeviceIdentityContracts.cs`:

```csharp
namespace SonicRelay.Api.Contracts;

public sealed record BootstrapDeviceRequest(string? Name, string? DeviceType, string? Platform);

public sealed record BootstrapDeviceResponse(Guid DeviceId, string CredentialSecret, int CredentialVersion);

public sealed record DeviceTokenRequest(Guid DeviceId, string CredentialSecret);

public sealed record DeviceTokenResponse(string AccessToken, DateTimeOffset ExpiresAt, IReadOnlyList<string> Scopes);
```

- [ ] **Step 5: Add `IssueAccessToken` to `DeviceCredentialService`**

Add `using System.Globalization; using System.IdentityModel.Tokens.Jwt; using System.Security.Claims; using Microsoft.IdentityModel.Tokens; using SonicRelay.Domain.DeviceIdentities;` and this method to the class from Task 2:

```csharp
public (string AccessToken, DateTimeOffset ExpiresAt) IssueAccessToken(DeviceIdentity device)
{
    var settings = options.Value;
    var key = RequireKey(settings.TokenSigningKey, nameof(DeviceIdentityOptions.TokenSigningKey));
    var now = time.GetUtcNow();
    var expiresAt = now.AddMinutes(settings.AccessTokenMinutes);
    var scopes = string.Join(' ', ScopesFor(device.DeviceType));

    var claims = new[]
    {
        new Claim(JwtRegisteredClaimNames.Sub, device.Id.ToString()),
        new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
        new Claim("device_type", device.DeviceType),
        new Claim("scope", scopes),
        new Claim("cv", device.CredentialVersion.ToString(CultureInfo.InvariantCulture))
    };
    var credentials = new SigningCredentials(
        new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)), SecurityAlgorithms.HmacSha256);
    var token = new JwtSecurityToken(
        settings.Issuer, settings.Audience, claims, now.UtcDateTime, expiresAt.UtcDateTime, credentials);

    return (new JwtSecurityTokenHandler().WriteToken(token), expiresAt);
}
```

- [ ] **Step 6: Add the endpoints**

`services/SonicRelay.Api/Endpoints/DeviceIdentityEndpoints.cs`:

```csharp
using System.Security.Claims;
using System.IdentityModel.Tokens.Jwt;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using SonicRelay.Api.Contracts;
using SonicRelay.Api.Services;
using SonicRelay.Domain.DeviceIdentities;
using SonicRelay.Domain.Devices;
using SonicRelay.Infrastructure.Persistence;

namespace SonicRelay.Api.Endpoints;

public static class DeviceIdentityEndpoints
{
    public static IEndpointRouteBuilder MapDeviceIdentityEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/devices").WithTags("DeviceIdentity");
        group.MapPost("/bootstrap", BootstrapAsync).RequireRateLimiting("device-bootstrap");
        group.MapPost("/token", TokenAsync).RequireRateLimiting("device-token");
        return app;
    }

    private static async Task<IResult> BootstrapAsync(BootstrapDeviceRequest request,
        DeviceCredentialService credentials, AppDbContext db, TimeProvider time, CancellationToken ct)
    {
        if (!ValidName(request.Name) || !ValidTypePlatform(request.DeviceType, request.Platform))
            return Results.BadRequest(new { error = "Invalid device name, type, or platform." });

        var (plaintext, hash) = credentials.GenerateCredential();
        var device = new DeviceIdentity
        {
            Id = Guid.NewGuid(),
            Name = request.Name!.Trim(),
            DeviceType = request.DeviceType!,
            Platform = request.Platform!,
            CredentialSecretHash = hash,
            CredentialVersion = 1,
            Status = DeviceIdentityStatuses.Active,
            CreatedAt = time.GetUtcNow()
        };
        db.DeviceIdentities.Add(device);
        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/devices/{device.Id}",
            new BootstrapDeviceResponse(device.Id, plaintext, device.CredentialVersion));
    }

    private static async Task<IResult> TokenAsync(DeviceTokenRequest request,
        DeviceCredentialService credentials, AppDbContext db, TimeProvider time, CancellationToken ct)
    {
        var device = await db.DeviceIdentities.SingleOrDefaultAsync(x => x.Id == request.DeviceId, ct);
        if (device is null || device.Status != DeviceIdentityStatuses.Active
            || !credentials.VerifySecret(request.CredentialSecret ?? string.Empty, device.CredentialSecretHash))
        {
            return Results.Unauthorized();
        }

        device.LastSeenAt = time.GetUtcNow();
        await db.SaveChangesAsync(ct);
        var (token, expiresAt) = credentials.IssueAccessToken(device);
        return Results.Ok(new DeviceTokenResponse(token, expiresAt, DeviceCredentialService.ScopesFor(device.DeviceType)));
    }

    internal static async Task<DeviceIdentity?> RequireDeviceAsync(
        ClaimsPrincipal principal, AppDbContext db, CancellationToken ct)
    {
        if (!Guid.TryParse(principal.FindFirstValue(JwtRegisteredClaimNames.Sub), out var deviceId)) return null;
        return await db.DeviceIdentities.SingleOrDefaultAsync(x => x.Id == deviceId, ct);
    }

    private static bool ValidName(string? name) => !string.IsNullOrWhiteSpace(name) && name.Trim().Length <= 120;

    private static bool ValidTypePlatform(string? type, string? platform) =>
        (type == DeviceTypes.WindowsPublisher && platform == DevicePlatforms.Windows)
        || (type == DeviceTypes.FlutterViewer && platform is DevicePlatforms.Android or DevicePlatforms.Ios);
}
```

- [ ] **Step 7: Wire configuration, the JWT scheme, rate limits, and the feature flag in `Program.cs`**

Add three `using` directives near the top of `Program.cs`: `using System.Text;`, `using Microsoft.AspNetCore.Authentication.JwtBearer;`, and `using Microsoft.IdentityModel.Tokens;`.

Immediately after `var builder = WebApplication.CreateBuilder(args);`, add:

```csharp
var deviceIdentityEnabled = builder.Configuration.GetValue("DeviceIdentity:Enabled", true);
```

After the existing `builder.Services.AddScoped<AccountDeletionService>();` line, add:

```csharp
builder.Services.Configure<DeviceIdentityOptions>(builder.Configuration.GetSection("DeviceIdentity"));
builder.Services.AddSingleton<DeviceCredentialService>();
if (deviceIdentityEnabled)
{
    builder.Services.AddAuthentication().AddJwtBearer("DeviceBearer", jwtOptions =>
    {
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
}
```

In the existing `builder.Services.AddRateLimiter(options => { ... })` block, after the `"rotate-code"` policy, add:

```csharp
options.AddPolicy("device-bootstrap", context => IpLimit(context, "RateLimits:DeviceBootstrap", 10));
options.AddPolicy("device-token", context => IpLimit(context, "RateLimits:DeviceToken", 10));
```

After `app.MapDeviceEndpoints();`, add:

```csharp
if (deviceIdentityEnabled)
{
    app.MapDeviceIdentityEndpoints();
}
```

- [ ] **Step 8: Add test configuration**

In `tests/SonicRelay.Api.IntegrationTests/SonicRelayApiFactory.cs`, after the existing `RateLimits:RotateCode:PermitLimit` line, add:

```csharp
builder.UseSetting("DeviceIdentity:CredentialHmacKey", "integration-test-device-credential-key");
builder.UseSetting("DeviceIdentity:PairingCodeHmacKey", "integration-test-pairing-code-key");
builder.UseSetting("DeviceIdentity:TokenSigningKey", "integration-test-device-token-signing-key-32bytes-min");
builder.UseSetting("RateLimits:DeviceBootstrap:PermitLimit", "100");
builder.UseSetting("RateLimits:DeviceToken:PermitLimit", "100");
```

- [ ] **Step 9: Run and verify it passes**

Run: `dotnet test tests/SonicRelay.Api.IntegrationTests/SonicRelay.Api.IntegrationTests.csproj --filter FullyQualifiedName~DeviceBootstrapAndTokenTests`
Expected: PASS.

- [ ] **Step 10: Commit**

```bash
git add services/SonicRelay.Api tests/SonicRelay.Api.IntegrationTests/DeviceBootstrapAndTokenTests.cs tests/SonicRelay.Api.IntegrationTests/SonicRelayApiFactory.cs
git commit -m "Add device bootstrap/token endpoints and the DeviceBearer JWT scheme"
```

---

### Task 4: Scope authorization, rotate-credential, and revoke

**Files:**
- Create: `services/SonicRelay.Api/Authorization/DeviceScopeRequirement.cs`
- Create: `services/SonicRelay.Api/Authorization/DeviceScopeAuthorizationHandler.cs`
- Modify: `services/SonicRelay.Api/Contracts/DeviceIdentityContracts.cs`
- Modify: `services/SonicRelay.Api/Endpoints/DeviceIdentityEndpoints.cs`
- Modify: `services/SonicRelay.Api/Program.cs`
- Test: `tests/SonicRelay.Api.IntegrationTests/DeviceCredentialLifecycleTests.cs`

**Interfaces:**
- Consumes: `DeviceIdentityEndpoints.RequireDeviceAsync` (Task 3), `DeviceCredentialService.GenerateCredential/VerifySecret` (Task 2).
- Produces: authorization policies `"device:read"`, `"device:manage"`, `"pairing:create"`, `"pairing:complete"`, `"pairing:revoke"` on the `DeviceBearer` scheme — consumed by Tasks 5 and 6.

- [ ] **Step 1: Write the failing integration test**

```csharp
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using SonicRelay.Api.Contracts;
using Xunit;

namespace SonicRelay.Api.IntegrationTests;

public sealed class DeviceCredentialLifecycleTests : IClassFixture<SonicRelayApiFactory>
{
    private readonly HttpClient _client;

    public DeviceCredentialLifecycleTests(SonicRelayApiFactory factory) => _client = factory.CreateClient();

    private async Task<(Guid DeviceId, string Secret, string AccessToken)> BootstrapAndAuthenticateAsync(
        string type, string platform)
    {
        var bootstrap = await (await _client.PostAsJsonAsync("/api/devices/bootstrap",
            new BootstrapDeviceRequest("Device", type, platform)))
            .Content.ReadFromJsonAsync<BootstrapDeviceResponse>();
        var token = await (await _client.PostAsJsonAsync("/api/devices/token",
            new DeviceTokenRequest(bootstrap!.DeviceId, bootstrap.CredentialSecret)))
            .Content.ReadFromJsonAsync<DeviceTokenResponse>();
        return (bootstrap.DeviceId, bootstrap.CredentialSecret, token!.AccessToken);
    }

    [Fact]
    public async Task RotateCredential_Invalidates_PreviousToken()
    {
        var (deviceId, secret, accessToken) = await BootstrapAndAuthenticateAsync("windows_publisher", "windows");

        using var rotateRequest = new HttpRequestMessage(HttpMethod.Post, "/api/devices/rotate-credential")
        {
            Content = JsonContent.Create(new RotateCredentialRequest(secret))
        };
        rotateRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        var rotateResponse = await _client.SendAsync(rotateRequest);
        Assert.Equal(HttpStatusCode.OK, rotateResponse.StatusCode);

        using var staleTokenRequest = new HttpRequestMessage(HttpMethod.Post, "/api/devices/revoke");
        staleTokenRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        var staleResponse = await _client.SendAsync(staleTokenRequest);
        Assert.Equal(HttpStatusCode.Forbidden, staleResponse.StatusCode);

        var newSecret = await rotateResponse.Content.ReadFromJsonAsync<RotateCredentialResponse>();
        var newToken = await (await _client.PostAsJsonAsync("/api/devices/token",
            new DeviceTokenRequest(deviceId, newSecret!.CredentialSecret)))
            .Content.ReadFromJsonAsync<DeviceTokenResponse>();
        Assert.NotNull(newToken);
    }

    [Fact]
    public async Task Revoke_Blocks_FutureTokenRequests_And_AlreadyIssuedTokens()
    {
        var (deviceId, secret, accessToken) = await BootstrapAndAuthenticateAsync("flutter_viewer", "android");

        using var revokeRequest = new HttpRequestMessage(HttpMethod.Post, "/api/devices/revoke");
        revokeRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        var revokeResponse = await _client.SendAsync(revokeRequest);
        Assert.Equal(HttpStatusCode.NoContent, revokeResponse.StatusCode);

        var tokenAfterRevoke = await _client.PostAsJsonAsync("/api/devices/token",
            new DeviceTokenRequest(deviceId, secret));
        Assert.Equal(HttpStatusCode.Unauthorized, tokenAfterRevoke.StatusCode);

        using var staleTokenRequest = new HttpRequestMessage(HttpMethod.Post, "/api/devices/revoke");
        staleTokenRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        var staleResponse = await _client.SendAsync(staleTokenRequest);
        Assert.Equal(HttpStatusCode.Forbidden, staleResponse.StatusCode);
    }

    [Fact]
    public async Task RotateCredential_Requires_CurrentSecret()
    {
        var (_, _, accessToken) = await BootstrapAndAuthenticateAsync("windows_publisher", "windows");

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/devices/rotate-credential")
        {
            Content = JsonContent.Create(new RotateCredentialRequest("wrong-secret"))
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
```

- [ ] **Step 2: Run and verify it fails**

Run: `dotnet test tests/SonicRelay.Api.IntegrationTests/SonicRelay.Api.IntegrationTests.csproj --filter FullyQualifiedName~DeviceCredentialLifecycleTests`
Expected: compile/route failures — `RotateCredentialRequest`/`RotateCredentialResponse`/routes/policies do not exist yet, and unauthenticated `DeviceBearer` requests currently 401 or 404 rather than exercising scope checks.

- [ ] **Step 3: Add rotate contracts**

Append to `services/SonicRelay.Api/Contracts/DeviceIdentityContracts.cs`:

```csharp
public sealed record RotateCredentialRequest(string CurrentCredentialSecret);

public sealed record RotateCredentialResponse(string CredentialSecret, int CredentialVersion);
```

- [ ] **Step 4: Implement the scope requirement and handler**

`services/SonicRelay.Api/Authorization/DeviceScopeRequirement.cs`:

```csharp
using Microsoft.AspNetCore.Authorization;

namespace SonicRelay.Api.Authorization;

public sealed class DeviceScopeRequirement(string scope) : IAuthorizationRequirement
{
    public string Scope { get; } = scope;
}
```

`services/SonicRelay.Api/Authorization/DeviceScopeAuthorizationHandler.cs`:

```csharp
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
        var scopes = context.User.FindFirstValue("scope")?.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            ?? [];
        if (!scopes.Contains(requirement.Scope)) return;

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

`context.User.FindFirstValue` needs `using System.Security.Claims;` — add it to the handler file.

- [ ] **Step 5: Register policies and the handler in `Program.cs`**

Add `using SonicRelay.Api.Authorization; using Microsoft.AspNetCore.Authorization;` near the top.

Inside the existing `builder.Services.AddAuthorization(options => { ... })` block, after the `"AdminOnly"` policy, add:

```csharp
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
```

Register the handler next to `builder.Services.AddSingleton<DeviceCredentialService>();`:

```csharp
builder.Services.AddScoped<IAuthorizationHandler, DeviceScopeAuthorizationHandler>();
```

- [ ] **Step 6: Add rotate-credential and revoke endpoints**

In `services/SonicRelay.Api/Endpoints/DeviceIdentityEndpoints.cs`, add to the route group in `MapDeviceIdentityEndpoints`:

```csharp
group.MapPost("/rotate-credential", RotateAsync).RequireAuthorization("device:manage");
group.MapPost("/revoke", RevokeAsync).RequireAuthorization("device:manage");
```

Add the handlers:

```csharp
private static async Task<IResult> RotateAsync(RotateCredentialRequest request, ClaimsPrincipal principal,
    DeviceCredentialService credentials, AppDbContext db, CancellationToken ct)
{
    var device = await RequireDeviceAsync(principal, db, ct);
    if (device is null) return Results.Unauthorized();
    if (!credentials.VerifySecret(request.CurrentCredentialSecret ?? string.Empty, device.CredentialSecretHash))
        return Results.Unauthorized();

    var (plaintext, hash) = credentials.GenerateCredential();
    device.CredentialSecretHash = hash;
    device.CredentialVersion += 1;
    await db.SaveChangesAsync(ct);
    return Results.Ok(new RotateCredentialResponse(plaintext, device.CredentialVersion));
}

private static async Task<IResult> RevokeAsync(ClaimsPrincipal principal,
    AppDbContext db, TimeProvider time, CancellationToken ct)
{
    var device = await RequireDeviceAsync(principal, db, ct);
    if (device is null) return Results.Unauthorized();
    if (device.Status != DeviceIdentityStatuses.Revoked)
    {
        device.Status = DeviceIdentityStatuses.Revoked;
        device.RevokedAt = time.GetUtcNow();
        await db.SaveChangesAsync(ct);
    }
    return Results.NoContent();
}
```

- [ ] **Step 7: Run and verify it passes**

Run: `dotnet test tests/SonicRelay.Api.IntegrationTests/SonicRelay.Api.IntegrationTests.csproj --filter FullyQualifiedName~DeviceCredentialLifecycleTests`
Expected: PASS. Note the revoked/stale-token calls assert `403 Forbidden` (authenticated but the scope requirement fails), not `401`.

- [ ] **Step 8: Commit**

```bash
git add services/SonicRelay.Api tests/SonicRelay.Api.IntegrationTests/DeviceCredentialLifecycleTests.cs
git commit -m "Add device scope authorization, rotate-credential, and revoke"
```

---

### Task 5: Pairing challenge creation and completion

**Files:**
- Create: `services/SonicRelay.Api/Services/PairingChallengeService.cs`
- Create: `services/SonicRelay.Api/Contracts/PairingContracts.cs`
- Create: `services/SonicRelay.Api/Endpoints/PairingEndpoints.cs`
- Modify: `services/SonicRelay.Api/Program.cs`
- Modify: `tests/SonicRelay.Api.IntegrationTests/SonicRelayApiFactory.cs`
- Test: `tests/SonicRelay.Api.IntegrationTests/PairingChallengeTests.cs`

**Interfaces:**
- Consumes: `DeviceIdentityEndpoints.RequireDeviceAsync`, `"pairing:create"`/`"pairing:complete"` policies (Tasks 3–4), `PairingChallenge`, `DevicePairing`, `DevicePairingStatuses` (Task 1).
- Produces: `POST /api/pairings/challenges`, `POST /api/pairings/complete`, `PairingResponse` — consumed by Task 6.

- [ ] **Step 1: Write the failing integration test**

```csharp
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using SonicRelay.Api.Contracts;
using Xunit;

namespace SonicRelay.Api.IntegrationTests;

public sealed class PairingChallengeTests : IClassFixture<SonicRelayApiFactory>
{
    private readonly HttpClient _client;

    public PairingChallengeTests(SonicRelayApiFactory factory) => _client = factory.CreateClient();

    private async Task<string> BootstrapAndAuthenticateAsync(string type, string platform)
    {
        var bootstrap = await (await _client.PostAsJsonAsync("/api/devices/bootstrap",
            new BootstrapDeviceRequest("Device", type, platform)))
            .Content.ReadFromJsonAsync<BootstrapDeviceResponse>();
        var token = await (await _client.PostAsJsonAsync("/api/devices/token",
            new DeviceTokenRequest(bootstrap!.DeviceId, bootstrap.CredentialSecret)))
            .Content.ReadFromJsonAsync<DeviceTokenResponse>();
        return token!.AccessToken;
    }

    private HttpRequestMessage Authorized(HttpMethod method, string url, string accessToken, object? body = null)
    {
        var request = new HttpRequestMessage(method, url);
        if (body is not null) request.Content = JsonContent.Create(body);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return request;
    }

    [Fact]
    public async Task Publisher_Creates_Challenge_And_Viewer_Completes_Pairing()
    {
        var publisherToken = await BootstrapAndAuthenticateAsync("windows_publisher", "windows");
        var viewerToken = await BootstrapAndAuthenticateAsync("flutter_viewer", "android");

        var challengeResponse = await _client.SendAsync(
            Authorized(HttpMethod.Post, "/api/pairings/challenges", publisherToken));
        Assert.Equal(HttpStatusCode.Created, challengeResponse.StatusCode);
        var challenge = await challengeResponse.Content.ReadFromJsonAsync<CreateChallengeResponse>();
        Assert.NotNull(challenge);
        Assert.Contains(challenge!.ChallengeId.ToString(), challenge.QrPayload);

        var completeResponse = await _client.SendAsync(Authorized(HttpMethod.Post, "/api/pairings/complete",
            viewerToken, new CompletePairingRequest(challenge.ChallengeId, challenge.Code)));

        Assert.Equal(HttpStatusCode.OK, completeResponse.StatusCode);
        var pairing = await completeResponse.Content.ReadFromJsonAsync<PairingResponse>();
        Assert.Equal("active", pairing!.Status);
    }

    [Fact]
    public async Task Complete_Rejects_WrongCode_Without_Revealing_Reason()
    {
        var publisherToken = await BootstrapAndAuthenticateAsync("windows_publisher", "windows");
        var viewerToken = await BootstrapAndAuthenticateAsync("flutter_viewer", "android");
        var challengeResponse = await _client.SendAsync(
            Authorized(HttpMethod.Post, "/api/pairings/challenges", publisherToken));
        var challenge = await challengeResponse.Content.ReadFromJsonAsync<CreateChallengeResponse>();

        var wrongCode = await _client.SendAsync(Authorized(HttpMethod.Post, "/api/pairings/complete",
            viewerToken, new CompletePairingRequest(challenge!.ChallengeId, "WRONGCODE")));
        var unknownChallenge = await _client.SendAsync(Authorized(HttpMethod.Post, "/api/pairings/complete",
            viewerToken, new CompletePairingRequest(Guid.NewGuid(), challenge.Code)));

        Assert.Equal(HttpStatusCode.BadRequest, wrongCode.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, unknownChallenge.StatusCode);
    }

    [Fact]
    public async Task Complete_Rejects_Code_After_MaxAttempts()
    {
        var publisherToken = await BootstrapAndAuthenticateAsync("windows_publisher", "windows");
        var viewerToken = await BootstrapAndAuthenticateAsync("flutter_viewer", "android");
        var challengeResponse = await _client.SendAsync(
            Authorized(HttpMethod.Post, "/api/pairings/challenges", publisherToken));
        var challenge = await challengeResponse.Content.ReadFromJsonAsync<CreateChallengeResponse>();

        for (var i = 0; i < 5; i++)
        {
            await _client.SendAsync(Authorized(HttpMethod.Post, "/api/pairings/complete",
                viewerToken, new CompletePairingRequest(challenge!.ChallengeId, "WRONGCODE")));
        }

        var finalAttempt = await _client.SendAsync(Authorized(HttpMethod.Post, "/api/pairings/complete",
            viewerToken, new CompletePairingRequest(challenge!.ChallengeId, challenge.Code)));

        Assert.Equal(HttpStatusCode.BadRequest, finalAttempt.StatusCode);
    }

    [Fact]
    public async Task Only_PublisherDevices_Can_Create_Challenges()
    {
        var viewerToken = await BootstrapAndAuthenticateAsync("flutter_viewer", "android");

        var response = await _client.SendAsync(
            Authorized(HttpMethod.Post, "/api/pairings/challenges", viewerToken));

        // A viewer's token never carries the "pairing:create" scope (see
        // DeviceCredentialService.ScopesFor in Task 2), so the "pairing:create"
        // policy itself rejects the request before the handler runs.
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
```

- [ ] **Step 2: Run and verify it fails**

Run: `dotnet test tests/SonicRelay.Api.IntegrationTests/SonicRelay.Api.IntegrationTests.csproj --filter FullyQualifiedName~PairingChallengeTests`
Expected: compile failure — `PairingChallengeService`, contracts, and routes do not exist.

- [ ] **Step 3: Implement `PairingChallengeService`**

```csharp
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace SonicRelay.Api.Services;

public sealed class PairingChallengeService(IOptions<DeviceIdentityOptions> options, TimeProvider time)
{
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    public int MaxAttempts => options.Value.PairingMaxAttempts;

    public string GenerateCode()
    {
        Span<char> span = stackalloc char[8];
        for (var i = 0; i < span.Length; i++) span[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        return new string(span);
    }

    public string HashCode(string code)
    {
        var key = DeviceCredentialService.RequireKey(
            options.Value.PairingCodeHmacKey, nameof(DeviceIdentityOptions.PairingCodeHmacKey));
        return Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(code)));
    }

    public DateTimeOffset NewExpiry() => time.GetUtcNow().AddMinutes(options.Value.PairingCodeTtlMinutes);
}
```

- [ ] **Step 4: Add pairing contracts**

`services/SonicRelay.Api/Contracts/PairingContracts.cs`:

```csharp
namespace SonicRelay.Api.Contracts;

public sealed record CreateChallengeResponse(Guid ChallengeId, string Code, string QrPayload, DateTimeOffset ExpiresAt);

public sealed record CompletePairingRequest(Guid ChallengeId, string Code);

public sealed record PairingResponse(
    Guid PairingId, Guid PublisherDeviceId, Guid ViewerDeviceId, string Status,
    DateTimeOffset CreatedAt, DateTimeOffset? LastUsedAt);
```

- [ ] **Step 5: Implement `PairingEndpoints`**

```csharp
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using SonicRelay.Api.Contracts;
using SonicRelay.Api.Services;
using SonicRelay.Domain.DeviceIdentities;
using SonicRelay.Infrastructure.Persistence;

namespace SonicRelay.Api.Endpoints;

public static class PairingEndpoints
{
    public static IEndpointRouteBuilder MapPairingEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/pairings/challenges", CreateChallengeAsync)
            .RequireAuthorization("pairing:create")
            .RequireRateLimiting("pairing-create")
            .WithTags("Pairing");

        app.MapPost("/api/pairings/complete", CompleteAsync)
            .RequireAuthorization("pairing:complete")
            .RequireRateLimiting("pairing-complete")
            .WithTags("Pairing");

        return app;
    }

    // The "pairing:create" policy already restricts callers to publisher
    // devices (DeviceCredentialService.ScopesFor only grants that scope to
    // windows_publisher), so no device-type check is needed here.
    private static async Task<IResult> CreateChallengeAsync(ClaimsPrincipal principal,
        PairingChallengeService challenges, AppDbContext db, TimeProvider time, CancellationToken ct)
    {
        var publisher = await DeviceIdentityEndpoints.RequireDeviceAsync(principal, db, ct);
        if (publisher is null || publisher.Status != DeviceIdentityStatuses.Active) return Results.Unauthorized();

        var code = challenges.GenerateCode();
        var challenge = new PairingChallenge
        {
            Id = Guid.NewGuid(),
            PublisherDeviceId = publisher.Id,
            CodeHash = challenges.HashCode(code),
            ExpiresAt = challenges.NewExpiry(),
            MaxAttempts = challenges.MaxAttempts,
            CreatedAt = time.GetUtcNow()
        };
        db.PairingChallenges.Add(challenge);
        await db.SaveChangesAsync(ct);

        var qrPayload = JsonSerializer.Serialize(new { challengeId = challenge.Id, code });
        return Results.Created($"/api/pairings/{challenge.Id}",
            new CreateChallengeResponse(challenge.Id, code, qrPayload, challenge.ExpiresAt));
    }

    // The "pairing:complete" policy already restricts callers to viewer
    // devices, mirroring CreateChallengeAsync above.
    private static async Task<IResult> CompleteAsync(CompletePairingRequest request, ClaimsPrincipal principal,
        PairingChallengeService challenges, AppDbContext db, TimeProvider time, CancellationToken ct)
    {
        var viewer = await DeviceIdentityEndpoints.RequireDeviceAsync(principal, db, ct);
        if (viewer is null || viewer.Status != DeviceIdentityStatuses.Active) return Results.Unauthorized();

        var challenge = await db.PairingChallenges.SingleOrDefaultAsync(x => x.Id == request.ChallengeId, ct);
        if (!IsUsable(challenge, time.GetUtcNow()))
            return Results.BadRequest(new { error = "Invalid or expired pairing code." });

        if (challenges.HashCode(request.Code ?? string.Empty) != challenge!.CodeHash)
        {
            challenge.AttemptCount += 1;
            await db.SaveChangesAsync(ct);
            return Results.BadRequest(new { error = "Invalid or expired pairing code." });
        }

        challenge.ConsumedAt = time.GetUtcNow();
        var pairing = new DevicePairing
        {
            Id = Guid.NewGuid(),
            PublisherDeviceId = challenge.PublisherDeviceId,
            ViewerDeviceId = viewer.Id,
            Status = DevicePairingStatuses.Active,
            CreatedAt = time.GetUtcNow(),
            LastUsedAt = time.GetUtcNow()
        };
        db.DevicePairings.Add(pairing);
        await db.SaveChangesAsync(ct);
        return Results.Ok(ToResponse(pairing));
    }

    private static bool IsUsable(PairingChallenge? challenge, DateTimeOffset now) =>
        challenge is not null
        && challenge.ConsumedAt is null
        && challenge.ExpiresAt > now
        && challenge.AttemptCount < challenge.MaxAttempts;

    internal static PairingResponse ToResponse(DevicePairing pairing) => new(
        pairing.Id, pairing.PublisherDeviceId, pairing.ViewerDeviceId, pairing.Status,
        pairing.CreatedAt, pairing.LastUsedAt);
}
```

- [ ] **Step 6: Wire `Program.cs` and test settings**

In `Program.cs`, add `builder.Services.AddSingleton<PairingChallengeService>();` next to `AddSingleton<DeviceCredentialService>();`. In the rate limiter block, after the `device-token` policy, add:

```csharp
options.AddPolicy("pairing-create", context => UserLimit(context, "RateLimits:PairingCreate", 10));
options.AddPolicy("pairing-complete", context => UserLimit(context, "RateLimits:PairingComplete", 10));
```

After `app.MapDeviceIdentityEndpoints();` inside the `if (deviceIdentityEnabled)` block, add `app.MapPairingEndpoints();`.

In `SonicRelayApiFactory.cs`, after the `RateLimits:DeviceToken:PermitLimit` line, add:

```csharp
builder.UseSetting("RateLimits:PairingCreate:PermitLimit", "100");
builder.UseSetting("RateLimits:PairingComplete:PermitLimit", "100");
```

- [ ] **Step 7: Run and verify it passes**

Run: `dotnet test tests/SonicRelay.Api.IntegrationTests/SonicRelay.Api.IntegrationTests.csproj --filter FullyQualifiedName~PairingChallengeTests`
Expected: PASS.

- [ ] **Step 8: Commit**

```bash
git add services/SonicRelay.Api tests/SonicRelay.Api.IntegrationTests/PairingChallengeTests.cs tests/SonicRelay.Api.IntegrationTests/SonicRelayApiFactory.cs
git commit -m "Add pairing challenge creation and completion endpoints"
```

---

### Task 6: Pairing list/revoke and device isolation

**Files:**
- Modify: `services/SonicRelay.Api/Endpoints/PairingEndpoints.cs`
- Test: `tests/SonicRelay.Api.IntegrationTests/PairingManagementTests.cs`

**Interfaces:**
- Consumes: `PairingEndpoints.ToResponse`, `DeviceIdentityEndpoints.RequireDeviceAsync`, `"device:read"`/`"pairing:revoke"` policies (Tasks 3–5).
- Produces: `GET /api/devices/{deviceId}/pairings`, `DELETE /api/pairings/{pairingId}`.

- [ ] **Step 1: Write the failing integration test**

```csharp
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using SonicRelay.Api.Contracts;
using Xunit;

namespace SonicRelay.Api.IntegrationTests;

public sealed class PairingManagementTests : IClassFixture<SonicRelayApiFactory>
{
    private readonly HttpClient _client;

    public PairingManagementTests(SonicRelayApiFactory factory) => _client = factory.CreateClient();

    private async Task<(Guid DeviceId, string AccessToken)> BootstrapAndAuthenticateAsync(string type, string platform)
    {
        var bootstrap = await (await _client.PostAsJsonAsync("/api/devices/bootstrap",
            new BootstrapDeviceRequest("Device", type, platform)))
            .Content.ReadFromJsonAsync<BootstrapDeviceResponse>();
        var token = await (await _client.PostAsJsonAsync("/api/devices/token",
            new DeviceTokenRequest(bootstrap!.DeviceId, bootstrap.CredentialSecret)))
            .Content.ReadFromJsonAsync<DeviceTokenResponse>();
        return (bootstrap.DeviceId, token!.AccessToken);
    }

    private HttpRequestMessage Authorized(HttpMethod method, string url, string accessToken, object? body = null)
    {
        var request = new HttpRequestMessage(method, url);
        if (body is not null) request.Content = JsonContent.Create(body);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return request;
    }

    private async Task<PairingResponse> PairAsync((Guid DeviceId, string AccessToken) publisher,
        (Guid DeviceId, string AccessToken) viewer)
    {
        var challenge = await (await _client.SendAsync(
            Authorized(HttpMethod.Post, "/api/pairings/challenges", publisher.AccessToken)))
            .Content.ReadFromJsonAsync<CreateChallengeResponse>();
        var pairing = await (await _client.SendAsync(Authorized(HttpMethod.Post, "/api/pairings/complete",
            viewer.AccessToken, new CompletePairingRequest(challenge!.ChallengeId, challenge.Code))))
            .Content.ReadFromJsonAsync<PairingResponse>();
        return pairing!;
    }

    [Fact]
    public async Task List_Returns_Only_ActivePairings_For_The_Authenticated_Device()
    {
        var publisherA = await BootstrapAndAuthenticateAsync("windows_publisher", "windows");
        var viewerA = await BootstrapAndAuthenticateAsync("flutter_viewer", "android");
        var publisherB = await BootstrapAndAuthenticateAsync("windows_publisher", "windows");
        var viewerB = await BootstrapAndAuthenticateAsync("flutter_viewer", "android");

        await PairAsync(publisherA, viewerA);
        await PairAsync(publisherB, viewerB);

        var response = await _client.SendAsync(Authorized(
            HttpMethod.Get, $"/api/devices/{publisherA.DeviceId}/pairings", publisherA.AccessToken));
        var pairings = await response.Content.ReadFromJsonAsync<List<PairingResponse>>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Single(pairings!);
        Assert.Equal(publisherA.DeviceId, pairings![0].PublisherDeviceId);
        Assert.Equal(viewerA.DeviceId, pairings[0].ViewerDeviceId);
    }

    [Fact]
    public async Task Cannot_List_Another_Devices_Pairings()
    {
        var publisherA = await BootstrapAndAuthenticateAsync("windows_publisher", "windows");
        var publisherB = await BootstrapAndAuthenticateAsync("windows_publisher", "windows");

        var response = await _client.SendAsync(Authorized(
            HttpMethod.Get, $"/api/devices/{publisherB.DeviceId}/pairings", publisherA.AccessToken));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Revoke_Removes_Pairing_From_Both_Participants_Lists()
    {
        var publisher = await BootstrapAndAuthenticateAsync("windows_publisher", "windows");
        var viewer = await BootstrapAndAuthenticateAsync("flutter_viewer", "android");
        var pairing = await PairAsync(publisher, viewer);

        var revokeResponse = await _client.SendAsync(Authorized(
            HttpMethod.Delete, $"/api/pairings/{pairing.PairingId}", viewer.AccessToken));
        Assert.Equal(HttpStatusCode.NoContent, revokeResponse.StatusCode);

        var publisherList = await (await _client.SendAsync(Authorized(
            HttpMethod.Get, $"/api/devices/{publisher.DeviceId}/pairings", publisher.AccessToken)))
            .Content.ReadFromJsonAsync<List<PairingResponse>>();

        Assert.Empty(publisherList!);
    }
}
```

- [ ] **Step 2: Run and verify it fails**

Run: `dotnet test tests/SonicRelay.Api.IntegrationTests/SonicRelay.Api.IntegrationTests.csproj --filter FullyQualifiedName~PairingManagementTests`
Expected: 404s — the routes do not exist yet.

- [ ] **Step 3: Add the endpoints**

In `MapPairingEndpoints`, add:

```csharp
app.MapGet("/api/devices/{deviceId:guid}/pairings", ListAsync)
    .RequireAuthorization("device:read")
    .WithTags("Pairing");

app.MapDelete("/api/pairings/{pairingId:guid}", RevokeAsync)
    .RequireAuthorization("pairing:revoke")
    .WithTags("Pairing");
```

Add the handlers:

```csharp
private static async Task<IResult> ListAsync(Guid deviceId, ClaimsPrincipal principal,
    AppDbContext db, CancellationToken ct)
{
    var caller = await DeviceIdentityEndpoints.RequireDeviceAsync(principal, db, ct);
    if (caller is null || caller.Id != deviceId) return Results.Unauthorized();

    var pairings = await db.DevicePairings.AsNoTracking()
        .Where(x => (x.PublisherDeviceId == deviceId || x.ViewerDeviceId == deviceId)
            && x.Status == DevicePairingStatuses.Active)
        .OrderByDescending(x => x.CreatedAt)
        .ToListAsync(ct);
    return Results.Ok(pairings.Select(ToResponse));
}

private static async Task<IResult> RevokeAsync(Guid pairingId, ClaimsPrincipal principal,
    AppDbContext db, TimeProvider time, CancellationToken ct)
{
    var caller = await DeviceIdentityEndpoints.RequireDeviceAsync(principal, db, ct);
    if (caller is null) return Results.Unauthorized();

    var pairing = await db.DevicePairings.SingleOrDefaultAsync(x =>
        x.Id == pairingId && (x.PublisherDeviceId == caller.Id || x.ViewerDeviceId == caller.Id), ct);
    if (pairing is null) return Results.NotFound();

    if (pairing.Status != DevicePairingStatuses.Revoked)
    {
        pairing.Status = DevicePairingStatuses.Revoked;
        pairing.RevokedAt = time.GetUtcNow();
        await db.SaveChangesAsync(ct);
    }
    return Results.NoContent();
}
```

- [ ] **Step 4: Run and verify it passes**

Run: `dotnet test tests/SonicRelay.Api.IntegrationTests/SonicRelay.Api.IntegrationTests.csproj --filter FullyQualifiedName~PairingManagementTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add services/SonicRelay.Api/Endpoints/PairingEndpoints.cs tests/SonicRelay.Api.IntegrationTests/PairingManagementTests.cs
git commit -m "Add pairing list and revoke endpoints"
```

---

### Task 7: Feature flag off-path verification

**Files:**
- Test: `tests/SonicRelay.Api.IntegrationTests/DeviceIdentityFeatureFlagTests.cs`

**Interfaces:**
- Consumes: `SonicRelayApiFactory`'s internal constructor taking `IReadOnlyDictionary<string, string?>` (existing).

- [ ] **Step 1: Write the test**

```csharp
using System.Net;
using System.Net.Http.Json;
using SonicRelay.Api.Contracts;
using Xunit;

namespace SonicRelay.Api.IntegrationTests;

public sealed class DeviceIdentityFeatureFlagTests
{
    [Fact]
    public async Task Disabling_DeviceIdentity_Removes_New_Endpoints_Without_Affecting_Identity_Auth()
    {
        await using var factory = new SonicRelayApiFactory(new Dictionary<string, string?>
        {
            ["DeviceIdentity:Enabled"] = "false"
        });
        var client = factory.CreateClient();

        var bootstrapResponse = await client.PostAsJsonAsync("/api/devices/bootstrap",
            new BootstrapDeviceRequest("Device", "windows_publisher", "windows"));
        Assert.Equal(HttpStatusCode.NotFound, bootstrapResponse.StatusCode);

        var meResponse = await client.GetAsync("/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, meResponse.StatusCode);
    }
}
```

- [ ] **Step 2: Run and verify it currently fails**

Run: `dotnet test tests/SonicRelay.Api.IntegrationTests/SonicRelay.Api.IntegrationTests.csproj --filter FullyQualifiedName~DeviceIdentityFeatureFlagTests`
Expected: if Task 3's `if (deviceIdentityEnabled)` gating around `AddJwtBearer` and `MapDeviceIdentityEndpoints`/`MapPairingEndpoints` was implemented correctly, this should already PASS. If it fails, `Program.cs` is mapping the endpoints or authentication scheme unconditionally — fix by re-checking the `deviceIdentityEnabled` guards added in Tasks 3–5.

- [ ] **Step 3: Fix gating if needed, then confirm it passes**

Run the Step 1 command again. Expected: PASS.

- [ ] **Step 4: Commit**

```bash
git add tests/SonicRelay.Api.IntegrationTests/DeviceIdentityFeatureFlagTests.cs
git commit -m "Add feature-flag off-path coverage for device identity auth"
```

---

### Task 8: Documentation

**Files:**
- Create: `docs/adr/0005-device-identity-credentials.md`
- Create: `docs/device-identity.md`
- Modify: `docs/security.md`
- Modify: `docs/architecture.md`
- Modify: `README.md`

- [ ] **Step 1: Add ADR 0005**

`docs/adr/0005-device-identity-credentials.md`:

```markdown
# ADR 0005: Symmetric device credentials with a parallel DeviceBearer scheme

- Status: Accepted
- Date: 2026-07-21

## Context

Issue #26 (Phase 1) introduces device-identity authentication so Windows and
Flutter clients can authenticate without a human account. Devices need a
persistent, revocable credential and short-lived access tokens with explicit
scopes, without breaking the existing Identity login flow.

## Decision

Bootstrap issues a server-generated, high-entropy secret once; only its HMAC
is stored. `/api/devices/token` exchanges that secret for a short-lived JWT on
a new `DeviceBearer` authentication scheme, registered alongside the existing
`Identity.Bearer` scheme (ADR 0002) rather than replacing it. A custom
authorization requirement re-checks device status and credential version
against the database on every scoped request, so rotation and revocation take
effect immediately despite tokens being self-contained. The whole feature is
gated by `DeviceIdentity:Enabled`.

Asymmetric (public-key) proof-of-possession was considered and rejected for
this phase: it requires client-side key generation and challenge-response
signing on both Windows and Flutter before any server work is testable,
which the issue explicitly allows deferring for the MVP. `DeviceIdentity`
reserves a `PublicKey` column for this later.

## Consequences

Two independent bearer schemes coexist until Phase 4 removes Identity. A
compromised device secret is equivalent to a compromised device until
rotated or revoked; short token lifetimes (default 5 minutes) and per-request
status checks bound the exposure window. Migrating `StreamSession`/signaling
to device ownership (Phase 2) and client integration (Phase 3) are tracked
separately in issue #26.
```

- [ ] **Step 2: Add `docs/device-identity.md`**

```markdown
# Device Identity Auth (Phase 1)

This document describes the device-credential flow added alongside the
existing Identity login (see `docs/adr/0005-device-identity-credentials.md`).
It covers backend behavior only; Windows/Flutter client integration is a
later phase of issue #26.

## Flow

1. `POST /api/devices/bootstrap` — a device registers with a `name`,
   `deviceType` (`windows_publisher` or `flutter_viewer`), and `platform`.
   The response includes the device ID and a credential secret returned
   exactly once; only its HMAC is ever stored.
2. `POST /api/devices/token` — the device exchanges its ID and secret for a
   short-lived JWT (`DeviceIdentity:AccessTokenMinutes`, default 5 minutes)
   carrying scopes appropriate to its device type.
3. `POST /api/devices/rotate-credential` — requires the current secret and a
   valid `device:manage`-scoped token; issues a new secret and invalidates
   every token issued under the previous credential version immediately.
4. `POST /api/devices/revoke` — marks the device revoked; blocks future token
   requests and rejects already-issued tokens on their next authorized
   request, without waiting for expiry.

## Pairing

1. A publisher device with a `pairing:create`-scoped token calls
   `POST /api/pairings/challenges` and receives a short-TTL code
   (`DeviceIdentity:PairingCodeTtlMinutes`, default 5 minutes) plus a QR
   payload containing the challenge ID and code — no persistent secret is
   ever embedded in the QR payload.
2. A viewer device with a `pairing:complete`-scoped token calls
   `POST /api/pairings/complete` with the code. A wrong code, expired
   challenge, and already-consumed challenge all return the same generic
   error and increment the attempt counter; the challenge is rejected outright
   after `DeviceIdentity:PairingMaxAttempts` (default 5) failed attempts.
3. `GET /api/devices/{deviceId}/pairings` and `DELETE /api/pairings/{pairingId}`
   are restricted to a caller whose authenticated device ID participates in
   the pairing.

## Configuration

| Key | Purpose |
| --- | --- |
| `DeviceIdentity:Enabled` | Feature flag; `false` removes this flow's HTTP surface entirely. |
| `DeviceIdentity:CredentialHmacKey` | Server-side pepper for hashing device credential secrets. |
| `DeviceIdentity:PairingCodeHmacKey` | Server-side pepper for hashing pairing codes. |
| `DeviceIdentity:TokenSigningKey` | Symmetric signing key for `DeviceBearer` JWTs. |
| `DeviceIdentity:AccessTokenMinutes` | Access token lifetime (default 5). |
| `DeviceIdentity:PairingCodeTtlMinutes` | Pairing challenge TTL (default 5). |
| `DeviceIdentity:PairingMaxAttempts` | Max failed pairing attempts before a challenge is rejected (default 5). |

Set high-entropy values for `CredentialHmacKey`, `PairingCodeHmacKey`, and
`TokenSigningKey` outside Git, the same way `Sessions:CodeHmacKey` is handled.

## Out of scope in Phase 1

`ApplicationUser`, the existing owner-scoped `Device`, `StreamSession`,
signaling, and TURN credential issuance are unchanged. See issue #26 for the
remaining phases.
```

- [ ] **Step 3: Update `docs/security.md`**

Add a new subsection under "Implemented controls", after the existing "Session codes" subsection:

```markdown
### Device identity credentials (Phase 1 of issue #26)

- Device bootstrap issues a high-entropy secret once; only its HMAC-SHA-256
  output, keyed by `DeviceIdentity:CredentialHmacKey`, is persisted.
- Access tokens are short-lived JWTs on a separate `DeviceBearer` scheme;
  every scoped request re-checks device status and credential version against
  the database, so rotation and revocation take effect immediately.
- Pairing codes follow the session-code convention: HMAC-hashed, short TTL,
  attempt-limited, and indistinguishable failure responses.
- The entire flow is gated by `DeviceIdentity:Enabled` and does not affect
  the existing Identity login endpoints.
```

- [ ] **Step 4: Update `docs/architecture.md`**

Add a bullet under "Decision records":

```markdown
- [ADR 0005: Symmetric device credentials with a parallel DeviceBearer scheme](adr/0005-device-identity-credentials.md)
```

Add one sentence to the "Components" section's `src/SonicRelay.Domain` bullet: append ", and (Phase 1 of issue #26) a parallel device-identity credential and pairing model — see `docs/device-identity.md`."

- [ ] **Step 5: Update `README.md`**

Add `DeviceIdentity:CredentialHmacKey`, `DeviceIdentity:PairingCodeHmacKey`, and `DeviceIdentity:TokenSigningKey` to whichever existing table or list documents required secrets (next to `Sessions:CodeHmacKey`), with a one-line pointer to `docs/device-identity.md`.

- [ ] **Step 6: Commit**

```bash
git add docs/adr/0005-device-identity-credentials.md docs/device-identity.md docs/security.md docs/architecture.md README.md
git commit -m "Document device identity auth (Phase 1 of issue #26)"
```

---

### Task 9: Full verification

**Files:**
- Modify only files touched above, if verification exposes a defect.

- [ ] **Step 1: Build the full solution**

Run: `dotnet build SonicRelay.sln`
Expected: `Build succeeded. 0 Warning(s). 0 Error(s).`

- [ ] **Step 2: Run the full integration test project**

Run: `dotnet test tests/SonicRelay.Api.IntegrationTests/SonicRelay.Api.IntegrationTests.csproj --no-restore`
Expected: all tests pass, including every pre-existing test class (`DeviceEndpointsTests`, `AuthEndpointsTests`, `SessionEndpointsTests`, etc.) — proving nothing about the existing Identity flow regressed.

- [ ] **Step 3: Check formatting and diff scope**

Run: `dotnet format SonicRelay.sln --verify-no-changes --no-restore`
Run: `git diff --check`
Expected: both exit successfully; the diff touches only `src/SonicRelay.Domain/DeviceIdentities/**`, `src/SonicRelay.Infrastructure/Persistence/**`, `services/SonicRelay.Api/**`, `tests/SonicRelay.Api.IntegrationTests/**`, `docs/**`, and `README.md`.

- [ ] **Step 4: Summarize**

Report endpoint list, test results, and a concise diff summary. Do not update unrelated dependencies or refactor code outside this plan's scope.
