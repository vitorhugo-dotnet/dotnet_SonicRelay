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

- Session creation requires an owned, non-revoked source device.
- Session reads are limited to owners or participants and return `404` to other users.
- End and code rotation operations require session ownership.
- Join requires an owned, non-revoked viewer device and enforces the session viewer limit.
- WebSocket upgrade requires matching authenticated user, device and session participant records.
- Signaling routing always uses the authenticated participant as `from` and restricts recipients to the same session.

The named policies `CanRegisterDevice`, `CanCreateSession`, `CanJoinSession`, `CanPublishSession` and `CanViewSession` currently only require authentication; resource-specific checks live in handlers. Device handlers do not yet implement ownership or persistence.

### Session codes

- Codes are generated with `RandomNumberGenerator` from 36 uppercase alphanumeric symbols.
- Redis keys use HMAC-SHA-256 output keyed by `Sessions:CodeHmacKey`; plaintext codes are returned only when created/rotated.
- Redis entries have an absolute TTL and rotation removes the previous lookup.
- Expired/invalid code responses are deliberately indistinguishable.
- Background cleanup marks elapsed sessions expired, removes their code and prunes disconnected participants after the configured retention period.

Current limitation: successful join lookup does not consume a code. A code can be reused until rotation, session end or expiry.

### Abuse and data exposure

- Fixed-window limits return `429`: login and refresh are keyed by IP; create, join and rotate are keyed by user ID (falling back to IP).
- Defaults per 60-second window are login `5`, refresh `5`, create `10`, join `10`, rotate `5`.
- Signaling frames are limited to 64 KiB text messages.
- Signaling logs record routing metadata only; SDP and ICE payloads are not logged by the endpoint.
- Readiness checks include PostgreSQL and Redis; liveness does not expose dependency state.

## Secrets and deployment

- Set a high-entropy `Sessions__CodeHmacKey`; the Compose development fallback is not production-safe.
- Keep PostgreSQL, Redis, TURN and SSH credentials outside Git. The CI deploy script expects runtime secrets in `/opt/sonicrelay/.env` (or the configured app directory).
- The automated Compose file binds the API to `127.0.0.1:8080` by default; terminate TLS at a reverse proxy.
- TURN/STUN must use its native ports and should not be placed behind a normal HTTP reverse proxy.

## Known production gaps

- Device endpoints are stubs, so they do not enforce ownership, validate input or persist/revoke devices.
- There is no CORS configuration. Browser-based clients need an explicit allowlist before use.
- There is no access/refresh-token server-side revocation mechanism.
- `ApplicationUser.IsDisabled` exists but is not checked by endpoint authorization.
- Email confirmation is disabled even though the production environment example suggests it should be required; `AUTH_REQUIRE_CONFIRMED_EMAIL` is not read by the application.
- The live signaling registry is in memory, preventing safe multi-replica routing without sticky sessions or a backplane.
- TURN uses static configuration; temporary per-session TURN credentials are not issued by the API.
- The API-only CI deployment does not provision PostgreSQL, Redis, coturn, TLS or backups.
- No explicit request-body limit is documented for HTTP endpoints beyond server defaults.

Before internet-facing production use, close these gaps, restrict network exposure, configure backups/restore testing, rotate secrets, and add operational alerting.
