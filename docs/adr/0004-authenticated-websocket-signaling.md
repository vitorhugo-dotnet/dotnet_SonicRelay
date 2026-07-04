# ADR 0004: Use authenticated WebSocket signaling

- Status: Accepted
- Date: 2026-07-04

## Context

WebRTC peers need bidirectional offer, answer and ICE exchange with session isolation and immediate disconnect/terminal-session notification.

## Decision

Use an authenticated WebSocket endpoint. Validate user ownership of the device and participant membership before upgrade, derive sender identity server-side, and route frames only to participants registered in the same session.

## Consequences

The protocol supports low-overhead bidirectional signaling and does not log SDP/ICE payloads. The current registry is in memory, so live connections are bound to one API process and horizontal scaling requires sticky sessions or a distributed registry/backplane.
