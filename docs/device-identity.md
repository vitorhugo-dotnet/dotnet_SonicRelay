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

## Out of scope in Phase 1

`ApplicationUser`, the existing owner-scoped `Device`, `StreamSession`,
signaling, and TURN credential issuance are unchanged. See issue #26 for the
remaining phases.
