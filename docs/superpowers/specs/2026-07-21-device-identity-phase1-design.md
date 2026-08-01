# Device Identity Auth (Phase 1) Design

## Scope

Implements Phase 1 of issue #26: introduce device-credential authentication as a
parallel, opt-in flow alongside the existing ASP.NET Core Identity login. This PR
does **not** touch `ApplicationUser`, the existing owner-scoped `Device` entity,
`StreamSession`/`SessionParticipant`, signaling, or TURN credentials — migrating
those to device ownership is Phase 2 of the issue and is a separate PR. Windows
and Flutter client changes are Phase 3 and also out of scope here.

New capability added:

- `POST /api/devices/bootstrap`
- `POST /api/devices/token`
- `POST /api/devices/rotate-credential`
- `POST /api/devices/revoke`
- `POST /api/pairings/challenges`
- `POST /api/pairings/complete`
- `GET  /api/devices/{deviceId}/pairings`
- `DELETE /api/pairings/{pairingId}`

## Domain model

New namespace `SonicRelay.Domain.DeviceIdentities`, intentionally separate from
`SonicRelay.Domain.Devices.Device` (which stays owner-scoped and untouched):

```text
DeviceIdentity
  Id, Name, DeviceType (windows_publisher | flutter_viewer), Platform
  CredentialSecretHash, CredentialVersion
  Status (active | revoked)
  CreatedAt, LastSeenAt, RevokedAt

PairingChallenge
  Id, PublisherDeviceId
  CodeHash, ExpiresAt, MaxAttempts, AttemptCount, ConsumedAt
  CreatedAt

DevicePairing
  Id, PublisherDeviceId, ViewerDeviceId
  Status (active | revoked)
  CreatedAt, LastUsedAt, RevokedAt
```

A new EF Core migration adds `device_identities`, `pairing_challenges`, and
`device_pairings` tables to `AppDbContext`. No existing table changes.

## Credential model

Asymmetric proof-of-possession is deferred: the issue permits this trade-off for
MVP ("prefer it only if it doesn't add much complexity to the MVP"), and
implementing challenge-response signing on both clients now would. Instead:

- `POST /api/devices/bootstrap` generates a 256-bit CSPRNG secret server-side and
  returns it once, Base64url-encoded, alongside the new `deviceId`.
- Only `HMAC-SHA256(secret)`, keyed by a configured pepper
  (`DeviceIdentity:CredentialHmacKey`, same pattern as `Sessions:CodeHmacKey`),
  is persisted — never the plaintext secret.
- `PublicKey` is reserved on `DeviceIdentity` for future asymmetric support but
  is unused in Phase 1.

This trade-off is recorded in ADR 0005.

## Token issuance

A new JWT bearer scheme, `DeviceBearer`, is added alongside the existing
`Identity.Bearer` scheme (ADR 0002) — the two do not interact. `POST
/api/devices/token` exchanges `{ deviceId, credentialSecret }` for a short-lived
JWT (default 5 minutes, `DeviceIdentity:AccessTokenMinutes`) with:

- `sub`: device ID
- `device_type`: `windows_publisher` | `flutter_viewer`
- `scope`: space-separated scopes granted for that device type
- `cv`: the device's `CredentialVersion` at issuance time
- standard `iss`, `aud`, `exp`, `jti`

Scopes: `device:read`, `device:manage`, `pairing:create`, `pairing:complete`,
`pairing:revoke`. Publisher devices receive `pairing:create`; viewer devices
receive `pairing:complete`; both receive `device:read`, `device:manage`,
`pairing:revoke`.

A custom authorization requirement checks the `scope` claim **and** re-reads the
device row on every request, rejecting the request unless `Status == active` and
`CredentialVersion == cv`. This means rotation and revocation take effect
immediately, without a token blocklist — directly satisfying the issue's
rotation/revocation test requirements even though tokens are self-contained
JWTs.

`rotate-credential` requires the current secret again (proof of possession),
issues a new secret, and increments `CredentialVersion`, invalidating every
previously issued token. `revoke` sets `Status = revoked`; any subsequent
`/token` call or scoped request fails.

## Pairing flow

`POST /api/pairings/challenges` (publisher, `pairing:create`) generates a code
using the same pattern as existing session codes (`RandomNumberGenerator` over
an uppercase alphanumeric alphabet), persists only its hash plus a short TTL
(`DeviceIdentity:PairingCodeTtlMinutes`, default 5) and `MaxAttempts` (default
5), and returns the plaintext code once plus a QR payload — a small JSON object
containing `challengeId` and `code`, no persistent secret.

`POST /api/pairings/complete` (viewer, `pairing:complete`) validates the hash,
expiry, and attempt count; a mismatch increments `AttemptCount` and returns a
generic invalid-code error indistinguishable from "expired" or "not found"
(matches the existing session-code convention of not revealing which case
applied). A match consumes the challenge (`ConsumedAt`) and creates a
`DevicePairing`. Both endpoints are additionally rate-limited (IP for challenge
creation is not enough on its own since a stolen viewer token could brute-force
codes, so `pairing-complete` is keyed by device ID and IP).

`GET /api/devices/{deviceId}/pairings` and `DELETE /api/pairings/{pairingId}`
are restricted to a caller whose authenticated device ID is a participant in
the pairing.

## Feature flag

`DeviceIdentity:Enabled` (config, default `true`) gates whether Program.cs
registers the `DeviceBearer` scheme and maps the new endpoint groups at all.
Setting it to `false` removes this PR's behavior entirely with no code changes,
satisfying the issue's Phase 1 requirement for a flag to choose the flow.

## Rate limiting and logging

New policies follow the existing `AddRateLimiter` pattern (IP-keyed for
anonymous endpoints, device-keyed for authenticated ones): `device-bootstrap`,
`device-token`, `pairing-create`, `pairing-complete`. Defaults and config
section names mirror the existing `RateLimits:*` convention.

No credential secret, pairing code, JWT, or QR payload is ever logged — logs
carry only device/challenge/pairing IDs and outcomes, matching the existing
signaling-log convention.

## Documentation

- `docs/adr/0005-device-identity-credentials.md`: records the symmetric-secret
  trade-off and the parallel-scheme approach.
- `docs/device-identity.md`: describes bootstrap, token, rotation, revocation,
  and pairing flows (same style as `docs/account-deletion.md`).
- `docs/security.md` and `docs/architecture.md`: append the new controls and
  entities; existing sections are not restructured.

## Testing

Follows the existing convention: tests live in
`tests/SonicRelay.Api.IntegrationTests` (EF Core InMemory + in-memory
distributed cache; no Docker/Postgres/Redis required), covering:

- bootstrap issuing a usable credential; secret never persisted in plaintext;
- token issuance success/failure (wrong secret, revoked device, unknown device
  ID) without revealing which case applied;
- rotation invalidating tokens issued under the previous `CredentialVersion`;
- revocation blocking both `/token` and already-issued (but not yet expired)
  access tokens;
- pairing challenge expiry, consumption (single use), and attempt-limit
  rejection;
- successful pairing end to end (bootstrap both devices, challenge, complete,
  list, revoke);
- scope enforcement (a viewer-scoped token cannot call `pairing/challenges`,
  etc.);
- isolation between two independent publisher/viewer device pairs.

## Non-goals

- Any change to `ApplicationUser`, `Device`, `StreamSession`,
  `SessionParticipant`, signaling, or TURN credential issuance.
- Windows/Flutter client implementation or secure local storage.
- Removing or deprecating Identity endpoints.
- Asymmetric (public-key) device credentials.
- Reviewing or closing issue #20.
