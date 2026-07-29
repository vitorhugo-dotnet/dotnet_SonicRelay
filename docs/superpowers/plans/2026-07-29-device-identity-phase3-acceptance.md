# Device Identity Phase 3 Cross-Repository Acceptance Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Prove backend, Windows publisher, and Flutter viewer implement one secure pairing/streaming flow without Identity fallback.

**Architecture:** Verify repository-local tests first, then run a clean backend and exercise public HTTP/WebSocket contracts with both clients. Capture statuses and masked identifiers, never secrets or codes.

**Tech Stack:** .NET 10, Flutter/Dart, SonicRelay development infrastructure, WinUI publisher, Android/iOS viewer.

## Global Constraints

- Start only after all backend, Windows, and Flutter focused tasks pass.
- Use clean development persistence so legacy rows cannot hide bootstrap defects.
- Never record credentials, tokens, QR payloads, pairing/session codes, SDP, or complete ICE candidates.
- Do not use issue-closing keywords because Phase 4 remains open.

---

### Task 1: Repository-local final verification

**Files:**
- No production files.
- Modify READMEs only if observed commands or behavior differ.

**Interfaces:**
- Consumes: completed Phase 3 branches in all three repositories.
- Produces: fresh build/test evidence for every repository.

- [ ] **Step 1: Verify backend**

```powershell
dotnet build SonicRelay.sln --no-restore --verbosity minimal
dotnet test tests/SonicRelay.Api.IntegrationTests/SonicRelay.Api.IntegrationTests.csproj --filter "FullyQualifiedName~DeviceBootstrapAndTokenTests|FullyQualifiedName~DeviceCredentialLifecycleTests|FullyQualifiedName~PairingChallengeTests|FullyQualifiedName~PairingManagementTests|FullyQualifiedName~SessionEndpointsTests|FullyQualifiedName~SignalingWebSocketTests|FullyQualifiedName~WebRtcEndpointsTests" --no-restore --verbosity minimal
git diff --check
```

- [ ] **Step 2: Verify Windows**

```powershell
dotnet build SonicRelay.Windows.slnx --no-restore --verbosity minimal
dotnet test tests/SonicRelay.Windows.Core.Tests/SonicRelay.Windows.Core.Tests.csproj --no-restore --verbosity minimal
dotnet test tests/SonicRelay.Windows.ApiClient.Tests/SonicRelay.Windows.ApiClient.Tests.csproj --no-restore --verbosity minimal
dotnet test tests/SonicRelay.Windows.Signaling.Tests/SonicRelay.Windows.Signaling.Tests.csproj --no-restore --verbosity minimal
dotnet test tests/SonicRelay.Windows.Presentation.Tests/SonicRelay.Windows.Presentation.Tests.csproj --no-restore --verbosity minimal
git diff --check
```

- [ ] **Step 3: Verify Flutter**

```powershell
flutter test
flutter analyze
git diff --check
```

Expected: every command exits zero. Fix repository-local failures before
integrated acceptance.

### Task 2: Execute the public-contract flow

**Files:**
- No production files.
- Create a redacted report under `docs/verification/` only if that repository already tracks such reports.

**Interfaces:**
- Consumes: public Device Identity, pairing, session, TURN, and signaling endpoints.
- Produces: end-to-end Phase 3 evidence.

- [ ] **Step 1: Start clean development services**

Use the documented startup command. Clear only the explicit development
database/cache selected for this run; never delete broad directories or
unrelated Docker volumes.

- [ ] **Step 2: Verify bootstrap and QR/manual pairing**

1. Launch Windows and confirm readiness without login.
2. Create a challenge and display code, expiry, and QR.
3. Launch Flutter and confirm pairing without login.
4. Scan QR and confirm both list the same active pairing.
5. Repeat with a new challenge using manual challenge ID plus code.

- [ ] **Step 3: Verify persistence and streaming**

1. Restart both clients and confirm no new device identity is created.
2. Create a Windows stream and retain only a masked session identifier.
3. Join from Flutter with the separate session code.
4. Confirm TURN resolution, DeviceBearer WebSocket connection with only
   `sessionId`, offer/answer/ICE completion, and audible playback.

- [ ] **Step 4: Verify isolation and revocation**

1. Confirm an unpaired second viewer gets the invalid-code response.
2. Revoke the first pairing during its active stream; stream stays connected.
3. End/recreate the stream; revoked viewer cannot join.
4. Re-pair, join, revoke the viewer device, then confirm its next authenticated
   HTTP request or WebSocket reconnect returns to setup.

- [ ] **Step 5: Record redacted results and prepare PRs**

Report exit codes, test counts, platform versions, and pass/fail for each step.
Update each PR with repository-local results and sibling PR links, without
`Closes #26`.
