# Device Identity Phase 3 Windows Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace Windows human login with secure device bootstrap/token authentication and add code plus QR pairing management.

**Architecture:** Store one DPAPI-protected `DeviceCredential` atomically. A single-flight `DeviceIdentitySession` supplies short-lived tokens through a Core interface to HTTP, signaling, and TURN. Pairing challenge/QR state remains separate from streaming session-code state.

**Tech Stack:** .NET 10, WinUI 3, Windows App SDK 2.2, xUnit, DPAPI, QRCoder 1.8.0.

## Global Constraints

- Use branch `codex/issue-26-phase3` in `windows_SonicRelay`.
- Add only `QRCoder` 1.8.0; do not update unrelated packages.
- Bootstrap `windows_publisher/windows`.
- Never send `deviceId` in session bodies or signaling queries.
- Never use Identity or `/auth/refresh` as fallback.
- Never persist QR payloads or log credentials, tokens, codes, SDP, or complete ICE candidates.
- Keep pairing-code and session-code state in separate models.

---

### Task 1: Durable device credential storage

**Files:**
- Create: `src/SonicRelay.Windows.Core/Storage/DeviceIdentity/DeviceCredential.cs`
- Create: `src/SonicRelay.Windows.Core/Storage/DeviceIdentity/IDeviceCredentialStore.cs`
- Create: `src/SonicRelay.Windows.Core/Storage/DeviceIdentity/DeviceCredentialStorageResult.cs`
- Create: `src/SonicRelay.Windows.Core/Storage/DeviceIdentity/UserScopedDeviceCredentialStore.cs`
- Create: `src/SonicRelay.Windows.Core/Authentication/IDeviceAccessTokenProvider.cs`
- Create: `tests/SonicRelay.Windows.Core.Tests/DeviceCredentialStoreTests.cs`

**Interfaces:**
- Produces: `DeviceCredential(Guid DeviceId, string CredentialSecret, int CredentialVersion, string DeviceType, string Platform)`.
- Produces: `IDeviceCredentialStore.SaveAsync`, `LoadAsync`, `DeleteAsync`.
- Produces: `IDeviceAccessTokenProvider.GetAccessTokenAsync(bool forceRefresh = false, CancellationToken cancellationToken = default)`.

- [ ] **Step 1: Write RED atomic storage tests**

```csharp
[Fact]
public async Task Save_load_and_delete_round_trip_one_atomic_credential()
{
    var store = CreateStore();
    var expected = new DeviceCredential(DeviceId, "secret", 1,
        "windows_publisher", "windows");
    Assert.True((await store.SaveAsync(expected)).Succeeded);
    Assert.Equal(expected, (await store.LoadAsync()).Credential);
    Assert.True((await store.DeleteAsync()).Succeeded);
    Assert.Null((await store.LoadAsync()).Credential);
}
```

Also prove a failed temporary write leaves the old credential readable and
errors never include `CredentialSecret`.

- [ ] **Step 2: Run and verify RED**

```powershell
dotnet test tests/SonicRelay.Windows.Core.Tests/SonicRelay.Windows.Core.Tests.csproj --filter "FullyQualifiedName~DeviceCredentialStoreTests" --no-restore --verbosity minimal
```

Expected: compile failure because the new types do not exist.

- [ ] **Step 3: Implement the protected atomic-file pattern**

Use `device-credential.dat`, existing `ITokenProtector`, a `.tmp` write, and
`File.Move(..., overwrite: true)`. Do not reuse `TokenSet` or `tokens.dat`.

- [ ] **Step 4: Run GREEN and commit**

```powershell
dotnet test tests/SonicRelay.Windows.Core.Tests/SonicRelay.Windows.Core.Tests.csproj --filter "FullyQualifiedName~DeviceCredentialStoreTests" --no-restore --verbosity minimal
git add src/SonicRelay.Windows.Core/Storage/DeviceIdentity src/SonicRelay.Windows.Core/Authentication tests/SonicRelay.Windows.Core.Tests/DeviceCredentialStoreTests.cs
git commit -m "feat(windows): persist device credentials securely"
```

### Task 2: Bootstrap and single-flight token provider

**Files:**
- Create: `src/SonicRelay.Windows.ApiClient/DeviceIdentity/DeviceIdentityContracts.cs`
- Create: `src/SonicRelay.Windows.ApiClient/DeviceIdentity/DeviceIdentityApiClient.cs`
- Create: `src/SonicRelay.Windows.ApiClient/DeviceIdentity/DeviceIdentitySession.cs`
- Create: `tests/SonicRelay.Windows.ApiClient.Tests/DeviceIdentitySessionTests.cs`

**Interfaces:**
- Consumes: `IDeviceCredentialStore`, raw `HttpClient`.
- Produces: `IDeviceIdentityApiClient.BootstrapAsync` and `TokenAsync`.
- Produces: `DeviceIdentitySession : IDeviceAccessTokenProvider`.

- [ ] **Step 1: Write RED lifecycle/concurrency tests**

Cover absent credential bootstrap, stored credential exchange, 30-second
expiry margin, forced renewal, five concurrent callers/one exchange, `401`
clearing the credential, and network failure retaining it.

```csharp
var tokens = await Task.WhenAll(Enumerable.Range(0, 5)
    .Select(_ => session.GetAccessTokenAsync()));
Assert.Single(tokens.Distinct());
Assert.Equal(1, api.TokenCalls);
```

- [ ] **Step 2: Run and verify RED**

```powershell
dotnet test tests/SonicRelay.Windows.ApiClient.Tests/SonicRelay.Windows.ApiClient.Tests.csproj --filter "FullyQualifiedName~DeviceIdentitySessionTests" --no-restore --verbosity minimal
```

- [ ] **Step 3: Implement explicit contracts and serialization**

```csharp
public sealed record BootstrapDeviceRequest(string Name, string Type, string Platform);
public sealed record BootstrapDeviceResponse(Guid DeviceId,
    string CredentialSecret, int CredentialVersion);
public sealed record DeviceTokenRequest(Guid DeviceId, string CredentialSecret);
public sealed record DeviceTokenResponse(string AccessToken, int ExpiresIn);
```

Use `SemaphoreSlim` around load/bootstrap/exchange. Persist the credential
before returning. Cache token/absolute expiry only in memory. Never bootstrap
as a response to transport failure.

- [ ] **Step 4: Run GREEN and commit**

```powershell
dotnet test tests/SonicRelay.Windows.ApiClient.Tests/SonicRelay.Windows.ApiClient.Tests.csproj --filter "FullyQualifiedName~DeviceIdentitySessionTests" --no-restore --verbosity minimal
git add src/SonicRelay.Windows.ApiClient/DeviceIdentity tests/SonicRelay.Windows.ApiClient.Tests/DeviceIdentitySessionTests.cs
git commit -m "feat(windows): bootstrap device identity tokens"
```

### Task 3: DeviceBearer HTTP, sessions, signaling, and TURN

**Files:**
- Modify: `src/SonicRelay.Windows.ApiClient/ApiHttpClient.cs`
- Modify: `src/SonicRelay.Windows.ApiClient/Sessions/SessionContracts.cs`
- Modify: `src/SonicRelay.Windows.ApiClient/Sessions/SessionApiClient.cs`
- Modify: `src/SonicRelay.Windows.ApiClient/WebRtc/WebRtcApiClient.cs`
- Modify: `src/SonicRelay.Windows.ApiClient/WebRtc/BackendIceServersProvider.cs`
- Modify: `src/SonicRelay.Windows.Signaling/ISignalingClient.cs`
- Modify: `src/SonicRelay.Windows.Signaling/SignalingClient.cs`
- Modify: `src/SonicRelay.Windows.Signaling/SonicRelay.Windows.Signaling.csproj`
- Modify: `tests/SonicRelay.Windows.ApiClient.Tests/ApiRequestTests.cs`
- Modify: `tests/SonicRelay.Windows.ApiClient.Tests/BackendIceServersProviderTests.cs`
- Modify: `tests/SonicRelay.Windows.Signaling.Tests/SignalingClientTests.cs`
- Modify: `tests/SonicRelay.Windows.Presentation.Tests/PublisherWorkflowTests.cs`
- Modify: `tests/SonicRelay.Windows.WebRtc.Tests/WebRtcPublisherTests.cs`

**Interfaces:**
- Consumes: `IDeviceAccessTokenProvider.GetAccessTokenAsync`.
- Produces: `CreateSessionRequest(int? MaxViewers = null)`.
- Produces: `ISignalingClient.ConnectAsync(string sessionId, CancellationToken)`.

- [ ] **Step 1: Write RED wire-contract tests**

Assert exact JSON `{ "maxViewers": 3 }`, DTO constructors without
`OwnerUserId`, and exact URI:

```csharp
Assert.Equal("wss://signal.example/ws?tenant=blue&sessionId=session%20one",
    socket.ConnectedUri?.AbsoluteUri);
```

Return `token-1` then `token-2` from the provider and prove reconnect uses
`token-2`.

- [ ] **Step 2: Run and verify RED**

```powershell
dotnet test tests/SonicRelay.Windows.ApiClient.Tests/SonicRelay.Windows.ApiClient.Tests.csproj --filter "FullyQualifiedName~ApiRequestTests|FullyQualifiedName~BackendIceServersProviderTests" --no-restore --verbosity minimal
dotnet test tests/SonicRelay.Windows.Signaling.Tests/SonicRelay.Windows.Signaling.Tests.csproj --filter "FullyQualifiedName~SignalingClientTests" --no-restore --verbosity minimal
```

- [ ] **Step 3: Replace token-store coupling and legacy wire fields**

`ApiHttpClient` gets tokens from the provider. Permit one forced exchange and
replay only when a call site sets `replaySafe: true`; POST side effects default
false. Remove signaling `deviceId` state/query. Resolve a token inside every
socket-open attempt and each TURN request.

- [ ] **Step 4: Run GREEN and commit**

```powershell
dotnet test tests/SonicRelay.Windows.ApiClient.Tests/SonicRelay.Windows.ApiClient.Tests.csproj --no-restore --verbosity minimal
dotnet test tests/SonicRelay.Windows.Signaling.Tests/SonicRelay.Windows.Signaling.Tests.csproj --no-restore --verbosity minimal
git add src/SonicRelay.Windows.ApiClient src/SonicRelay.Windows.Signaling tests/SonicRelay.Windows.ApiClient.Tests tests/SonicRelay.Windows.Signaling.Tests
git commit -m "feat(windows): use device bearer for streaming"
```

### Task 4: Pairing API and QR rendering

**Files:**
- Create: `src/SonicRelay.Windows.ApiClient/Pairing/PairingContracts.cs`
- Create: `src/SonicRelay.Windows.ApiClient/Pairing/PairingApiClient.cs`
- Create: `src/SonicRelay.Windows.Presentation/Pairing/PairingViewModel.cs`
- Create: `src/SonicRelay.Windows.Presentation/Pairing/PairingQrCodeService.cs`
- Create: `src/SonicRelay.Windows.App/Controls/PairingCard.xaml`
- Create: `src/SonicRelay.Windows.App/Controls/PairingCard.xaml.cs`
- Modify: `src/SonicRelay.Windows.Presentation/SonicRelay.Windows.Presentation.csproj`
- Modify: `src/SonicRelay.Windows.App/Pages/ConnectionPage.xaml`
- Modify: `src/SonicRelay.Windows.App/Pages/ConnectionPage.xaml.cs`
- Create: `tests/SonicRelay.Windows.ApiClient.Tests/PairingApiClientTests.cs`
- Create: `tests/SonicRelay.Windows.Presentation.Tests/PairingViewModelTests.cs`
- Create: `tests/SonicRelay.Windows.Presentation.Tests/PairingQrCodeServiceTests.cs`

**Interfaces:**
- Produces: `CreatePairingChallengeAsync`, `ListPairingsAsync(Guid)`, `RevokePairingAsync(Guid)`.
- Produces: `PairingChallengeState(ChallengeId, Code, QrPayload, ExpiresAt)` independent of `SessionCode`.

- [ ] **Step 1: Write RED API, QR, and state-isolation tests**

Assert endpoint paths; challenge refresh never changes session code; QR input
equals the API `QrPayload`; rendering returns bytes without a file write.

- [ ] **Step 2: Add QRCoder and implement memory-only rendering**

```xml
<PackageReference Include="QRCoder" Version="1.8.0" />
```

Reference QRCoder from the Presentation project. Use `QRCodeGenerator` plus
`PngByteQRCode` in `PairingQrCodeService` and return PNG bytes. The App control
loads those bytes into a WinUI `BitmapImage` from an in-memory stream. Dispose
QR objects and clear expired images.

- [ ] **Step 3: Implement create/refresh/list/confirmed revoke**

Use labels `Pairing code` and `Session join code`. Treat `qrPayload` as opaque
and never store it.

- [ ] **Step 4: Run GREEN and commit**

```powershell
dotnet test tests/SonicRelay.Windows.ApiClient.Tests/SonicRelay.Windows.ApiClient.Tests.csproj --filter "FullyQualifiedName~Pairing" --no-restore --verbosity minimal
dotnet test tests/SonicRelay.Windows.Presentation.Tests/SonicRelay.Windows.Presentation.Tests.csproj --filter "FullyQualifiedName~Pairing" --no-restore --verbosity minimal
git add src tests
git commit -m "feat(windows): display secure pairing QR"
```

### Task 5: Login-free composition and documentation

**Files:**
- Modify: `src/SonicRelay.Windows.App/App.xaml.cs`
- Modify: `src/SonicRelay.Windows.Presentation/PublisherWorkflow.cs`
- Modify: `src/SonicRelay.Windows.Presentation/PublisherSnapshot.cs`
- Modify: `tests/SonicRelay.Windows.Presentation.Tests/PublisherWorkflowTests.cs`
- Modify: `tests/Repository.Structure.Tests.ps1`
- Modify: `README.md`

**Interfaces:**
- Consumes: Tasks 1-4.
- Produces: startup without `/auth/login`, `/auth/register`, `/auth/me`, or `/auth/refresh`.

- [ ] **Step 1: Write RED composition/workflow tests**

Prove startup requests a device token, never calls `IAuthApiClient`, creates a
session without device ID, and reconnects signaling with session ID only.

- [ ] **Step 2: Replace active Identity composition**

Register `UserScopedDeviceCredentialStore`, `DeviceIdentityApiClient`, and one
shared `DeviceIdentitySession`. Legacy Identity code may remain only if
unreachable from production composition for Phase 4.

- [ ] **Step 3: Document and verify**

Document DPAPI storage, reset consequences, QR/manual pairing, separate
session code, and no account login.

```powershell
dotnet test tests/SonicRelay.Windows.Presentation.Tests/SonicRelay.Windows.Presentation.Tests.csproj --no-restore --verbosity minimal
dotnet build SonicRelay.Windows.slnx --no-restore --verbosity minimal
git diff --check
git add src tests README.md
git commit -m "feat(windows): start with device identity"
```
