# Device Identity Auth (Phase 3) Implementation Plan

> **Required workflow:** execute each task test-first. Run the named focused
> test before implementation to observe the expected failure, implement the
> smallest change, then rerun it. Do not restore Identity as a fallback.

**Goal:** Adapt the Windows publisher and Flutter viewer to bootstrap and
persist a trusted device identity once, exchange its credential for
DeviceBearer access tokens, and use that identity for pairing, sessions,
signaling, and TURN without human login.

**Architecture:** Each client owns a secure `DeviceCredential` and a
single-flight `DeviceSession` token provider. API and WebSocket transports ask
that provider for a current short-lived token. Pairing challenges and session
join codes remain separate feature models. The Phase 2 backend contract is the
oracle and should not change unless a focused client test demonstrates a
blocking requirement.

**Repositories:** `windows_SonicRelay`, `flutter_SonicRelay`, and this backend
for contract verification/documentation. Full design:
`docs/superpowers/specs/2026-07-22-device-identity-phase3-design.md`.

## Global constraints

- Never send `deviceId` in session create/join bodies or the signaling query.
- Never authenticate sessions with Identity tokens or keep a legacy fallback.
- Store the persistent credential only in existing platform-secure storage.
- Never log credentials, access tokens, pairing/session codes, or QR payloads.
- Do not add packages. Preserve `qrPayload` in contracts, but defer QR UI.
- Do not conflate pairing challenge state with streaming session state.
- Prefer focused tests; run complete client suites only at final verification.

---

## Task 1: Windows device credential and token provider

**Files:**
- Modify storage contracts/implementations under `src/SonicRelay.Windows.Core/Storage/`
- Create Device Identity contracts/client under `src/SonicRelay.Windows.ApiClient/DeviceIdentity/`
- Modify `src/SonicRelay.Windows.ApiClient/ApiHttpClient.cs`
- Add focused tests in the corresponding Core and ApiClient test projects

1. Write failing tests for atomic credential save/load/delete, bootstrap when
   absent, token exchange when absent/near expiry, single-flight concurrency,
   invalid-credential clearing, and transient-failure retention.
2. Add explicit `DeviceCredential` and `DeviceAccessToken` models; do not reuse
   Identity `TokenSet` semantics.
3. Implement `/api/devices/bootstrap` and `/api/devices/token` calls for
   `windows_publisher/windows` and a serialized token provider.
4. Adapt bearer-header resolution to the provider and rerun focused tests.
5. Commit the green increment in the Windows repository.

## Task 2: Windows Phase 2 session/signaling/TURN contracts

**Files:**
- Modify `SonicRelay.Windows.ApiClient/Sessions/*`
- Modify `SonicRelay.Windows.Signaling/SignalingClient.cs` and interface
- Modify WebRTC backend ICE provider wiring
- Modify directly associated tests

1. Write failing tests that session creation serializes only `maxViewers`, DTOs
   do not require `ownerUserId`, and signaling builds `?sessionId=` only.
2. Remove supplied device identity from the session and signaling interfaces.
3. Resolve a fresh DeviceBearer token on every signaling reconnect and TURN
   request.
4. Rerun the focused ApiClient and Signaling tests and commit.

## Task 3: Windows first-run and pairing management UX

**Files:**
- Modify desktop composition and account/login navigation
- Add pairing API contracts/repository and focused view-model/control files
- Modify session code labels only where needed
- Add focused desktop tests

1. Write failing composition/navigation tests proving startup bootstraps a
   device and production flow cannot reach login/register.
2. Replace the active login state with device setup/readiness state.
3. Add pairing challenge creation/display (code and expiry only), refresh, list,
   and confirmed revoke. Keep the API `qrPayload` in the response contract.
4. Prove pairing code state and session join code state cannot overwrite each
   other. Run focused desktop tests and commit.

## Task 4: Flutter device credential and token provider

**Files:**
- Replace active auth credential models/storage under `lib/core/storage/` and
  `lib/features/auth/` with Device Identity equivalents
- Modify `lib/core/http/auth_interceptor.dart`
- Modify providers under `lib/app/di/app_providers.dart`
- Add focused storage/repository/interceptor tests

1. Write failing tests for atomic secure credential persistence, bootstrap,
   token exchange/expiry margin, single-flight behavior, invalid-credential
   clearing, transient-failure retention, and one safe 401 renewal.
2. Implement `flutter_viewer` bootstrap with runtime `android|ios` platform.
3. Replace refresh-token behavior with `/api/devices/token`; never call Identity
   login/refresh as fallback.
4. Rerun focused tests and commit in the Flutter repository.

## Task 5: Flutter Phase 2 session/signaling/TURN contracts

**Files:**
- Modify session request/repository files
- Modify `features/signaling/data/signaling_client.dart`
- Modify ICE-server/stats token wiring if necessary
- Modify directly associated tests

1. Write failing tests that join sends only normalized `code`, signaling uses
   only `sessionId`, and reconnect resolves a current DeviceBearer token.
2. Remove old device registration and supplied device ID from these paths.
3. Keep device ID only as non-authoritative local signaling message metadata
   where required by the existing envelope.
4. Run focused session/signaling/ICE tests and commit.

## Task 6: Flutter pairing and login-free navigation

**Files:**
- Add pairing data/domain/presentation files
- Modify router and app composition
- Remove login page from production route graph
- Add focused router, repository, view-model, and widget tests

1. Write failing tests for challenge completion, indistinguishable invalid-code
   errors, list/revoke, and navigation without account login.
2. Add first-run pairing entry for `challengeId + pairing code` and retain no
   redeemed code.
3. Keep the existing session join page as a separate post-pairing flow.
4. Add paired-device list and confirmed revoke; rerun focused tests and commit.

## Task 7: Cross-repository acceptance and documentation

1. Run focused backend integration filters for bootstrap/token, pairing,
   sessions, signaling, TURN, and revocation.
2. Run complete Windows and Flutter suites only after all focused tests pass.
3. Against a clean Phase 2 backend, verify bootstrap → pairing → restart/token
   exchange → session create/join → TURN → signaling → pairing/device
   revocation as specified in the design.
4. Update both client READMEs with actual first-run, secure-storage, reset,
   pairing, and session-code behavior; correct backend docs only if stale.
5. Run formatting/diff checks, commit each repository, and prepare PR metadata
   without issue-closing keywords.
