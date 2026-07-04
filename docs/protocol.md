# HTTP and WebSocket protocol

This document describes routes mapped by the current API. Unless marked public, HTTP requests require `Authorization: Bearer <opaque-access-token>`.

## Health

| Method | Route | Auth | Behavior |
| --- | --- | --- | --- |
| `GET` | `/health/live` | Public | Process liveness only; excludes registered dependency checks. |
| `GET` | `/health/ready` | Public | Checks PostgreSQL and Redis. |

Swagger is enabled by default only in Development, or when `Swagger:Enabled=true`.

## Authentication

`MapIdentityApi<ApplicationUser>` maps the standard ASP.NET Core Identity API under `/auth`: `/register`, `/login`, `/refresh`, `/confirmEmail`, `/resendConfirmationEmail`, `/forgotPassword`, `/resetPassword`, `/manage/2fa` and `/manage/info`. The framework owns their request/response contracts. This project additionally maps:

| Method | Route | Auth | Behavior |
| --- | --- | --- | --- |
| `POST` | `/auth/logout` | Required | Returns `204`; it does not revoke already issued self-contained bearer tokens. |
| `GET` | `/auth/me` | Required | Returns `id`, `email`, `displayName`, `emailConfirmed`, `createdAt` and `lastLoginAt`. |

For desktop/mobile login, omit cookies or use `POST /auth/login?useCookies=false`. The response contains an opaque bearer access token, expiry and refresh token; it is not a JWT.

```json
{
  "tokenType": "Bearer",
  "accessToken": "<opaque-access-token>",
  "expiresIn": 900,
  "refreshToken": "<opaque-refresh-token>"
}
```

Login and refresh use fixed-window, IP-keyed rate limits.

## Devices

All device routes require authentication, but their current handlers are stubs.

| Method | Route | Current behavior |
| --- | --- | --- |
| `POST` | `/api/devices/` | Returns `201 Created` without creating a device or response body. |
| `GET` | `/api/devices/` | Returns `200 OK` without a device collection body. |
| `GET` | `/api/devices/{deviceId}` | Returns `200` with the route `deviceId`; no lookup or ownership check. |
| `PATCH` | `/api/devices/{deviceId}` | Returns `200` with the route `deviceId`; no update. |
| `DELETE` | `/api/devices/{deviceId}` | Returns `204`; no deletion. |
| `POST` | `/api/devices/{deviceId}/revoke` | Returns `200` with `deviceId` and `revoked: true`; no persistence. |

Session routes do perform real device ownership/revocation checks against PostgreSQL. Consequently, the public device lifecycle is not yet usable end to end.

## Sessions

All session routes require authentication.

| Method | Route | Behavior |
| --- | --- | --- |
| `POST` | `/api/sessions/` | Creates a waiting session and publisher participant for an owned, non-revoked source device; returns `201` and a six-character code. |
| `GET` | `/api/sessions/active` | Lists waiting/active sessions owned by or joined by the caller, including connected viewer count. |
| `GET` | `/api/sessions/{sessionId}` | Returns a session to its owner or participant; inaccessible sessions return `404`. |
| `POST` | `/api/sessions/{sessionId}/end` | Owner-only; marks the session ended, disconnects participants and removes the Redis code. Idempotently returns the ended session. |
| `POST` | `/api/sessions/{sessionId}/rotate-code` | Owner-only; rejects ended/expired sessions with `409`, invalidates the previous code and returns a new code. |
| `POST` | `/api/sessions/join` | Resolves a valid code and owned viewer device, enforces the viewer limit, creates/reconnects a participant and activates a waiting session. |

Create request:

```json
{ "sourceDeviceId": "<uuid>", "maxViewers": 3 }
```

`maxViewers` defaults to `Sessions:MaxViewersPerSession` and must be at least one. There is currently no upper bound.

Join request:

```json
{ "code": "ABC123", "deviceId": "<uuid>" }
```

Codes are trimmed, uppercased and must contain exactly six ASCII letters/digits. Wrong, malformed, expired and terminal-session codes all return `404` with the same error. Despite the store method name `RedeemAsync`, a successful lookup does not consume the code; it remains reusable until rotation, session end or expiry.

Session responses contain `id`, `ownerUserId`, `sourceDeviceId`, `status`, `maxViewers`, `codeExpiresAt`, `startedAt`, `endedAt`, `createdAt`, and `code` when a new code is issued.

## WebSocket signaling

Connect with an authenticated WebSocket upgrade:

```text
GET /ws/signaling?sessionId={uuid}&deviceId={uuid}
Authorization: Bearer <opaque-access-token>
```

Before upgrade, the API verifies:

- both query parameters are UUIDs;
- the session exists and is not ended, expired or past `codeExpiresAt`;
- the device belongs to the authenticated user and is not revoked;
- a participant matches the session, user and device.

Validation failures return HTTP `400`, `401`, `403`, `404` or `410` before the upgrade. After connection, the server sends:

```json
{ "type": "session.joined", "participantId": "<uuid>", "role": "publisher" }
```

### Client messages

`ping` requires no recipient and produces `{ "type": "pong" }`. These routed types require a UUID `to` participant and may include any JSON `payload`:

- `publisher.ready`
- `viewer.ready`
- `webrtc.offer`
- `webrtc.answer`
- `webrtc.ice_candidate`
- `pong`

The server ignores client-supplied sender/session metadata and emits a normalized routed frame:

```json
{
  "type": "webrtc.offer",
  "from": "<sender-participant-uuid>",
  "to": "<recipient-participant-uuid>",
  "payload": {}
}
```

Routing is constrained to the current session. Missing recipients produce an `error` frame with `participant_not_found`. Other error codes are `invalid_message`, `unsupported_message_type` and `invalid_recipient`.

Text messages may be fragmented but may not exceed 64 KiB. Binary frames are rejected. Disconnects broadcast `session.left` to other live participants. When the session becomes terminal, the server sends `session.ended` and closes routing for that connection. There is no persisted signaling history; `SignalingEvent` is mapped in EF Core but the endpoint does not write it.
