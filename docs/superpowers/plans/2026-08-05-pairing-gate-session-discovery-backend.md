# Pairing Gate & Session Discovery — Backend Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Remove the shared relay-settings table, let a viewer tell "not paired" apart from "bad code" when joining, and let a paired viewer discover and join an open session without a code.

**Architecture:** Four independent slices of `services/SonicRelay.Api`. Task 1 deletes the relay-settings feature and drops its table, returning `TurnCredentialService` to a pure projection of `TurnOptions`. Tasks 2-4 extend `SessionEndpoints`: a distinguishable `403` for the unpaired case, a discovery endpoint, and a join-by-id endpoint that shares its post-redemption checks with the code path.

**Tech Stack:** .NET 10, ASP.NET Core minimal APIs, EF Core (Npgsql in production, InMemory in tests), xUnit, `WebApplicationFactory<Program>`.

## Global Constraints

- Target framework and SDK are unchanged; do not touch `global.json` or any `.csproj` target.
- Endpoint authorization uses the existing named scope policies. Do not invent new policies: discovery and join-by-id both use `session:join`.
- Rate limiting for both new endpoints uses the existing `join-session` policy.
- A join code is never returned by any endpoint other than session create and rotate-code.
- Table names are snake_case (`relay_settings`, `device_pairings`, `stream_sessions`); entity property names are PascalCase.
- Every integration test derives its clients from `DeviceIdentityTestHelper.BootstrapAndAuthorizeAsync` — never hand-craft a token.
- Run all backend tests with: `dotnet test SonicRelay.sln`
- Commit after every task. Do not squash tasks into one commit.

---

### Task 1: Remove the relay settings API and table

The relay/coturn override moves to a per-device client-side preference, so the
shared table and its endpoints go away entirely. Production is unaffected:
`TURN_PUBLIC_HOST` in the VPS `.env` has been the real source throughout, and
`Program.cs` keeps deriving the `turn:`/`stun:` URIs from it.

**Files:**
- Delete: `services/SonicRelay.Api/Endpoints/SettingsEndpoints.cs`
- Delete: `services/SonicRelay.Api/Contracts/SettingsContracts.cs`
- Delete: `src/SonicRelay.Domain/RelaySettings/RelaySettings.cs`
- Delete: `tests/SonicRelay.Api.IntegrationTests/RelaySettingsPersistenceTests.cs`
- Modify: `src/SonicRelay.Infrastructure/Persistence/AppDbContext.cs:3` (using), `:17` (DbSet), `:78-83` (entity block)
- Modify: `services/SonicRelay.Api/Program.cs:193` (remove `app.MapSettingsEndpoints();`)
- Modify: `services/SonicRelay.Api/Services/TurnCredentialService.cs`
- Create: `src/SonicRelay.Infrastructure/Persistence/Migrations/<timestamp>_DropRelaySettings.cs` (generated)
- Test: `tests/SonicRelay.Api.IntegrationTests/WebRtcEndpointsTests.cs`

**Interfaces:**
- Consumes: nothing from earlier tasks.
- Produces: `TurnCredentialService.BuildAsync(string deviceId, CancellationToken)` — signature unchanged, but the constructor loses its `AppDbContext` parameter and becomes `TurnCredentialService(IOptions<TurnOptions> options, TimeProvider time)`. `RelayModes` no longer exists in `SonicRelay.Domain`; the string values `automatic`/`forceRelay`/`disableFallback` survive only in the clients.

- [ ] **Step 1: Write the failing test**

`tests/SonicRelay.Api.IntegrationTests/WebRtcEndpointsTests.cs` already exists
with nine tests and an `IClassFixture<SonicRelayApiFactory>` shape. Keep the
file, its private helpers (`BootstrapAsync`, `GetIceServersAsync`,
`TryGetNonNull`) and these six tests untouched — every one is driven purely by
`Turn:*`/`TURN_*` configuration and never touched `RelaySettings`, so they all
still describe behaviour that must keep working:

- `Ice_servers_requires_authentication`
- `Ice_servers_returns_stun_only_when_turn_is_not_configured`
- `Ice_servers_returns_turn_entry_with_coturn_rest_credentials`
- `Ice_servers_accepts_flat_environment_style_configuration`
- `Ice_servers_derives_turn_and_stun_uris_from_the_public_host`
- `Ice_servers_prefers_explicit_turn_uris_over_the_derived_ones`

`Ice_servers_derives_turn_and_stun_uris_from_the_public_host` matters most:
`TURN_PUBLIC_HOST` derivation is this deployment's real production path, and
after this task it is the only path. Do not drop its coverage in the same
commit that removes the alternative.

Delete only these three, which exercise the removed feature:

- `Ice_servers_omits_turn_when_relay_mode_is_disable_fallback`
- `Ice_servers_uses_the_overridden_turn_uri_and_secret_when_present`
- `Put_relay_settings_via_http_is_reflected_end_to_end_in_ice_servers`

Drop the `using SonicRelay.Domain.RelaySettings;` and
`using SonicRelay.Infrastructure.Persistence;` imports that only those three
needed. Then add one test proving the endpoint is gone:

```csharp
    [Fact]
    public async Task Relay_settings_endpoint_is_gone()
    {
        await using var factory = new SonicRelayApiFactory();
        var client = factory.CreateClient();
        await DeviceIdentityTestHelper.BootstrapAndAuthorizeAsync(
            client, DeviceTypes.WindowsPublisher, DevicePlatforms.Windows);

        var response = await client.GetAsync("/api/settings/relay");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
```

`tests/SonicRelay.Api.IntegrationTests/SettingsEndpointsTests.cs` also exists
and exercises the removed endpoint directly. Delete that whole file.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test SonicRelay.sln --filter FullyQualifiedName~WebRtcEndpointsTests`
Expected: `Relay_settings_endpoint_is_gone` FAILS (the endpoint still answers `200`).
The six retained tests still pass.

- [ ] **Step 3: Delete the relay settings feature**

Delete these four files outright:

```bash
git rm services/SonicRelay.Api/Endpoints/SettingsEndpoints.cs \
       services/SonicRelay.Api/Contracts/SettingsContracts.cs \
       src/SonicRelay.Domain/RelaySettings/RelaySettings.cs \
       tests/SonicRelay.Api.IntegrationTests/RelaySettingsPersistenceTests.cs
```

In `services/SonicRelay.Api/Program.cs`, delete line 193:

```csharp
app.MapSettingsEndpoints();
```

In `src/SonicRelay.Infrastructure/Persistence/AppDbContext.cs`, delete the
`using SonicRelay.Domain.RelaySettings;` line, the `DbSet` property:

```csharp
public DbSet<RelaySettings> RelaySettings => Set<RelaySettings>();
```

and the whole entity block:

```csharp
modelBuilder.Entity<RelaySettings>(entity =>
{
    entity.ToTable("relay_settings");
    entity.HasKey(x => x.Id);
    entity.Property(x => x.RelayMode).HasMaxLength(20).IsRequired();
});
```

- [ ] **Step 4: Simplify TurnCredentialService**

Replace the whole `TurnCredentialService` class in
`services/SonicRelay.Api/Services/TurnCredentialService.cs` with the version
below. `TurnOptions`, `IceServerEntry` and `IceServersResponse` in the same
file are unchanged.

The `disableFallback` branch is removed deliberately: the relay mode is now a
per-device client preference, and a server that withheld TURN entries would
impose one device's choice on every other device it serves.

```csharp
public sealed class TurnCredentialService(IOptions<TurnOptions> options, TimeProvider time)
{
    public Task<IceServersResponse> BuildAsync(string deviceId, CancellationToken cancellationToken)
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
                Encoding.UTF8.GetBytes(settings.StaticAuthSecret), Encoding.UTF8.GetBytes(username)));
            servers.Add(new IceServerEntry(settings.TurnUris, username, credential));
        }

        return Task.FromResult(new IceServersResponse(servers, settings.CredentialTtlSeconds));
    }
}
```

Delete the now-unused usings at the top of the file:
`using Microsoft.EntityFrameworkCore;`, `using SonicRelay.Domain.RelaySettings;`
and `using SonicRelay.Infrastructure.Persistence;`.

`BuildAsync` keeps returning `Task<...>` so `WebRtcEndpoints.GetIceServersAsync`
needs no change at its call site.

- [ ] **Step 5: Generate the drop migration**

Run:

```bash
dotnet ef migrations add DropRelaySettings \
  --project src/SonicRelay.Infrastructure \
  --startup-project services/SonicRelay.Api \
  --output-dir Persistence/Migrations
```

Verify the generated `Up` contains `migrationBuilder.DropTable(name: "relay_settings");`
and that `Down` recreates it with the same columns as
`20260804155058_AddRelaySettings.cs` (`Id` uuid, `RelayMode` varchar(20) not
null, `TurnUris` text[] null, `TurnStaticAuthSecret` text null, `UpdatedAt`
timestamptz not null). If `Down` is empty, hand-write it from that file —
a one-way migration would block a rollback deploy.

- [ ] **Step 6: Run the full suite**

Run: `dotnet test SonicRelay.sln`
Expected: PASS. Both new `WebRtcEndpointsTests` pass and nothing else broke.

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "Remove the shared relay settings table and its API

The relay mode and coturn override become per-device client preferences, so
a single backend-wide row is the wrong shape: any device editing it changed
the relay for every other device the backend serves.

TurnCredentialService returns to a pure projection of TurnOptions, which is
what production has actually been using all along — TURN_PUBLIC_HOST in the
VPS .env, from which Program.cs derives the turn:/stun: URIs. The
disableFallback branch goes with it: suppressing TURN is now a client-side
filter, since a server-side one would impose one device's preference on all
the others."
```

---

### Task 2: Tell "not paired" apart from "bad code" on join

`JoinAsync` currently funnels an unpaired viewer into the same
`404 invalid code` as a wrong code, which is what made the reported failure
undiagnosable from the phone.

**Files:**
- Modify: `services/SonicRelay.Api/Endpoints/SessionEndpoints.cs` (`JoinAsync`, `InvalidCode`)
- Test: `tests/SonicRelay.Api.IntegrationTests/SessionEndpointsTests.cs:83-135`

**Interfaces:**
- Consumes: nothing from Task 1.
- Produces: two stable error bodies used by both clients and by Task 4 —
  `404 { "error": "Invalid or expired session code.", "code": "invalid_code" }`
  and `403 { "error": "This device is not paired with the publisher of that session.", "code": "not_paired" }`.
  Also produces `SessionEndpoints.NotPaired()`, reused by Task 4.

- [ ] **Step 1: Rewrite the three tests that assert indistinguishability**

Three existing tests assert the unpaired response is byte-identical to the
invalid-code response. That was a deliberate choice; this task reverses it, so
replace their bodies rather than deleting them. In
`tests/SonicRelay.Api.IntegrationTests/SessionEndpointsTests.cs`, replace
`Join_rejects_an_unpaired_viewer`, `Join_rejects_a_viewer_paired_to_another_publisher`
and `Join_rejects_a_revoked_pairing` with:

```csharp
[Fact]
public async Task Join_rejects_an_unpaired_viewer_with_a_distinguishable_reason()
{
    var (_, _, code) = await CreateSessionAsync();
    var (viewerClient, _) = await BootstrapAsync(DeviceTypes.FlutterViewer, DevicePlatforms.Android);

    var unpaired = await viewerClient.PostAsJsonAsync("/api/sessions/join", new { code });
    var invalid = await viewerClient.PostAsJsonAsync("/api/sessions/join", new { code = "ZZZZZZ" });

    Assert.Equal(HttpStatusCode.Forbidden, unpaired.StatusCode);
    Assert.Equal("not_paired", (await ReadJsonAsync(unpaired)).GetProperty("code").GetString());
    Assert.Equal(HttpStatusCode.NotFound, invalid.StatusCode);
    Assert.Equal("invalid_code", (await ReadJsonAsync(invalid)).GetProperty("code").GetString());
}

[Fact]
public async Task Join_rejects_a_viewer_paired_to_another_publisher()
{
    var (_, _, code) = await CreateSessionAsync();
    var (_, otherPublisherId) = await BootstrapAsync(DeviceTypes.WindowsPublisher, DevicePlatforms.Windows);
    var (viewerClient, viewerDeviceId) = await BootstrapAsync(DeviceTypes.FlutterViewer, DevicePlatforms.Android);
    await PairDevicesAsync(otherPublisherId, viewerDeviceId);

    var response = await viewerClient.PostAsJsonAsync("/api/sessions/join", new { code });

    Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    Assert.Equal("not_paired", (await ReadJsonAsync(response)).GetProperty("code").GetString());
}

[Fact]
public async Task Join_rejects_a_revoked_pairing()
{
    var (_, sessionId, code) = await CreateSessionAsync();
    var (viewerClient, viewerDeviceId) = await BootstrapAsync(DeviceTypes.FlutterViewer, DevicePlatforms.Android);
    await PairDevicesAsync(await GetPublisherDeviceIdAsync(sessionId), viewerDeviceId, DevicePairingStatuses.Revoked);

    var response = await viewerClient.PostAsJsonAsync("/api/sessions/join", new { code });

    Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    Assert.Equal("not_paired", (await ReadJsonAsync(response)).GetProperty("code").GetString());
}
```

Also update `Unpaired_attempt_does_not_block_the_paired_viewer` (same file),
changing only its first assertion — the point of that test is that a failed
attempt does not consume the code, and that must keep holding:

```csharp
Assert.Equal(HttpStatusCode.Forbidden, unpaired.StatusCode);
Assert.Equal(HttpStatusCode.OK, paired.StatusCode);
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test SonicRelay.sln --filter FullyQualifiedName~SessionEndpointsTests`
Expected: the four tests above FAIL with `Assert.Equal() Failure: Expected: Forbidden, Actual: NotFound`.

- [ ] **Step 3: Implement the split**

In `services/SonicRelay.Api/Endpoints/SessionEndpoints.cs`, replace the
`InvalidCode` helper:

```csharp
private static IResult InvalidCode() =>
    Results.NotFound(new { error = "Invalid or expired session code.", code = "invalid_code" });

private static IResult NotPaired() =>
    Results.Json(new
    {
        error = "This device is not paired with the publisher of that session.",
        code = "not_paired"
    }, statusCode: StatusCodes.Status403Forbidden);
```

Then in `JoinAsync`, change only the pairing branch:

```csharp
if (!await HasActivePairingAsync(db, session.SourceDeviceId, device.Id, ct))
    return NotPaired();
```

Leave the check where it is, after `codeStore.RedeemAsync`. Moving it earlier
would let an unpaired caller probe arbitrary codes for validity; keeping it
here means the caller must already hold a live code to reach the `403`.
`RedeemAsync` is a plain read, so a rejected attempt still consumes nothing.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test SonicRelay.sln --filter FullyQualifiedName~SessionEndpointsTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add services/SonicRelay.Api/Endpoints/SessionEndpoints.cs tests/SonicRelay.Api.IntegrationTests/SessionEndpointsTests.cs
git commit -m "Return 403 not_paired instead of a generic invalid-code 404

A viewer whose pairing is missing or revoked got the same response as one
that typed a wrong code, so a broken pairing was indistinguishable from a
typo on the phone. This reverses the earlier deliberate choice to make the
two identical: the check stays after code redemption, so a caller must
already hold a live code to observe the difference, and brute-forcing one
is not reachable through the join-session rate limit."
```

---

### Task 3: Discover sessions of paired publishers

**Files:**
- Modify: `services/SonicRelay.Api/Endpoints/SessionEndpoints.cs` (route table, new `GetDiscoverableAsync`)
- Test: `tests/SonicRelay.Api.IntegrationTests/SessionEndpointsTests.cs`

**Interfaces:**
- Consumes: `NotPaired()` is not used here. `PairDevicesAsync`, `CreateSessionAsync`, `BootstrapAsync`, `GetPublisherDeviceIdAsync`, `ReadJsonAsync` from the existing test class.
- Produces: `GET /api/sessions/discoverable` → `200` with a JSON array; each element is
  `{ sessionId: Guid, publisherDeviceId: Guid, publisherDeviceName: string, status: string, viewerCount: int, maxViewers: int, createdAt: DateTimeOffset }`.
  Task 4 and the Flutter client depend on `sessionId` and `publisherDeviceName`.

- [ ] **Step 1: Write the failing tests**

Append to `SessionEndpointsTests`:

```csharp
[Fact]
public async Task Discoverable_lists_waiting_sessions_of_actively_paired_publishers()
{
    var (_, sessionId, _) = await CreateSessionAsync();
    var (viewerClient, viewerDeviceId) = await BootstrapAsync(DeviceTypes.FlutterViewer, DevicePlatforms.Android);
    await PairDevicesAsync(await GetPublisherDeviceIdAsync(sessionId), viewerDeviceId);

    var response = await viewerClient.GetAsync("/api/sessions/discoverable");
    var body = await ReadJsonAsync(response);

    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    var entry = body.EnumerateArray().Single(x => x.GetProperty("sessionId").GetGuid() == sessionId);
    Assert.Equal(SessionStatuses.Waiting, entry.GetProperty("status").GetString());
    Assert.Equal(0, entry.GetProperty("viewerCount").GetInt32());
    Assert.False(string.IsNullOrWhiteSpace(entry.GetProperty("publisherDeviceName").GetString()));
}

[Fact]
public async Task Discoverable_never_exposes_the_join_code()
{
    var (_, sessionId, _) = await CreateSessionAsync();
    var (viewerClient, viewerDeviceId) = await BootstrapAsync(DeviceTypes.FlutterViewer, DevicePlatforms.Android);
    await PairDevicesAsync(await GetPublisherDeviceIdAsync(sessionId), viewerDeviceId);

    var response = await viewerClient.GetAsync("/api/sessions/discoverable");
    var entry = (await ReadJsonAsync(response)).EnumerateArray().Single();

    Assert.False(entry.TryGetProperty("code", out _));
}

[Fact]
public async Task Discoverable_excludes_unpaired_and_revoked_publishers()
{
    var (_, sessionId, _) = await CreateSessionAsync();
    var (unpairedClient, _) = await BootstrapAsync(DeviceTypes.FlutterViewer, DevicePlatforms.Android);
    var (revokedClient, revokedViewerId) = await BootstrapAsync(DeviceTypes.FlutterViewer, DevicePlatforms.Android);
    await PairDevicesAsync(await GetPublisherDeviceIdAsync(sessionId), revokedViewerId, DevicePairingStatuses.Revoked);

    var unpaired = await ReadJsonAsync(await unpairedClient.GetAsync("/api/sessions/discoverable"));
    var revoked = await ReadJsonAsync(await revokedClient.GetAsync("/api/sessions/discoverable"));

    Assert.Empty(unpaired.EnumerateArray());
    Assert.Empty(revoked.EnumerateArray());
}

[Fact]
public async Task Discoverable_excludes_ended_sessions()
{
    var (ownerClient, sessionId, _) = await CreateSessionAsync();
    var (viewerClient, viewerDeviceId) = await BootstrapAsync(DeviceTypes.FlutterViewer, DevicePlatforms.Android);
    await PairDevicesAsync(await GetPublisherDeviceIdAsync(sessionId), viewerDeviceId);
    await ownerClient.PostAsync($"/api/sessions/{sessionId}/end", null);

    var body = await ReadJsonAsync(await viewerClient.GetAsync("/api/sessions/discoverable"));

    Assert.DoesNotContain(body.EnumerateArray(), x => x.GetProperty("sessionId").GetGuid() == sessionId);
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test SonicRelay.sln --filter FullyQualifiedName~Discoverable`
Expected: FAIL with `404` — the route does not exist yet.

- [ ] **Step 3: Implement the endpoint**

In `MapSessionEndpoints`, add the route immediately after the `/active` line:

```csharp
group.MapGet("/discoverable", GetDiscoverableAsync).RequireAuthorization("session:join");
```

Then add the handler next to `GetActiveAsync`:

```csharp
// Sessions a paired viewer is allowed to join without a code. The pairing is the
// authorization; the join code only ever proved the viewer could read the publisher's
// screen, which an active pairing establishes more strongly. No code is projected here
// — it is a separate short-lived secret and discovery must not become a way to read it.
private static async Task<IResult> GetDiscoverableAsync(ClaimsPrincipal principal, AppDbContext db,
    CancellationToken ct)
{
    var device = await DeviceIdentityEndpoints.RequireDeviceAsync(principal, db, ct);
    if (device is null) return Results.Unauthorized();

    var sessions = await db.StreamSessions.AsNoTracking()
        .Where(x => (x.Status == SessionStatuses.Waiting || x.Status == SessionStatuses.Active)
            && db.DevicePairings.Any(p => p.PublisherDeviceId == x.SourceDeviceId
                && p.ViewerDeviceId == device.Id
                && p.Status == DevicePairingStatuses.Active))
        .OrderByDescending(x => x.CreatedAt)
        .Select(x => new
        {
            SessionId = x.Id,
            PublisherDeviceId = x.SourceDeviceId,
            PublisherDeviceName = db.DeviceIdentities
                .Where(d => d.Id == x.SourceDeviceId).Select(d => d.Name).FirstOrDefault(),
            x.Status,
            x.MaxViewers,
            x.CreatedAt,
            ViewerCount = db.SessionParticipants.Count(p => p.SessionId == x.Id
                && p.Role == ParticipantRoles.Viewer
                && p.Status == ParticipantStatuses.Connected)
        })
        .ToListAsync(ct);

    return Results.Ok(sessions);
}
```

`DeviceIdentity.Name` is a non-nullable `string` defaulting to empty
(`src/SonicRelay.Domain/DeviceIdentities/DeviceIdentity.cs:5`), so
`publisherDeviceName` may be `""` for a device registered without a name, but
never `null`. The Flutter client binds to that exact property name.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test SonicRelay.sln --filter FullyQualifiedName~Discoverable`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add services/SonicRelay.Api/Endpoints/SessionEndpoints.cs tests/SonicRelay.Api.IntegrationTests/SessionEndpointsTests.cs
git commit -m "Add GET /api/sessions/discoverable for paired viewers

/active only returns sessions the caller already participates in, so a paired
viewer could not see that its publisher was broadcasting until after it had
joined. Discovery lists Waiting/Active sessions of actively paired publishers
and deliberately projects no join code."
```

---

### Task 4: Join a discovered session by id

**Files:**
- Modify: `services/SonicRelay.Api/Endpoints/SessionEndpoints.cs` (route table, new `JoinByIdAsync`, extracted `AdmitViewerAsync`)
- Test: `tests/SonicRelay.Api.IntegrationTests/SessionEndpointsTests.cs`

**Interfaces:**
- Consumes: `NotPaired()` and `InvalidCode()` from Task 2; `GET /api/sessions/discoverable` from Task 3 supplies the `sessionId`.
- Produces: `POST /api/sessions/{sessionId:guid}/join` → `200` with the same
  body shape `ToResponse(session)` returns for the code path (no `code`
  field), `403 not_paired`, `404 invalid_code`, or `409` on the viewer limit.

- [ ] **Step 1: Write the failing tests**

Append to `SessionEndpointsTests`:

```csharp
[Fact]
public async Task Join_by_id_admits_an_actively_paired_viewer()
{
    var (_, sessionId, _) = await CreateSessionAsync();
    var (viewerClient, viewerDeviceId) = await BootstrapAsync(DeviceTypes.FlutterViewer, DevicePlatforms.Android);
    await PairDevicesAsync(await GetPublisherDeviceIdAsync(sessionId), viewerDeviceId);

    var response = await viewerClient.PostAsync($"/api/sessions/{sessionId}/join", null);
    var body = await ReadJsonAsync(response);

    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    Assert.Equal(sessionId, body.GetProperty("id").GetGuid());
    Assert.Equal(SessionStatuses.Active, body.GetProperty("status").GetString());
}

[Fact]
public async Task Join_by_id_rejects_an_unpaired_viewer()
{
    var (_, sessionId, _) = await CreateSessionAsync();
    var (viewerClient, _) = await BootstrapAsync(DeviceTypes.FlutterViewer, DevicePlatforms.Android);

    var response = await viewerClient.PostAsync($"/api/sessions/{sessionId}/join", null);

    Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    Assert.Equal("not_paired", (await ReadJsonAsync(response)).GetProperty("code").GetString());
}

[Fact]
public async Task Join_by_id_is_idempotent_for_an_existing_participant()
{
    var (_, sessionId, code) = await CreateSessionAsync();
    var (viewerClient, viewerDeviceId) = await BootstrapAsync(DeviceTypes.FlutterViewer, DevicePlatforms.Android);
    await PairDevicesAsync(await GetPublisherDeviceIdAsync(sessionId), viewerDeviceId);
    await viewerClient.PostAsJsonAsync("/api/sessions/join", new { code });

    var response = await viewerClient.PostAsync($"/api/sessions/{sessionId}/join", null);

    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    await using var scope = _factory.Services.CreateAsyncScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    Assert.Single(await db.SessionParticipants
        .Where(x => x.SessionId == sessionId && x.DeviceId == viewerDeviceId && x.Role == ParticipantRoles.Viewer)
        .ToListAsync());
}

[Fact]
public async Task Join_by_id_enforces_the_viewer_limit()
{
    var (_, sessionId, _) = await CreateSessionAsync(maxViewers: 1);
    var publisherId = await GetPublisherDeviceIdAsync(sessionId);
    var (firstClient, firstViewerId) = await BootstrapAsync(DeviceTypes.FlutterViewer, DevicePlatforms.Android);
    var (secondClient, secondViewerId) = await BootstrapAsync(DeviceTypes.FlutterViewer, DevicePlatforms.Android);
    await PairDevicesAsync(publisherId, firstViewerId);
    await PairDevicesAsync(publisherId, secondViewerId);
    Assert.Equal(HttpStatusCode.OK, (await firstClient.PostAsync($"/api/sessions/{sessionId}/join", null)).StatusCode);

    var response = await secondClient.PostAsync($"/api/sessions/{sessionId}/join", null);

    Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
}

[Fact]
public async Task Join_by_id_rejects_an_unknown_session()
{
    var (viewerClient, _) = await BootstrapAsync(DeviceTypes.FlutterViewer, DevicePlatforms.Android);

    var response = await viewerClient.PostAsync($"/api/sessions/{Guid.NewGuid()}/join", null);

    Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    Assert.Equal("invalid_code", (await ReadJsonAsync(response)).GetProperty("code").GetString());
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test SonicRelay.sln --filter FullyQualifiedName~Join_by_id`
Expected: FAIL with `404` (no such route) or `405`.

- [ ] **Step 3: Extract the shared admission logic**

The code path and the id path must not drift, so pull everything `JoinAsync`
does after it has resolved a live session into one helper. Add to
`SessionEndpoints`:

```csharp
// Shared by both join paths (code and session id): everything that happens once a live
// session has been resolved. Keeping it in one place is what stops the two entry points
// from drifting on pairing, viewer-limit or reconnect semantics.
private static async Task<IResult> AdmitViewerAsync(StreamSession session, DeviceIdentity device,
    AppDbContext db, ILoggerFactory loggerFactory, CancellationToken ct)
{
    var now = DateTimeOffset.UtcNow;
    var logger = loggerFactory.CreateLogger("SonicRelay.Sessions");

    var existing = await db.SessionParticipants.SingleOrDefaultAsync(x => x.SessionId == session.Id
        && x.DeviceId == device.Id && x.Role == ParticipantRoles.Viewer, ct);
    if (existing is not null)
    {
        existing.Status = ParticipantStatuses.Connected;
        existing.LeftAt = null;
        await db.SaveChangesAsync(ct);
        logger.LogInformation(
            "Reconnected participant {ParticipantId} to session {SessionId} from device {DeviceId}",
            existing.Id, session.Id, device.Id);
        return Results.Ok(ToResponse(session));
    }

    if (!await HasActivePairingAsync(db, session.SourceDeviceId, device.Id, ct))
        return NotPaired();

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
    logger.LogInformation(
        "Joined session {SessionId} as participant {ParticipantId} from device {DeviceId}",
        session.Id, participant.Id, device.Id);
    return Results.Ok(ToResponse(session));
}
```

Now delete everything in `JoinAsync` from `var existing = await db.SessionParticipants...`
to the end of the method and replace it with:

```csharp
        return await AdmitViewerAsync(session, device, db, loggerFactory, ct);
    }
```

`JoinAsync` keeps its own code normalisation, `RedeemAsync`, and the
expiry/ended checks above that point, all unchanged.

- [ ] **Step 4: Implement the join-by-id endpoint**

Add the route after the `/join` line in `MapSessionEndpoints`:

```csharp
group.MapPost("/{sessionId:guid}/join", JoinByIdAsync).RequireAuthorization("session:join").RequireRateLimiting("join-session");
```

And the handler:

```csharp
// Code-free join for a session the caller found through /discoverable. It runs exactly the
// checks the code path runs after redemption; the active pairing is the authorization.
// A session the caller cannot see is reported as invalid_code rather than not_paired, so
// this endpoint cannot be used to probe which session ids exist.
private static async Task<IResult> JoinByIdAsync(Guid sessionId, ClaimsPrincipal principal, AppDbContext db,
    ILoggerFactory loggerFactory, CancellationToken ct)
{
    var device = await DeviceIdentityEndpoints.RequireDeviceAsync(principal, db, ct);
    if (device is null) return Results.Unauthorized();

    var session = await db.StreamSessions.SingleOrDefaultAsync(x => x.Id == sessionId, ct);
    if (session is null || session.Status is SessionStatuses.Ended or SessionStatuses.Expired)
        return InvalidCode();

    return await AdmitViewerAsync(session, device, db, loggerFactory, ct);
}
```

Note `session.CodeExpiresAt` is deliberately not checked here: it bounds the
join *code*, not the session, and this path uses no code. A session stays
joinable by a paired viewer for as long as it is Waiting or Active.

- [ ] **Step 5: Run the full suite**

Run: `dotnet test SonicRelay.sln`
Expected: PASS — the new join-by-id tests and every pre-existing session test,
which now exercise `AdmitViewerAsync` through the code path.

- [ ] **Step 6: Commit**

```bash
git add services/SonicRelay.Api/Endpoints/SessionEndpoints.cs tests/SonicRelay.Api.IntegrationTests/SessionEndpointsTests.cs
git commit -m "Add POST /api/sessions/{id}/join for discovered sessions

A paired viewer that found a session through /discoverable should not have to
read a code off the publisher's screen — the active pairing already carries
more authority than the code does. Both join paths now share AdmitViewerAsync
so pairing, viewer-limit and reconnect semantics cannot drift between them."
```

---

## Verification

After Task 4, confirm the whole slice:

```bash
dotnet test SonicRelay.sln
```

Expected: all tests pass, including the pre-existing signaling and device
identity suites.

Then confirm no dangling references to the removed feature:

```bash
grep -rn "RelaySettings\|settings/relay" --include=*.cs services/ src/ tests/
```

Expected: no output.
