# Device Identity Phase 3 Backend Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Require an active publisher/viewer device pairing for every new session join without disclosing whether the session or pairing exists.

**Architecture:** Keep the Phase 2 `{ code }` join contract. Resolve the authenticated viewer and live session as today, allow an existing participant to reconnect, then require an active `DevicePairing` before creating a new participant. Return the existing invalid-code response when the relationship is absent.

**Tech Stack:** .NET 10, ASP.NET Core minimal APIs, EF Core, xUnit, `WebApplicationFactory` integration tests.

## Global Constraints

- Work from PR #28 head `9b9a491b56189830626a2237faeb436b46752b47` or a descendant.
- Keep `POST /api/sessions/join` as `{ "code": "ABC123" }`.
- Absent, revoked, or wrong-publisher pairing returns the invalid/expired-code status and body.
- Pairing revocation does not disconnect or block reconnection of an existing participant.
- Never log pairing/session codes, QR payloads, credential secrets, or access tokens.
- Run only named focused tests until final verification.

---

### Task 1: Specify pairing-gated join behavior

**Files:**
- Modify: `tests/SonicRelay.Api.IntegrationTests/SessionEndpointsTests.cs`

**Interfaces:**
- Consumes: `DeviceIdentityTestHelper.BootstrapAndAuthorizeAsync`, `DevicePairing`, `AppDbContext`.
- Produces: `PairDevicesAsync(Guid publisherId, Guid viewerId, string status)` test helper.

- [ ] **Step 1: Add the pairing seed helper and RED tests**

```csharp
private async Task PairDevicesAsync(Guid publisherId, Guid viewerId,
    string status = DevicePairingStatuses.Active)
{
    await using var scope = _factory.Services.CreateAsyncScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.DevicePairings.Add(new DevicePairing
    {
        Id = Guid.NewGuid(),
        PublisherDeviceId = publisherId,
        ViewerDeviceId = viewerId,
        Status = status,
        CreatedAt = DateTimeOffset.UtcNow,
        RevokedAt = status == DevicePairingStatuses.Revoked
            ? DateTimeOffset.UtcNow : null
    });
    await db.SaveChangesAsync();
}
```

Add these tests:

```text
Join_accepts_an_actively_paired_viewer
Join_hides_absent_pairing_like_an_invalid_code
Join_rejects_a_viewer_paired_to_another_publisher
Join_rejects_a_revoked_pairing
Unpaired_attempt_does_not_block_the_paired_viewer
Existing_participant_can_reconnect_after_pairing_revocation
```

Compare both status and complete response body with a `ZZZZZZ` request for
the non-disclosure case. For reconnection, join while active, revoke through
EF, call join again as the same viewer, and expect `200 OK`.

- [ ] **Step 2: Run the focused tests and verify RED**

```powershell
dotnet test tests/SonicRelay.Api.IntegrationTests/SonicRelay.Api.IntegrationTests.csproj --filter "FullyQualifiedName~SessionEndpointsTests" --no-restore --verbosity minimal
```

Expected: the unpaired, revoked, and wrong-publisher tests fail because the
current handler accepts any viewer holding a valid session code.

- [ ] **Step 3: Commit the RED tests**

```powershell
git add tests/SonicRelay.Api.IntegrationTests/SessionEndpointsTests.cs
git commit -m "test(api): require device pairing for session join"
```

### Task 2: Enforce pairing for new participants

**Files:**
- Modify: `services/SonicRelay.Api/Endpoints/SessionEndpoints.cs`

**Interfaces:**
- Consumes: `DevicePairingStatuses.Active`, `StreamSession.SourceDeviceId`, viewer `DeviceIdentity.Id`.
- Produces: `HasActivePairingAsync(AppDbContext, Guid, Guid, CancellationToken)`.

- [ ] **Step 1: Add the minimal query**

```csharp
private static Task<bool> HasActivePairingAsync(AppDbContext db,
    Guid publisherId, Guid viewerId, CancellationToken ct) =>
    db.DevicePairings.AsNoTracking().AnyAsync(x =>
        x.PublisherDeviceId == publisherId
        && x.ViewerDeviceId == viewerId
        && x.Status == DevicePairingStatuses.Active, ct);
```

Add `using SonicRelay.Domain.DeviceIdentities;`.

- [ ] **Step 2: Check after existing-participant reconnection and before capacity mutation**

```csharp
if (existing is not null)
{
    // Keep the current reconnect branch unchanged.
}

if (!await HasActivePairingAsync(db, session.SourceDeviceId, device.Id, ct))
    return InvalidCode();
```

Do not check pairing during WebSocket reconnect or session reads. Do not
return `403`, `409`, or pairing-specific copy.

- [ ] **Step 3: Run session tests and verify GREEN**

```powershell
dotnet test tests/SonicRelay.Api.IntegrationTests/SonicRelay.Api.IntegrationTests.csproj --filter "FullyQualifiedName~SessionEndpointsTests" --no-restore --verbosity minimal
```

- [ ] **Step 4: Run adjacent pairing tests**

```powershell
dotnet test tests/SonicRelay.Api.IntegrationTests/SonicRelay.Api.IntegrationTests.csproj --filter "FullyQualifiedName~PairingChallengeTests|FullyQualifiedName~PairingManagementTests" --no-restore --verbosity minimal
```

Expected: all selected tests pass.

- [ ] **Step 5: Commit GREEN**

```powershell
git add services/SonicRelay.Api/Endpoints/SessionEndpoints.cs
git commit -m "feat(api): require active pairing for session join"
```

### Task 3: Document the authorization contract

**Files:**
- Modify: `docs/device-identity.md`
- Modify: `docs/security.md`
- Modify: `README.md`

**Interfaces:**
- Consumes: tested Task 2 behavior.
- Produces: public distinction between durable pairing and per-session code.

- [ ] **Step 1: Add the exact behavior**

```text
A new viewer participant needs both an active DevicePairing to the session's
source device and the current session join code. The API deliberately returns
the invalid/expired-code response when either condition is absent. Existing
participants may reconnect after pairing revocation until the session ends.
```

- [ ] **Step 2: Verify docs and diff**

```powershell
git diff --check
git grep -n -E "ownerUserId|deviceId=.*ws/signaling" -- README.md docs
```

Expected: diff check succeeds; no stale ownership or signaling query appears.

- [ ] **Step 3: Commit docs**

```powershell
git add README.md docs/device-identity.md docs/security.md
git commit -m "docs: require pairing before joining sessions"
```
