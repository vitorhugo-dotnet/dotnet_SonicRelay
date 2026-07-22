# Security

This document separates controls present in the current code from work still required for production hardening.

## Implemented controls

### Identity and tokens

- ASP.NET Core Identity stores users and roles in PostgreSQL and requires unique email addresses.
- Desktop/mobile clients use opaque Identity bearer and refresh tokens. Defaults are 15 minutes and 30 days, configurable through `Auth:AccessTokenMinutes` and `Auth:RefreshTokenDays`.
- Protected API groups and the WebSocket endpoint require authentication.
- Account/email confirmation is not required by the current Identity configuration.
- `/auth/logout` returns success but does not maintain a token revocation list; clients must delete local tokens.

### Authorization and isolation

- Session creation requires a `session:create`-scoped `DeviceBearer` token; the caller's own authenticated device is always the session's source device.
- Session reads (`GET /api/sessions/active`, `GET /api/sessions/{id}`) are limited to sessions where the caller's device is the source or a participant and return `404` otherwise.
- End and code rotation operations require a `session:end`-scoped token and require the caller's device to be the session's source device.
- Join requires a `session:join`-scoped token and enforces the session viewer limit; the joining device is always the caller's own, never a client-supplied one.
- WebSocket upgrade requires a `signaling:connect`-scoped token and a matching session participant record for the caller's device.
- Signaling routing always uses the authenticated participant as `from` and restricts recipients to the same session.

The named policies `session:create`, `session:join`, `session:end`, `signaling:connect` and `turn:credentials` each require a `DeviceBearer` token carrying the matching scope; `DeviceScopeAuthorizationHandler` also re-checks the device's live status and credential version against the database on every request, so revocation and credential rotation take effect immediately. `CanRegisterDevice`, used only by the unrelated, pre-existing owner-scoped `Device` CRUD feature, still just requires authentication.

### Session codes

- Codes are generated with `RandomNumberGenerator` from 36 uppercase alphanumeric symbols.
- Redis keys use HMAC-SHA-256 output keyed by `Sessions:CodeHmacKey`; plaintext codes are returned only when created/rotated.
- Redis entries have an absolute TTL and rotation removes the previous lookup.
- Expired/invalid code responses are deliberately indistinguishable.
- Background cleanup marks elapsed sessions expired, removes their code and prunes disconnected participants after the configured retention period.

Current limitation: successful join lookup does not consume a code. A code can be reused until rotation, session end or expiry.

### Device identity credentials (Phase 1 of issue #26)

- Device bootstrap issues a high-entropy secret once; only its HMAC-SHA-256
  output, keyed by `DeviceIdentity:CredentialHmacKey`, is persisted.
- Access tokens are short-lived JWTs on a separate `DeviceBearer` scheme;
  every scoped request re-checks device status and credential version against
  the database, so rotation and revocation take effect immediately.
- Pairing codes follow the session-code convention: HMAC-hashed, short TTL,
  attempt-limited, and indistinguishable failure responses. `pairing-create`
  and `pairing-complete` are rate-limited by IP, not by device: per-device
  keying was evaluated but would require making `DeviceBearer` the app's
  default authentication scheme, which is out of scope for this phase, so
  `DeviceIdentity:PairingMaxAttempts` remains the primary defense against
  pairing-code brute-forcing.
- The entire flow is gated by `DeviceIdentity:Enabled` and does not affect
  the existing Identity login endpoints.
- Sessions, signaling and TURN credential issuance (Phase 2 of issue #26)
  now authenticate exclusively via `DeviceBearer`; the previous
  Identity-based, `ApplicationUser`-owned session path no longer exists for
  these routes. `DeviceScopeAuthorizationHandler`'s live device-status and
  credential-version check — the same one backing the scoped
  `session:*`/`signaling:connect`/`turn:credentials` policies above — also
  protects three read-only routes that need no capability beyond an active
  device: `GET /api/sessions/active`, `GET /api/sessions/{id}` and
  `POST /api/webrtc/stats`, via a scope-less `DeviceAuthenticated` policy
  rather than a capability-scoped one. `DeviceIdentity:Enabled` now only
  gates the bootstrap/token/rotate-credential/revoke/pairing HTTP surface
  above; sessions, signaling and TURN have no fallback authentication path
  and require `DeviceBearer` regardless of the flag.

### Abuse and data exposure

- Fixed-window limits return `429`: login, refresh, device-bootstrap, device-token, pairing-create, pairing-complete, create-session, join-session and rotate-code are all keyed by IP. Create/join/rotate cannot be keyed by device or user: `DeviceBearer` tokens carry no claim a per-caller limiter could key on (see [Device identity credentials](#device-identity-credentials-phase-1-of-issue-26)).
- Defaults per 60-second window are login `5`, refresh `5`, create `10`, join `10`, rotate `5`, device-bootstrap `10`, device-token `10`, pairing-create `10`, pairing-complete `10`.
- Signaling frames are limited to 64 KiB text messages.
- Signaling logs record routing metadata only; SDP and ICE payloads are not logged by the endpoint.
- Readiness checks include PostgreSQL and Redis; liveness does not expose dependency state.

## Secrets and deployment

- Set a high-entropy `Sessions__CodeHmacKey`; the Compose development fallback is not production-safe.
- Keep PostgreSQL, Redis, TURN and SSH credentials outside Git. The CI deploy script expects runtime secrets in `/opt/sonicrelay/.env` (or the configured app directory).
- The automated Compose file binds the API to `127.0.0.1:8080` by default; terminate TLS at a reverse proxy.
- TURN/STUN must use its native ports and should not be placed behind a normal HTTP reverse proxy.

## Known production gaps

- Device ownership and lifecycle are enforced by handlers; policy names alone do not express those resource checks.
- There is no CORS configuration. Browser-based clients need an explicit allowlist before use.
- There is no access/refresh-token server-side revocation mechanism.
- `ApplicationUser.IsDisabled` exists but is not checked by endpoint authorization.
- Email confirmation is disabled even though the production environment example suggests it should be required; `AUTH_REQUIRE_CONFIRMED_EMAIL` is not read by the application.
- The live signaling registry is in memory, preventing safe multi-replica routing without sticky sessions or a backplane.
- TURN uses static configuration; temporary per-session TURN credentials are not issued by the API.
- The API-only CI deployment does not provision PostgreSQL, Redis, coturn, TLS or backups.
- No explicit request-body limit is documented for HTTP endpoints beyond server defaults.

Before internet-facing production use, close these gaps, restrict network exposure, configure backups/restore testing, rotate secrets, and add operational alerting.
