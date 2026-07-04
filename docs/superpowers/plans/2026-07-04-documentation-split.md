# SonicRelay Documentation Split Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the oversized README with a concise entry point and focused, implementation-accurate architecture, protocol, security, deployment, and ADR documentation.

**Architecture:** Treat mapped API routes, endpoint handlers, infrastructure registrations, tests, deployment manifests, and CI workflow as authoritative. Move existing Mermaid diagrams to the focused architecture document and keep README content limited to orientation and common commands.

**Tech Stack:** Markdown, Mermaid, ASP.NET Core Minimal API, PostgreSQL, Redis, WebSockets, Docker Compose, GitHub Actions

---

### Task 1: Inventory implemented behavior

**Files:**
- Inspect: `services/SonicRelay.Api/Program.cs`
- Inspect: `services/SonicRelay.Api/Endpoints/*.cs`
- Inspect: `src/SonicRelay.Infrastructure/**/*.cs`
- Inspect: `tests/SonicRelay.Api.IntegrationTests/*.cs`
- Inspect: `.github/workflows/vps-ci-cd.yml`
- Inspect: `deploy/*`
- Inspect: `infra/*`

- [ ] **Step 1: Extract route mappings and authorization requirements**

Read each endpoint mapping and record only routes registered by the application.

- [ ] **Step 2: Extract validation, persistence, status-code, and signaling behavior**

Read endpoint handlers and their focused integration tests to distinguish implemented behavior from roadmap items.

- [ ] **Step 3: Extract operational behavior**

Read the workflow, deployment scripts, and compose/environment examples to document the actual deployment path and required settings.

### Task 2: Create focused architecture and decision documentation

**Files:**
- Create: `docs/architecture.md`
- Create: `docs/adr/0001-control-plane-only.md`
- Create: `docs/adr/0002-identity-bearer-tokens.md`
- Create: `docs/adr/0003-postgresql-and-redis-storage.md`
- Create: `docs/adr/0004-authenticated-websocket-signaling.md`

- [ ] **Step 1: Move useful Mermaid diagrams into `docs/architecture.md`**

Retain the system topology, primary sequence, domain model, isolation, and peer-connection diagrams while correcting labels that conflict with current code.

- [ ] **Step 2: Document current component boundaries and status**

State that the backend owns control-plane state and signaling while client repositories own capture, WebRTC media, and playback.

- [ ] **Step 3: Write four implementation-backed ADRs**

Use `Accepted` status and sections for context, decision, and consequences. Do not record speculative choices.

### Task 3: Document implemented protocols and security

**Files:**
- Create: `docs/protocol.md`
- Create: `docs/security.md`

- [ ] **Step 1: Write the HTTP contract tables**

List mapped health, Identity, custom auth, device, and session endpoints with exact authorization and implemented behavior.

- [ ] **Step 2: Write the WebSocket contract**

Document query parameters, authentication, connection validation, server events, routed message types, payload routing, errors, size limits, and terminal-session behavior.

- [ ] **Step 3: Separate implemented controls from remaining production work**

Describe ownership checks, HMAC session codes, one-time redemption, rate-limit policies, sensitive-payload logging behavior, secret handling, and confirmed gaps without presenting recommendations as completed work.

### Task 4: Update deployment guide and README entry point

**Files:**
- Modify: `docs/deployment-vps-ssh.md`
- Modify: `README.md`

- [ ] **Step 1: Reconcile deployment documentation with repository automation**

Document the actual workflow triggers/jobs, GHCR image flow, SSH prerequisites, compose files, environment variables, health verification, and rollback supported by current scripts.

- [ ] **Step 2: Replace README with the concise entry point**

Keep the suite table, quick start, current status, focused-doc links, and CI/CD summary. Remove duplicated detailed material now owned by focused docs.

### Task 5: Verify documentation and E2E behavior

**Files:**
- Verify: `README.md`
- Verify: `docs/**/*.md`
- Test: `tests/SonicRelay.Api.IntegrationTests/SonicRelay.Api.IntegrationTests.csproj`

- [ ] **Step 1: Check documentation structure and links**

Run targeted searches for all required headings/files, stale stub language, and referenced relative paths. Run `git diff --check`.

- [ ] **Step 2: Run the API integration/E2E project unattended**

Run: `dotnet test tests/SonicRelay.Api.IntegrationTests/SonicRelay.Api.IntegrationTests.csproj --no-restore --nologo`

Expected: command exits successfully with zero failed tests. If restore artifacts are unavailable, rerun the same project without `--no-restore`.

- [ ] **Step 3: Review the final diff**

Confirm only requested documentation, the approved spec, and this plan changed; preserve unrelated `.vs/` content untouched.
