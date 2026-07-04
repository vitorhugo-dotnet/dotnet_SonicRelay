# Fake Signaling Client Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a runnable fake publisher/viewer client that validates the real SonicRelay signaling flow end to end.

**Architecture:** A dependency-free console project owns the HTTP/WebSocket orchestration and small typed protocol helpers. A separate xUnit project tests deterministic argument and envelope behavior; the live API remains an explicit manual dependency.

**Tech Stack:** .NET 10, `HttpClient`, `ClientWebSocket`, `System.Text.Json`, xUnit.

---

### Task 1: Define testable protocol behavior

**Files:**
- Create: `tests/SonicRelay.SignalingClient.Tests/SonicRelay.SignalingClient.Tests.csproj`
- Create: `tests/SonicRelay.SignalingClient.Tests/ClientOptionsTests.cs`
- Create: `tests/SonicRelay.SignalingClient.Tests/SignalingMessageTests.cs`
- Create: `tools/SonicRelay.SignalingClient/ClientOptions.cs`
- Create: `tools/SonicRelay.SignalingClient/SignalingMessage.cs`

- [ ] Write tests asserting that no arguments select `http://localhost:8080`, `--base-url` accepts an absolute HTTP(S) URL, invalid values fail, outgoing messages carry `type`, `messageId`, `to`, and fake JSON payload, and validation rejects mismatched type/session/from/to/message ID.
- [ ] Run `dotnet test tests/SonicRelay.SignalingClient.Tests/SonicRelay.SignalingClient.Tests.csproj` and verify RED because the production project/types do not exist.
- [ ] Add the console and test project files plus the minimal `ClientOptions.Parse` and `SignalingMessage.Create`/`ValidateRouted` implementations needed by those assertions.
- [ ] Re-run the focused test project and verify GREEN.

### Task 2: Implement the live flow

**Files:**
- Create: `tools/SonicRelay.SignalingClient/SonicRelay.SignalingClient.csproj`
- Create: `tools/SonicRelay.SignalingClient/SignalingClient.cs`
- Create: `tools/SonicRelay.SignalingClient/Program.cs`
- Modify: `SonicRelay.sln`

- [ ] Implement HTTP helpers that require success for `/auth/register`, `/auth/login`, `/api/devices/`, `/api/sessions/`, and `/api/sessions/join`, extracting `accessToken`, device `id`, session `id`, and `code` from their official JSON responses.
- [ ] Implement authenticated WebSocket connection to `/ws/signaling`, read each `session.joined`, exchange the five required messages with payload markers `fake-test-*`, validate each routed envelope, and close both sockets normally in `finally`.
- [ ] Add both projects to the solution and run the focused test project.

### Task 3: Document and verify

**Files:**
- Create: `tools/SonicRelay.SignalingClient/README.md`
- Modify: `README.md`

- [ ] Document Compose dependencies, API startup, `dotnet run --project tools/SonicRelay.SignalingClient -- --base-url http://localhost:8080`, representative stage output, and the no-audio/no-WebRTC limitation.
- [ ] Link the tool from the root README.
- [ ] Run `dotnet test tests/SonicRelay.SignalingClient.Tests/SonicRelay.SignalingClient.Tests.csproj` and `dotnet build tools/SonicRelay.SignalingClient/SonicRelay.SignalingClient.csproj --no-restore`; both must exit zero.
- [ ] Review the diff for issue #13 scope only, commit on `main`, push `origin/main`, and close issue #13 with the commit summary.
