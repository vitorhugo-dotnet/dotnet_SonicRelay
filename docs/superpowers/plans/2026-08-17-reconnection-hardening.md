# Reconnection Hardening Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make recovery after a real network loss deterministic across API, desktop publisher and Flutter viewer — no competing reconnect loops, no stale attempts overwriting newer state, no attempt budget burned while offline, and no `Live` badge without media.

**Architecture:** Three coordinated changes sharing one vocabulary. (1) A *recovery journal* — the structured event/stage names from issue #39 — implemented once per client repo so every recovery step is greppable. (2) A *network gate* on both reconnect loops: while the OS reports no usable transport the loop parks in an explicit `WaitingForNetwork` state, consuming no attempt budget, and resumes on a debounced restore with the backoff reset. (3) *Media-confirmed liveness* on the viewer: ICE `connected` alone no longer produces `connected`; inbound RTP must actually advance. On the API, participant rejoin becomes duplicate-proof at the database level.

**Tech Stack:** .NET 9 (minimal APIs, EF Core, xUnit), Avalonia/SIPSorcery desktop publisher (xUnit), Flutter/Dart viewer (`flutter_test`, `connectivity_plus`).

**Spec:** https://github.com/vitorhugo-dotnet/dotnet_SonicRelay/issues/39

## Global Constraints

- Every repo develops on branch `claude/superpower-skills-pr-ygfr79`.
- Journal payloads never carry tokens, full SDP, full ICE candidates or secrets. Both repos already own redactors (`DiagnosticRedactor`) — route journal properties through the existing `DiagnosticLog`/`sonicLog` paths so redaction is not re-implemented.
- Recovery event names are fixed and identical across repos: `network_lost`, `network_restored`, `recovery_started`, `recovery_cancelled`, `stale_attempt_ignored`, `signaling_reconnect_started`, `signaling_reconnect_succeeded`, `session_rejoin_started`, `session_rejoin_succeeded`, `ice_restart_started`, `ice_restart_succeeded`, `peer_rebuild_started`, `peer_rebuild_succeeded`, `media_resumed`, `recovery_failed`.
- Recovery event properties are fixed: `generation`, `attempt`, `stage`, `previousState`, `newState`, `reason`, `elapsedMs`, `result`.
- Network stabilization debounce default: 750 ms, injectable in tests.
- No new third-party dependencies in any repo.
- TDD: a failing test precedes every behavior change.

---

## File Structure

**dotnet_SonicRelay (API)**
- Modify `src/SonicRelay.Domain/Sessions/SessionParticipant.cs` — no shape change, documentation of the uniqueness invariant.
- Modify `src/SonicRelay.Infrastructure/Persistence/AppDbContext.cs` — unique index on `(SessionId, DeviceId, Role)`.
- Create `src/SonicRelay.Infrastructure/Persistence/Migrations/*_AddUniqueSessionParticipant.cs` — dedupe pass + unique index.
- Modify `services/SonicRelay.Api/Endpoints/SessionEndpoints.cs` — race-safe idempotent `AdmitViewerAsync`.
- Modify `services/SonicRelay.Api/Endpoints/SignalingWebSocketEndpoint.cs` — deterministic participant lookup.
- Test `tests/SonicRelay.Api.IntegrationTests/SessionRejoinIdempotencyTests.cs`.

**desktop_dotnet_SonicRelay (publisher)**
- Create `src/SonicRelay.Windows.Core/Diagnostics/RecoveryJournal.cs` — `RecoveryEvents`, `IRecoveryJournal`, `DiagnosticRecoveryJournal`, `NullRecoveryJournal`.
- Create `src/SonicRelay.Windows.Signaling/INetworkAvailability.cs` — gate abstraction + `SystemNetworkAvailability` + `AlwaysAvailableNetwork`.
- Modify `src/SonicRelay.Windows.Signaling/SignalingConnectionState.cs` — add `WaitingForNetwork`.
- Modify `src/SonicRelay.Windows.Signaling/SignalingClient.cs` — network gate + generation + journal.
- Test `tests/SonicRelay.Windows.Core.Tests/RecoveryJournalTests.cs`, `tests/SonicRelay.Windows.Signaling.Tests/SignalingClientNetworkGateTests.cs`.

**flutter_mobile-web_SonicRelay (viewer)**
- Create `lib/core/diagnostics/recovery_journal.dart`.
- Modify `lib/core/network/network_monitor.dart` — add current-value probe.
- Modify `lib/core/websocket/websocket_client.dart` — `waitingForNetwork` + budget freeze.
- Modify `lib/features/signaling/data/signaling_client.dart` — debounced restore, journal wiring.
- Modify `lib/features/listener/data/webrtc_receiver_service.dart` — media-confirmed `connected`.
- Modify `lib/features/listener/domain/listener_connection_state.dart` — add `waitingForMedia`.
- Tests under `test/core/diagnostics/`, `test/core/websocket/`, `test/features/signaling/data/`, `test/features/listener/data/`.

---

### Task 1: API — duplicate-proof participant rejoin

**Files:**
- Modify: `src/SonicRelay.Infrastructure/Persistence/AppDbContext.cs`
- Create: `src/SonicRelay.Infrastructure/Persistence/Migrations/<stamp>_AddUniqueSessionParticipant.cs`
- Modify: `services/SonicRelay.Api/Endpoints/SessionEndpoints.cs` (`AdmitViewerAsync`)
- Modify: `services/SonicRelay.Api/Endpoints/SignalingWebSocketEndpoint.cs` (participant lookup)
- Test: `tests/SonicRelay.Api.IntegrationTests/SessionRejoinIdempotencyTests.cs`

**Interfaces:**
- Consumes: existing `AppDbContext`, `SessionParticipant`, `DeviceIdentityTestHelper`.
- Produces: invariant "at most one `SessionParticipant` per `(SessionId, DeviceId, Role)`", relied on by every later rejoin path.

- [ ] **Step 1:** Write a failing integration test that joins the same session twice from one device concurrently and asserts exactly one viewer participant row exists.
- [ ] **Step 2:** Run it; expect failure (two rows, or `SingleOrDefaultAsync` throwing).
- [ ] **Step 3:** Add the unique index in `AppDbContext.OnModelCreating`, generate the migration with a dedupe pass that keeps the earliest `JoinedAt` row.
- [ ] **Step 4:** Make `AdmitViewerAsync` retry its lookup once on `DbUpdateException` so the loser of the insert race reuses the winner's row.
- [ ] **Step 5:** Replace the WebSocket endpoint's `SingleOrDefaultAsync` participant lookup with a deterministic `OrderBy(JoinedAt).FirstOrDefaultAsync`.
- [ ] **Step 6:** Run the API test suite; expect green.
- [ ] **Step 7:** Commit.

### Task 2: Desktop — recovery journal

**Files:**
- Create: `src/SonicRelay.Windows.Core/Diagnostics/RecoveryJournal.cs`
- Test: `tests/SonicRelay.Windows.Core.Tests/RecoveryJournalTests.cs`

**Interfaces:**
- Produces: `RecoveryEvents` (const event names), `IRecoveryJournal.Record(string @event, int generation, int attempt, IReadOnlyDictionary<string,string>? properties)`, `DiagnosticRecoveryJournal(DiagnosticLog)`, `NullRecoveryJournal.Instance`.

- [ ] **Step 1:** Failing test: recording `network_lost` with generation 3 writes a `Recovery` category entry carrying `event`, `generation`, `attempt`.
- [ ] **Step 2:** Run; expect compile failure.
- [ ] **Step 3:** Implement the journal over the existing `DiagnosticLog`.
- [ ] **Step 4:** Run; expect green. **Step 5:** Commit.

### Task 3: Desktop — network gate on signaling reconnect

**Files:**
- Create: `src/SonicRelay.Windows.Signaling/INetworkAvailability.cs`
- Modify: `src/SonicRelay.Windows.Signaling/SignalingConnectionState.cs`, `SignalingClient.cs`
- Test: `tests/SonicRelay.Windows.Signaling.Tests/SignalingClientNetworkGateTests.cs`

**Interfaces:**
- Consumes: `IRecoveryJournal` from Task 2.
- Produces: `INetworkAvailability { bool IsAvailable { get; } event Action<bool>? AvailabilityChanged; }`, `SignalingConnectionState.WaitingForNetwork`.

- [ ] **Step 1:** Failing test: with the gate reporting offline, a dropped socket parks in `WaitingForNetwork` and consumes zero reconnect attempts even after the policy's `MaxAttempts` would have elapsed.
- [ ] **Step 2:** Failing test: flipping the gate back online resumes with attempt 0 (base delay), not the capped delay.
- [ ] **Step 3:** Run both; expect failure.
- [ ] **Step 4:** Implement: gate check before each attempt, `WaitingForNetwork` state, await availability, debounce, reset attempt counter, journal `network_lost`/`network_restored`/`signaling_reconnect_*`, monotonic generation per recovery cycle.
- [ ] **Step 5:** Run the desktop suite; expect green. **Step 6:** Commit.

### Task 4: Flutter — recovery journal + network gate on the socket

**Files:**
- Create: `lib/core/diagnostics/recovery_journal.dart`
- Modify: `lib/core/network/network_monitor.dart`, `lib/core/websocket/websocket_client.dart`, `lib/features/signaling/data/signaling_client.dart`
- Test: `test/core/diagnostics/recovery_journal_test.dart`, `test/core/websocket/websocket_client_test.dart`, `test/features/signaling/data/signaling_client_test.dart`

**Interfaces:**
- Produces: `RecoveryEvents`, `RecoveryJournal.record(...)`, `WebSocketConnectionState.waitingForNetwork`, `NetworkMonitor.isOnline()`.

- [ ] **Step 1:** Failing test: while the gate is offline, successive disconnects do not advance the backoff attempt counter and the client reports `waitingForNetwork`.
- [ ] **Step 2:** Failing test: a restore is debounced by the stabilization window before the retry fires.
- [ ] **Step 3:** Run; expect failure. **Step 4:** Implement. **Step 5:** Run `flutter test`; expect green. **Step 6:** Commit.

### Task 5: Flutter — media-confirmed `Live`

**Files:**
- Modify: `lib/features/listener/domain/listener_connection_state.dart`, `lib/features/listener/data/webrtc_receiver_service.dart`
- Test: `test/features/listener/data/webrtc_receiver_service_test.dart`

**Interfaces:**
- Consumes: `RecoveryJournal` from Task 4.
- Produces: `ListenerConnectionState.waitingForMedia`.

- [ ] **Step 1:** Failing test: ICE reaching `connected` with no inbound packets leaves the state at `waitingForMedia`, not `connected`.
- [ ] **Step 2:** Failing test: the first stats poll showing `packetsReceived` advancing promotes the state to `connected` and journals `media_resumed`.
- [ ] **Step 3:** Failing test: metrics are cleared when the media path drops, so a stale RTT cannot survive into a reconnect.
- [ ] **Step 4:** Run; expect failure. **Step 5:** Implement. **Step 6:** Run `flutter test`; expect green. **Step 7:** Commit.

### Task 6: Documentation + PR

- [ ] **Step 1:** Record the shared recovery-event vocabulary in `docs/observability.md`.
- [ ] **Step 2:** Run every suite once more. **Step 3:** Push all three branches. **Step 4:** Open the PRs, with `Closes #39` on the API one.

---

## Out of scope (explicitly deferred, per issue "Fora de escopo" and staging)

The single-orchestrator state machine that subsumes the WebSocket and WebRTC
loops into one object, the 20-cycle chaos harness, and the interface-matrix
manual test sweep are follow-ups: they depend on the generation/gate/journal
primitives this plan lands first, and folding them in here would produce a
change too large to review against the acceptance criteria individually.
