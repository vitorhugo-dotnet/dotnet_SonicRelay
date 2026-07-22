# Device Identity Auth (Phase 1)

This document describes the device-credential flow added alongside the
existing Identity login (see `docs/adr/0005-device-identity-credentials.md`).
It covers backend behavior only; Windows/Flutter client integration is a
later phase of issue #26.

## Flow

1. `POST /api/devices/bootstrap` — a device registers with a `name`,
   `deviceType` (`windows_publisher` or `flutter_viewer`), and `platform`.
   The response includes the device ID and a credential secret returned
   exactly once; only its HMAC is ever stored.
2. `POST /api/devices/token` — the device exchanges its ID and secret for a
   short-lived JWT (`DeviceIdentity:AccessTokenMinutes`, default 5 minutes)
   carrying scopes appropriate to its device type.
3. `POST /api/devices/rotate-credential` — requires the current secret and a
   valid `device:manage`-scoped token; issues a new secret and invalidates
   every token issued under the previous credential version immediately.
4. `POST /api/devices/revoke` — marks the device revoked; blocks future token
   requests and rejects already-issued tokens on their next authorized
   request, without waiting for expiry.

## Pairing

1. A publisher device with a `pairing:create`-scoped token calls
   `POST /api/pairings/challenges` and receives a short-TTL code
   (`DeviceIdentity:PairingCodeTtlMinutes`, default 5 minutes) plus a QR
   payload containing the challenge ID and code — no persistent secret is
   ever embedded in the QR payload.
2. A viewer device with a `pairing:complete`-scoped token calls
   `POST /api/pairings/complete` with the code. A wrong code, expired
   challenge, and already-consumed challenge all return the same generic
   error and increment the attempt counter; the challenge is rejected outright
   after `DeviceIdentity:PairingMaxAttempts` (default 5) failed attempts.
3. `GET /api/devices/{deviceId}/pairings` and `DELETE /api/pairings/{pairingId}`
   are restricted to a caller whose authenticated device ID participates in
   the pairing.

`pairing-create` and `pairing-complete` are rate-limited by caller IP address
(the same keying as `login`/`refresh`), not by device ID. Per-device keying
was evaluated but is not currently achievable without making `DeviceBearer`
the app's default authentication scheme, which is out of scope for this
phase (`UseRateLimiter()` runs before the `DeviceBearer` principal is
populated by `UseAuthorization()`'s explicit `AddAuthenticationSchemes` call).
`DeviceIdentity:PairingMaxAttempts` is the primary defense against pairing-code
brute-forcing regardless of rate-limit keying granularity.

## Configuration

| Key | Purpose |
| --- | --- |
| `DeviceIdentity:Enabled` | Feature flag; `false` removes this flow's HTTP surface entirely. |
| `DeviceIdentity:CredentialHmacKey` | Server-side pepper for hashing device credential secrets. |
| `DeviceIdentity:PairingCodeHmacKey` | Server-side pepper for hashing pairing codes. |
| `DeviceIdentity:TokenSigningKey` | Symmetric signing key for `DeviceBearer` JWTs. |
| `DeviceIdentity:AccessTokenMinutes` | Access token lifetime (default 5). |
| `DeviceIdentity:PairingCodeTtlMinutes` | Pairing challenge TTL (default 5). |
| `DeviceIdentity:PairingMaxAttempts` | Max failed pairing attempts before a challenge is rejected (default 5). |

Set high-entropy values for `CredentialHmacKey`, `PairingCodeHmacKey`, and
`TokenSigningKey` outside Git, the same way `Sessions:CodeHmacKey` is handled.

## Sessions, signaling, and TURN (Phase 2)

Session lifecycle, WebRTC signaling and TURN credential issuance now
authenticate exclusively through `DeviceBearer`. `DeviceCredentialService.ScopesFor`
issues five additional scopes alongside `device:read`/`device:manage`/the
pairing scopes, split by device type:

| Scope | Windows Publisher | Flutter Viewer | Used by |
| --- | --- | --- | --- |
| `session:create` | yes | — | `POST /api/sessions` |
| `session:end` | yes | — | `POST /api/sessions/{sessionId}/end`, `POST /api/sessions/{sessionId}/rotate-code` |
| `session:join` | — | yes | `POST /api/sessions/join` |
| `signaling:connect` | yes | yes | `GET /ws/signaling` |
| `turn:credentials` | yes | yes | `GET /api/webrtc/ice-servers` |

Two read-only session routes (`GET /api/sessions/active`, `GET /api/sessions/{sessionId}`)
and the WebRTC stats endpoint (`POST /api/webrtc/stats`) require only the
scope-less `DeviceAuthenticated` policy — any active device presenting a
current credential version — since they need no capability beyond being a
live, authenticated device.

Neither session creation nor joining takes a client-supplied device id any
more: the caller's own authenticated device (from the token's `sub` claim) is
always the publisher of a session it creates and always the viewer that joins
one, so there is nothing left for a client to assert about which device it
is. The WebSocket handshake follows the same rule: `GET /ws/signaling` takes
only `sessionId` as a query parameter — the previous `deviceId` parameter is
gone, and the connecting participant's device comes from the bearer token.

`DeviceIdentity:Enabled` has a narrower meaning than the rest of this
document might suggest: it gates only the bootstrap/token/rotate-credential/
revoke/pairing HTTP surface described above. Sessions, signaling and TURN
credential issuance are unconditional — they always require a valid
`DeviceBearer` token regardless of the flag, since there is no other
authentication path left for them to fall back to.

`create-session`, `join-session` and `rotate-code` are rate-limited by caller
IP address, the same keying (and for the same reason) as
`pairing-create`/`pairing-complete` above: `DeviceBearer` tokens carry no
claim a per-device or per-user limiter could key on without making
`DeviceBearer` the app's default authentication scheme, which remains out of
scope.

## Out of scope in Phase 1

`ApplicationUser` and the existing owner-scoped `Device` entity are
unchanged by this document's Phase 1 flow. `StreamSession`, signaling, and
TURN credential issuance moved to `DeviceBearer` in Phase 2 — see
[Sessions, signaling, and TURN (Phase 2)](#sessions-signaling-and-turn-phase-2)
above. See issue #26 for the remaining phases.
