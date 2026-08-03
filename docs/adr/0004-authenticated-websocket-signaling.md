# ADR 0004: Use authenticated WebSocket signaling

- Status: Accepted
- Date: 2026-07-04

## Context

WebRTC peers need bidirectional offer, answer and ICE exchange with session isolation and immediate disconnect/terminal-session notification.

## Decision

Use an authenticated WebSocket endpoint. Validate the caller's device identity and participant membership before upgrade, derive sender identity server-side, and route frames only to participants registered in the same session.

## Consequences

The protocol supports low-overhead bidirectional signaling and does not log SDP/ICE payloads. The current registry is in memory, so live connections are bound to one API process and horizontal scaling requires sticky sessions or a distributed registry/backplane.

**Update (issue #26 Phase 4):** the connecting device's identity is derived entirely from its `DeviceBearer` token (see [ADR 0006](0006-remove-identity.md)); there is no separate human-user ownership check.
