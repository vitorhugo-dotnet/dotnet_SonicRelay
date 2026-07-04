# Device Endpoints Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Deliver authenticated, owner-isolated Device CRUD and revocation with explicit revoked-device rejection in session and signaling entry points.

**Architecture:** Minimal API handlers use request/response DTOs and owner-scoped EF Core queries. A small `DeviceAccess` helper centralizes active-device eligibility for session and WebSocket paths while device management can still read revoked records.

**Tech Stack:** .NET 10, ASP.NET Core Minimal APIs and Identity bearer authentication, EF Core, xUnit, `WebApplicationFactory`.

---

### Task 1: Device CRUD and ownership contract

**Files:**
- Create: `tests/SonicRelay.Api.IntegrationTests/DeviceEndpointsTests.cs`
- Create: `services/SonicRelay.Api/Contracts/DeviceContracts.cs`
- Modify: `services/SonicRelay.Api/Endpoints/DeviceEndpoints.cs`

- [ ] **Step 1: Write failing integration tests**

Add tests that register and authenticate two users, create devices through JSON DTOs, and assert create/list/get/patch/delete behavior. Assert user B receives `404` for user A's get, patch, delete, and revoke operations, and never sees A's device in list output.

- [ ] **Step 2: Run the focused test class and verify RED**

Run: `dotnet test tests/SonicRelay.Api.IntegrationTests/SonicRelay.Api.IntegrationTests.csproj --filter FullyQualifiedName~DeviceEndpointsTests --no-restore`

Expected: failures because placeholder handlers do not persist, filter, or map devices.

- [ ] **Step 3: Add DTOs and minimal owner-scoped handlers**

Define these API-only records in `DeviceContracts.cs`:

```csharp
public sealed record CreateDeviceRequest(string? Name, string? Type, string? Platform, string? PublicKey);
public sealed record UpdateDeviceRequest(string? Name, string? PublicKey);
public sealed record DeviceResponse(Guid Id, string Name, string Type, string Platform,
    string? PublicKey, bool Trusted, bool Revoked, DateTimeOffset? LastSeenAt, DateTimeOffset CreatedAt);
```

Replace placeholder handlers with async EF handlers that resolve the current Identity user, always scope record queries by `OwnerUserId`, map through `DeviceResponse`, create IDs/timestamps server-side, and save changes. Treat an unknown or foreign ID identically as `404`.

- [ ] **Step 4: Run the focused tests and verify GREEN**

Run the Task 1 command. Expected: CRUD and ownership tests pass.

### Task 2: Input validation

**Files:**
- Modify: `tests/SonicRelay.Api.IntegrationTests/DeviceEndpointsTests.cs`
- Modify: `services/SonicRelay.Api/Endpoints/DeviceEndpoints.cs`

- [ ] **Step 1: Write failing validation tests**

Cover blank and over-120-character names, unknown device types, unknown platforms, publisher/non-Windows combinations, viewer/Windows combinations, and empty PATCH bodies. Assert `400 Bad Request`.

- [ ] **Step 2: Run tests and verify RED**

Run the focused Device test command. Expected: invalid requests are currently accepted.

- [ ] **Step 3: Implement allowlist and combination validation**

Use `DeviceTypes` and `DevicePlatforms` constants. Permit only `windows_publisher/windows`, `flutter_viewer/android`, and `flutter_viewer/ios`. Reject invalid create requests and patches without a supplied field before persistence.

- [ ] **Step 4: Run tests and verify GREEN**

Run the focused Device test command. Expected: validation and prior CRUD tests pass.

### Task 3: Authentication and revocation

**Files:**
- Modify: `tests/SonicRelay.Api.IntegrationTests/DeviceEndpointsTests.cs`
- Modify: `services/SonicRelay.Api/Endpoints/DeviceEndpoints.cs`
- Modify: `services/SonicRelay.Api/Endpoints/SessionEndpoints.cs`
- Modify: `services/SonicRelay.Api/Endpoints/SignalingWebSocketEndpoint.cs`
- Create: `services/SonicRelay.Api/Services/DeviceAccess.cs`

- [ ] **Step 1: Write failing auth and revocation tests**

Assert all six device routes return `401` anonymously, revoke is idempotent and retained in GET responses, session creation rejects a revoked publisher, session join rejects a revoked viewer, and signaling rejects a revoked device before WebSocket acceptance.

- [ ] **Step 2: Run the focused tests and verify RED**

Run the focused Device test command. Expected: placeholder session/signaling routes accept revoked devices.

- [ ] **Step 3: Implement explicit active-device eligibility**

Add `DeviceAccess.FindActiveOwnedAsync`, which queries by device ID, current user ID, `Revoked == false`, expected type, and compatible platform. Use it in the documented session create/join device inputs and the signaling `deviceId` query before accepting a socket. Return `403 Forbidden` for an authenticated but ineligible device. Keep device-management queries independent so owners can see revoked devices.

- [ ] **Step 4: Run focused tests and verify GREEN**

Run the focused Device test command. Expected: all device, ownership, and revoked-use tests pass.

### Task 4: Targeted verification

**Files:**
- Modify only files above if verification exposes a defect.

- [ ] **Step 1: Run the integration project without rebuilding unrelated projects**

Run: `dotnet test tests/SonicRelay.Api.IntegrationTests/SonicRelay.Api.IntegrationTests.csproj --no-restore`

Expected: all API integration tests pass.

- [ ] **Step 2: Check formatting and diff scope**

Run: `dotnet format SonicRelay.sln --verify-no-changes --no-restore` and `git diff --check`.

Expected: both commands exit successfully and the diff contains only the listed device/session/signaling/test/documentation files.

- [ ] **Step 3: Summarize**

Report endpoint behavior, targeted test results, and a concise diff summary. Do not update dependencies or refactor unrelated code.
