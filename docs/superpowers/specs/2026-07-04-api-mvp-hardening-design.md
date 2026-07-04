# SonicRelay API MVP Hardening Design

## Scope

Harden the single-instance SonicRelay API with in-memory rate limiting, periodic database cleanup, terminal-session enforcement, safe structured logging, and integration coverage. Distributed rate-limit state, cleanup leases, and distributed socket coordination are outside this MVP.

## Rate Limiting

Use ASP.NET Core rate limiting with fixed windows and endpoint-specific named policies:

- `POST /auth/login`: 5 requests per minute, partitioned by remote IP.
- `POST /auth/refresh`: 5 requests per minute, partitioned by remote IP.
- `POST /api/sessions`: 10 requests per minute, partitioned by authenticated user ID.
- `POST /api/sessions/join`: 10 requests per minute, partitioned by authenticated user ID.
- `POST /api/sessions/{sessionId}/rotate-code`: 5 requests per minute, partitioned by authenticated user ID.

All limits and window durations are configuration-driven. Rejections return HTTP 429 with `Retry-After` when the limiter supplies retry metadata. Rejection logs contain the policy, route, and partition-safe request metadata; they never contain credentials, bearer tokens, refresh tokens, join codes, or bodies.

The limiter is process-local. Limits reset on restart and do not coordinate across API replicas, which is acceptable for the current single-instance deployment.

## Session Cleanup

A single `BackgroundService` runs every 60 seconds by default. Each pass creates a dependency-injection scope and performs two bounded operations:

1. Find waiting or active sessions whose `CodeExpiresAt` is at or before the current UTC time, set their status to `expired`, persist the state change, and remove their join-code entries through `ISessionCodeStore`.
2. Delete participants already marked `disconnected` whose `LeftAt` is at or before the current UTC time minus the configured retention period, which defaults to 24 hours.

Session rows are retained for auditability. Connected participant rows are not deleted by age alone. The worker logs one structured completion event per non-empty pass with expired-session and removed-participant counts. Failures are logged and the next scheduled pass continues; cancellation exits cleanly.

The worker is disabled in integration-test hosting unless a test explicitly starts or invokes cleanup, preventing timer races and nondeterministic shared-fixture behavior.

## Terminal Session Enforcement

Join treats a missing, expired, ended, or explicitly expired session identically: HTTP 404 with the existing generic `Invalid or expired session code` response. When join discovers elapsed `CodeExpiresAt`, it marks a non-ended session expired and removes its code before returning.

WebSocket admission returns HTTP 410 for ended sessions, expired sessions, or elapsed `CodeExpiresAt`. An accepted signaling connection periodically checks persisted session state. On an ended, expired, deleted, or elapsed session it sends a payload-free `session.ended` control message and exits the receive loop, after which normal connection cleanup closes the socket and marks the participant disconnected. Routing checks terminal state before dispatch so a message observed after a terminal transition is rejected rather than forwarded.

This remains in-process coordination. A future multi-instance deployment will require shared connection/session notifications.

## Structured Logging

Add structured logs for:

- rate-limit rejection;
- session creation, join, code rotation, end, and expiry detection;
- cleanup pass counts and failures;
- signaling admission rejection, connection, disconnection, metadata-only routing, and terminal closure.

Allowed fields are stable identifiers, route or policy names, signaling event type, session status, and aggregate counts. Logging must not serialize HTTP bodies, authorization headers, passwords, tokens, join codes, signaling `payload`, SDP, or ICE candidates. Existing signaling routing logs continue to log only message type and participant/session identifiers.

## Testing

Integration tests use intentionally small configured permit limits and isolated partitions. Coverage includes:

- login and refresh return 429 after their configured limit;
- create, join, and rotate-code return 429 after their configured limit;
- unrelated users or IP partitions do not consume each other's quota;
- elapsed sessions cannot be joined and are transitioned to `expired`;
- ended and expired sessions reject WebSocket admission;
- an established WebSocket receives `session.ended` and closes after terminal transition;
- cleanup marks elapsed sessions expired, removes their code, deletes only stale disconnected participants, and retains session rows and recent/connected participants;
- captured signaling logs do not contain a sentinel SDP or ICE value.

Development runs only filtered integration tests relevant to each change. Final verification runs the repository's E2E workflow non-interactively and reports the exact command and result.

## Configuration Defaults

- `RateLimits:Login:PermitLimit = 5`
- `RateLimits:Login:WindowSeconds = 60`
- `RateLimits:Refresh:PermitLimit = 5`
- `RateLimits:Refresh:WindowSeconds = 60`
- `RateLimits:CreateSession:PermitLimit = 10`
- `RateLimits:CreateSession:WindowSeconds = 60`
- `RateLimits:JoinSession:PermitLimit = 10`
- `RateLimits:JoinSession:WindowSeconds = 60`
- `RateLimits:RotateCode:PermitLimit = 5`
- `RateLimits:RotateCode:WindowSeconds = 60`
- `Sessions:CleanupIntervalSeconds = 60`
- `Sessions:DisconnectedParticipantRetentionHours = 24`

## Non-Goals

- Distributed counters or Redis-backed rate limiting.
- Distributed cleanup locks, queues, or schedulers.
- Deleting historical session rows.
- Persisting SDP or ICE payloads.
- Refactoring unrelated endpoint or persistence code.
