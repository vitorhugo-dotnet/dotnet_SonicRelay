# ADR 0005: Symmetric device credentials with a parallel DeviceBearer scheme

- Status: Accepted
- Date: 2026-07-21

## Context

Issue #26 (Phase 1) introduces device-identity authentication so Windows and
Flutter clients can authenticate without a human account. Devices need a
persistent, revocable credential and short-lived access tokens with explicit
scopes, without breaking the existing Identity login flow.

## Decision

Bootstrap issues a server-generated, high-entropy secret once; only its HMAC
is stored. `/api/devices/token` exchanges that secret for a short-lived JWT on
a new `DeviceBearer` authentication scheme, registered alongside the existing
`Identity.Bearer` scheme (ADR 0002) rather than replacing it. A custom
authorization requirement re-checks device status and credential version
against the database on every scoped request, so rotation and revocation take
effect immediately despite tokens being self-contained. The whole feature is
gated by `DeviceIdentity:Enabled`.

Asymmetric (public-key) proof-of-possession was considered and rejected for
this phase: it requires client-side key generation and challenge-response
signing on both Windows and Flutter before any server work is testable,
which the issue explicitly allows deferring for the MVP. `DeviceIdentity`
reserves a `PublicKey` column for this later.

## Consequences

Two independent bearer schemes coexist until Phase 4 removes Identity. A
compromised device secret is equivalent to a compromised device until
rotated or revoked; short token lifetimes (default 5 minutes) and per-request
status checks bound the exposure window. Migrating `StreamSession`/signaling
to device ownership (Phase 2) and client integration (Phase 3) are tracked
separately in issue #26.
