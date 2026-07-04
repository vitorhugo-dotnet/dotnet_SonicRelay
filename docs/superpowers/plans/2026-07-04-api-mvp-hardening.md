# SonicRelay API MVP Hardening Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add single-instance rate limiting, expired-session cleanup, terminal signaling enforcement, safe structured logs, and integration coverage.

**Architecture:** Use ASP.NET Core fixed-window named policies with IP or authenticated-user partitions. Run one configurable `BackgroundService` that creates a scope per cleanup pass, retains expired sessions, removes their codes, and deletes only stale disconnected participants. Keep signaling in-process and validate persisted terminal state both at admission and immediately before message routing.

**Tech Stack:** .NET 10, ASP.NET Core Minimal APIs/RateLimiting/WebSockets, EF Core, xUnit, `WebApplicationFactory`.

---

### Task 1: Rate-limit policies and auth coverage

**Files:**
- Modify: `services/SonicRelay.Api/Program.cs`
- Modify: `services/SonicRelay.Api/Endpoints/AuthEndpoints.cs`
- Modify: `tests/SonicRelay.Api.IntegrationTests/SonicRelayApiFactory.cs`
- Modify: `tests/SonicRelay.Api.IntegrationTests/AuthEndpointsTests.cs`

- [ ] **Step 1: Write failing login and refresh limit tests**

Add isolated factory instances with permit limit `1`, submit two requests from the same client partition, and assert the second response is `429 TooManyRequests`. Use distinct registered users so login behavior, not account state, is under test; obtain a valid refresh token before exercising refresh.

- [ ] **Step 2: Verify the auth tests fail for missing limiting**

Run: `dotnet test tests/SonicRelay.Api.IntegrationTests/SonicRelay.Api.IntegrationTests.csproj --filter "FullyQualifiedName~AuthEndpointsTests.Login_is_rate_limited|FullyQualifiedName~AuthEndpointsTests.Refresh_is_rate_limited" --verbosity minimal`

Expected: both tests fail because the second requests are not HTTP 429.

- [ ] **Step 3: Register fixed-window policies and attach Identity route metadata**

In `Program.cs`, call `AddRateLimiter` with named `login`, `refresh`, `create-session`, `join-session`, and `rotate-code` policies. Read each permit/window from `RateLimits:*`, use `RemoteIpAddress` for auth partitions and `ClaimTypes.NameIdentifier` with IP fallback for authenticated policies, set `QueueLimit = 0`, add a structured rejection log, and add `UseRateLimiter` after authentication.

In `AuthEndpoints.cs`, retain the convention builder returned by `MapIdentityApi<ApplicationUser>()`; add `EnableRateLimitingAttribute("login")` or `("refresh")` to route endpoint builders whose raw route pattern ends with `/login` or `/refresh`.

- [ ] **Step 4: Verify focused auth tests pass**

Run the Step 2 command. Expected: 2 passed, 0 failed.

- [ ] **Step 5: Commit rate limiting foundation**

Commit message: `feat: rate limit authentication endpoints`.

### Task 2: Session mutation limits and lifecycle logs

**Files:**
- Modify: `services/SonicRelay.Api/Endpoints/SessionEndpoints.cs`
- Modify: `tests/SonicRelay.Api.IntegrationTests/SessionEndpointsTests.cs`

- [ ] **Step 1: Write failing create, join, and rotate limit tests**

For each route, use a factory configured with permit limit `1`, establish valid prerequisites before consuming the tested policy, issue two valid requests from the same authenticated user partition, and assert the second is HTTP 429. Add a separate-user assertion proving authenticated partitions are independent.

- [ ] **Step 2: Verify session limiter tests fail**

Run: `dotnet test tests/SonicRelay.Api.IntegrationTests/SonicRelay.Api.IntegrationTests.csproj --filter "FullyQualifiedName~SessionEndpointsTests.Create_is_rate_limited|FullyQualifiedName~SessionEndpointsTests.Join_is_rate_limited|FullyQualifiedName~SessionEndpointsTests.Rotate_code_is_rate_limited" --verbosity minimal`

Expected: failures because limits are not attached.

- [ ] **Step 3: Attach policies and structured metadata-only logs**

Call `RequireRateLimiting` on create, join, and rotate endpoint builders. Inject `ILoggerFactory` only where lifecycle events occur and log session/user/device IDs and status for create, join, rotate, end, and join-detected expiry; do not log request objects or generated/redeemed codes.

- [ ] **Step 4: Verify focused session limiter tests pass**

Run the Step 2 command. Expected: 3 passed, 0 failed.

- [ ] **Step 5: Commit session limiter behavior**

Commit message: `feat: rate limit session mutations`.

### Task 3: Cleanup expired sessions and stale participants

**Files:**
- Create: `services/SonicRelay.Api/Services/SessionCleanupService.cs`
- Modify: `services/SonicRelay.Api/Program.cs`
- Modify: `tests/SonicRelay.Api.IntegrationTests/SonicRelayApiFactory.cs`
- Modify: `tests/SonicRelay.Api.IntegrationTests/SessionEndpointsTests.cs`

- [ ] **Step 1: Write a failing cleanup integration test**

Seed one elapsed active session with a connected participant, one stale disconnected participant, and one recent disconnected participant. Invoke one cleanup pass and assert: the session row remains and is `expired`; its join code no longer redeems; only the stale disconnected participant is deleted.

- [ ] **Step 2: Verify cleanup test fails because the service is missing**

Run: `dotnet test tests/SonicRelay.Api.IntegrationTests/SonicRelay.Api.IntegrationTests.csproj --filter "FullyQualifiedName~SessionEndpointsTests.Cleanup_expires_sessions_and_removes_only_stale_disconnected_participants" --verbosity minimal`

Expected: compile failure or failing resolution because `SessionCleanupService` does not exist.

- [ ] **Step 3: Implement one scoped cleanup worker**

Create a `BackgroundService` with public `CleanupOnceAsync(CancellationToken)`. On each pass, create a scope, query waiting/active elapsed sessions, mark them expired, delete disconnected participants with non-null `LeftAt` older than `Sessions:DisconnectedParticipantRetentionHours`, save once, then remove each expired session code. `ExecuteAsync` exits when `Sessions:CleanupEnabled` is false; otherwise it invokes cleanup immediately and waits `Sessions:CleanupIntervalSeconds` between passes. Log counts and exceptions using structured templates.

Register it once as a singleton and as the hosted service in `Program.cs`. Set `Sessions:CleanupEnabled=false` in the test factory so tests invoke deterministic passes.

- [ ] **Step 4: Verify focused cleanup test passes**

Run the Step 2 command. Expected: 1 passed, 0 failed.

- [ ] **Step 5: Commit cleanup service**

Commit message: `feat: clean up expired sessions`.

### Task 4: Terminal signaling and payload-safe logs

**Files:**
- Modify: `services/SonicRelay.Api/Endpoints/SignalingWebSocketEndpoint.cs`
- Modify: `tests/SonicRelay.Api.IntegrationTests/SignalingWebSocketTests.cs`

- [ ] **Step 1: Write failing terminal-state and log tests**

Add tests asserting ended and elapsed sessions reject WebSocket admission with HTTP 410; an established socket receives `session.ended` then closes after the database status becomes ended; and captured signaling logs do not contain sentinel SDP/ICE strings. Add a route-race test that changes session status before sending and asserts the intended recipient receives nothing.

- [ ] **Step 2: Verify the new signaling tests expose missing behavior**

Run: `dotnet test tests/SonicRelay.Api.IntegrationTests/SonicRelay.Api.IntegrationTests.csproj --filter "FullyQualifiedName~SignalingWebSocketTests" --verbosity minimal`

Expected: at least the pre-routing terminal-state/log-capture coverage fails before implementation.

- [ ] **Step 3: Enforce terminal state immediately before dispatch**

Pass `AppDbContext` into message handling, query terminal state before serializing or dispatching routed payloads, send only `session.ended`, and return a loop-control result that closes the receive loop. Add structured connect, disconnect, admission-rejection, and terminal-close logs containing only IDs, status, and message type. Preserve the existing metadata-only routing log and never interpolate or destructure the JSON payload.

- [ ] **Step 4: Verify signaling tests pass**

Run the Step 2 command. Expected: all signaling tests pass with 0 failures.

- [ ] **Step 5: Commit signaling hardening**

Commit message: `feat: enforce terminal signaling state`.

### Task 5: Final verification and E2E

**Files:**
- Review all modified files; no new scope.

- [ ] **Step 1: Run formatting and build checks**

Run: `dotnet format SonicRelay.sln --verify-no-changes --no-restore`

Run: `dotnet build SonicRelay.sln --no-restore --verbosity minimal`

Expected: both exit 0.

- [ ] **Step 2: Run the relevant integration project**

Run: `dotnet test tests/SonicRelay.Api.IntegrationTests/SonicRelay.Api.IntegrationTests.csproj --no-build --verbosity minimal`

Expected: all tests pass, 0 failed.

- [ ] **Step 3: Run repository E2E non-interactively**

Use the repository's documented Compose workflow with detached startup, execute health/auth/session/signaling smoke coverage without prompts, and always tear down started containers. If the repository has no executable E2E harness, use the integration project as the API E2E workflow and explicitly report that limitation rather than inventing an untracked script.

- [ ] **Step 4: Review requirements and diff**

Run `git diff --check`, inspect `git status --short`, and compare each design requirement to implementation/tests. Do not include `.vs`, build output, or unrelated files.

- [ ] **Step 5: Commit verification-only adjustments if needed**

Commit only necessary formatting/test corrections with message `test: cover API MVP hardening`.
