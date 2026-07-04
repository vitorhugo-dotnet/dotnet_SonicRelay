# WebRTC Client Documentation Design

## Goal

Document the SonicRelay signaling contract precisely enough to implement the Windows Publisher and Flutter Viewer, while also providing a separate beginner-friendly explanation of the architecture and protocols.

## Scope

- Expand `docs/protocol.md` with the Publisher and Viewer workflows, canonical signaling envelope, offer/answer and ICE exchange, backend validation boundary, opaque payload boundary, security guidance, and small JSON examples.
- Create `docs/beginner-guide.md` in Brazilian Portuguese with the fifteen sections requested by issue #15, practical analogies, Mermaid diagrams, the Spring Boot to ASP.NET Core mapping, study order, glossary, and common mistakes.
- Update the README documentation index to expose both documents.
- Correct stale statements in the existing protocol documentation when they contradict the current device endpoint implementation.
- Close the participant-discovery gap by broadcasting `session.joined` for a newly connected participant to existing live participants in the session.

No media capture, Flutter playback, SFU, media server, dependency, or infrastructure change is in scope.

## Design

`docs/protocol.md` remains the canonical technical reference. It distinguishes the control plane from the media plane, documents the exact authenticated WebSocket admission and envelope rules implemented by the API, and gives client authors an ordered checklist for Publisher and Viewer integration. SDP and ICE examples stay intentionally small and synthetic; the document explicitly prohibits logging their contents.

`docs/beginner-guide.md` is a conceptual companion rather than a second protocol specification. It explains each technology with practical analogies, uses simple architecture and sequence diagrams, and links readers back to the protocol for normative field and route details. This separation prevents introductory prose from weakening the exact contract.

The README links both audiences to the right entry point. Existing architecture, security, and ADR documents remain authoritative for their own concerns and are linked rather than duplicated at length.

On WebSocket admission, the new socket still receives its own `session.joined` envelope. Existing sockets in the same session additionally receive `session.joined` with the new participant ID and role. This gives the Publisher a deterministic trigger and target for `publisher.ready`; the Viewer then learns the Publisher ID from that message's authenticated `from` field.

## Validation

- Automated content checks assert every required heading, concept, README link, message type, and security boundary is present.
- A focused integration test proves that an existing Publisher receives the Viewer join announcement and its role/participant ID.
- Mermaid fences are checked for balanced opening and closing markers.
- `git diff --check` verifies whitespace integrity.
- No .NET test suite is required because no executable code changes.

## Acceptance criteria

- A Windows Publisher implementer can identify authentication, device/session setup, WebSocket admission, readiness, per-viewer offer, answer handling, trickle ICE, and media ownership without guessing.
- A Flutter Viewer implementer can identify join, WebSocket admission, readiness, offer handling, answer generation, trickle ICE, and playback ownership without guessing.
- Remote participant discovery requires no out-of-band ID sharing.
- The backend is consistently described as a signaling/control plane that neither inspects nor transmits audio.
- A reader without prior realtime experience can understand WebSocket, WebRTC, signaling, SDP, ICE, STUN, TURN/coturn, and Opus, including how they fit together.
- README links both the protocol and beginner guide.
