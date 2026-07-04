# WebRTC Client Documentation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Deliver an exact client integration protocol and a Brazilian Portuguese beginner guide for SonicRelay issues #14 and #15.

**Architecture:** Keep the normative signaling contract in `docs/protocol.md` and introductory teaching material in `docs/beginner-guide.md`. Broadcast a new participant's `session.joined` envelope to existing session peers so separate clients can discover routing IDs, then document that deterministic handshake.

**Tech Stack:** Markdown, Mermaid, PowerShell content assertions, Git.

---

### Task 1: Establish failing documentation checks

**Files:**
- Test: `README.md`
- Test: `docs/protocol.md`
- Test: `docs/beginner-guide.md`

- [ ] **Step 1: Run PowerShell assertions for the two README links, all fifteen beginner-guide headings, Publisher/Viewer flows, signaling envelope, offer/answer, ICE, validation boundary, opaque payload boundary, and security notes.**
- [ ] **Step 2: Confirm the command fails because `docs/beginner-guide.md` and the required expanded protocol sections do not exist.**

### Task 2: Expand the canonical protocol

**Files:**
- Modify: `docs/protocol.md`

- [ ] **Step 1: Correct the stale device-route description to match the implemented owner-scoped CRUD and revoke behavior.**
- [ ] **Step 2: Add the control-plane/media-plane mental model and explicit backend responsibilities.**
- [ ] **Step 3: Add ordered Publisher and Viewer integration flows.**
- [ ] **Step 4: Add canonical client/server envelopes and small JSON examples for readiness, offer, answer, and ICE.**
- [ ] **Step 5: Document server validation, opaque fields, error handling, connection limits, privacy, and security requirements.**

### Task 3: Implement participant discovery with TDD

**Files:**
- Modify: `tests/SonicRelay.Api.IntegrationTests/SignalingWebSocketTests.cs`
- Modify: `services/SonicRelay.Api/Endpoints/SignalingWebSocketEndpoint.cs`

- [ ] **Step 1: Add an integration test that connects a Publisher, then a Viewer in the same session, and expects the Publisher to receive `session.joined` containing the Viewer participant ID and role.**
- [ ] **Step 2: Run only that test and confirm it fails because no join announcement reaches the Publisher.**
- [ ] **Step 3: After sending the new socket its own join envelope, broadcast `session.joined` with `{ participantId, role }` to other live session participants.**
- [ ] **Step 4: Re-run only that test and confirm it passes.**

### Task 4: Create the beginner guide

**Files:**
- Create: `docs/beginner-guide.md`

- [ ] **Step 1: Add all fifteen required sections in Portuguese do Brasil.**
- [ ] **Step 2: Add practical analogies for WebSocket, WebRTC, signaling, SDP, ICE, STUN, TURN/coturn, and Opus.**
- [ ] **Step 3: Add simple Mermaid architecture and complete-flow diagrams.**
- [ ] **Step 4: Add the fair Spring Boot to ASP.NET Core mapping and rationale.**
- [ ] **Step 5: Explicitly preserve the control-plane-only architecture and direct/TURN media path.**

### Task 5: Update navigation and verify

**Files:**
- Modify: `README.md`
- Test: `README.md`
- Test: `docs/protocol.md`
- Test: `docs/beginner-guide.md`

- [ ] **Step 1: Add the beginner guide and explicit client-integration protocol links to the README documentation index.**
- [ ] **Step 2: Re-run the same PowerShell assertions and confirm every requirement passes.**
- [ ] **Step 3: Check balanced Mermaid fences and run `git diff --check`.**
- [ ] **Step 4: Review the final diff for scope and technical consistency.**
- [ ] **Step 5: Commit only the intended documentation files on `main`, push `origin/main`, and close issues #14 and #15 with the commit reference.**
