# Device Identity Auth (Phase 3) Design

## Scope

Phase 3 of issue #26 migrates the Windows publisher and Flutter viewer from
ASP.NET Core Identity and the old owner-scoped `Device` API to the
`DeviceIdentity` and `DeviceBearer` contracts delivered by Phases 1 and 2.
The clients will bootstrap once, store their device credential in the secure
store already used by each application, exchange that credential for
short-lived access tokens, and use those tokens for sessions, signaling, and
TURN credentials.

This is a cross-repository change involving:

- `vitorhugo-dotnet/windows_SonicRelay`;
- `vitorhugo-dotnet/flutter_SonicRelay`; and
- this backend only for contract-level end-to-end verification and
  documentation. No backend production change is currently required.

The Phase 2 branch was compared with GitHub before this design was written.
Its head is `9b9a491`, matching PR #28, and it is 24 commits ahead of and zero
commits behind `main` at `82a10fd`. The Windows and Flutter inspections used
their current `main` heads, `5113ef7` and `738a885`, respectively.

## Non-negotiable separation of codes

The clients must present two distinct concepts and must not reuse their
contracts, labels, storage, or view models:

1. A **device pairing challenge** is created by a publisher through
   `POST /api/pairings/challenges` and redeemed once by a viewer through
   `POST /api/pairings/complete`. It establishes a durable `DevicePairing`.
2. A **session join code** is returned by `POST /api/sessions` (or code
   rotation), expires with that streaming session, and is submitted to
   `POST /api/sessions/join` whenever a viewer joins a stream.

Pairing does not join a stream. Joining a stream does not establish trust.
Neither code is persisted or written to logs. Labels always say either
“pairing code” or “session join code”; the generic term “connection code” is
not used.

## Existing client findings

### Windows

The Windows client currently logs in through `/auth/login`, stores an Identity
access/refresh-token pair through `ITokenStore`, registers an owner-scoped
Windows device through `POST /api/devices`, and sends that device ID in both
the session-create body and signaling query string. Its session DTOs also
still deserialize the removed `ownerUserId` field.

The client already has platform-secure token stores (Windows user-scoped
storage and Linux Secret Service), an API request wrapper that supplies bearer
headers, a reconnecting signaling client, and a session-code card. Those
components should be adapted rather than duplicated.

### Flutter

The Flutter client currently displays login UI, stores Identity access and
refresh tokens in `flutter_secure_storage`, registers an old
`flutter_viewer` device, sends its stored device ID in the join body and
signaling query string, and refreshes Identity tokens on authorization
failures.

It already has `flutter_secure_storage`, Dio authorization interception,
session-join UI, and reconnecting signaling/WebRTC services. Phase 3 replaces
the credential model underneath these components and removes account-oriented
screens from the primary route graph; it does not rewrite the media stack.

## Device credential model

Each installation stores one atomic credential record:

```text
deviceId
credentialSecret
credentialVersion
deviceType
platform
```

The persistent `credentialSecret` is written only to the platform-secure
store. The short-lived DeviceBearer access token may be cached in memory and
in the same secure record where needed for process restoration, together with
its absolute `expiresAt`; it must never be treated as the durable credential.
There is no refresh token in the device protocol.

At startup a `DeviceIdentitySession` service performs the following serialized
operation so concurrent API calls cannot bootstrap or refresh twice:

1. Read the secure device credential.
2. If absent, call `/api/devices/bootstrap` with the fixed client type/platform
   and a non-sensitive display name, then securely persist the complete result
   before continuing.
3. If the cached access token is absent or near expiry, call
   `/api/devices/token` with the stored device ID and secret.
4. Return the access token to HTTP, WebSocket, and TURN callers.

An HTTP 401 from `/api/devices/token` means the durable credential was revoked,
rotated elsewhere, or lost server-side. The client clears the unusable local
record and returns to device setup; it does not fall back to Identity. Network
failures retain the credential and offer retry. Bootstrap is never used as an
automatic response to an ordinary transient failure.

The existing Windows `TokenSet` and Flutter `AuthSession` account models are
not stretched to represent device credentials: refresh-token and user fields
would encode invariants that no longer exist. They are replaced in the active
application flow by explicit device credential/access-token models. Old
Identity code may remain temporarily in source for Phase 4 cleanup only if it
is unreachable from production composition; it must not provide a parallel
session authentication path.

## Windows publisher flow

On first launch the application bootstraps a `windows_publisher/windows`
identity and stores it using the current platform-secure implementation. On
later launches it exchanges that credential for a fresh token without showing
login or registration UI.

Session creation sends only `{ maxViewers }`; `sourceDeviceId` and
`ownerUserId` are removed from request/response assumptions. The returned
session join code continues to appear in `SessionCodeCard` and is clearly
labelled as a session code.

A separate pairing action creates a challenge and displays its one-time code,
expiry, and a refresh action. It must not replace or overwrite the active
session code. The API-provided `qrPayload` is retained in the client contract
so a renderer can be added without changing the protocol, but this phase does
not add a QR package: the repository currently has no renderer and the task
explicitly prohibits unrequested dependency updates.

The paired-device surface calls
`GET /api/devices/{authenticatedDeviceId}/pairings` and displays the viewer
device ID, status, and creation/last-used timestamps. Revocation calls
`DELETE /api/pairings/{pairingId}` after confirmation and refreshes the list.
It revokes the relationship, not either device credential.

Signaling `ConnectAsync` accepts only `sessionId`. Its URI contains only the
`sessionId` query parameter, and every initial/reconnect attempt asks the
device-session service for a current access token. TURN requests use the same
token source.

## Flutter viewer flow

On first launch the application bootstraps a
`flutter_viewer/android|ios` identity and persists the credential in
`flutter_secure_storage`. The app opens its pairing screen rather than a login
screen. The viewer enters the publisher's pairing challenge ID and code (the
challenge ID may later come from the QR payload); successful completion stores
no pairing code because trust is server-side and the code is single-use.

After pairing, the normal home screen continues to accept the separate
short-lived session join code. `POST /api/sessions/join` sends only `{ code }`.
An unauthorized join is described as a device authorization problem, never as
an expired human login.

Flutter signaling removes `deviceId` from the WebSocket URI but may retain the
authenticated device ID in local message metadata where the signaling
protocol currently uses it as `from`; the server remains authoritative and
overwrites/routs identity from the authenticated participant. Each reconnect
resolves a current DeviceBearer token. ICE-server and WebRTC-stats requests
use the same interceptor/token source.

The viewer pairing-management surface lists publisher pairings through the
same authenticated-device endpoint and can revoke them with confirmation.
Revocation does not erase the viewer's own credential. A local “reset this
device” action, if retained, must clearly explain that clearing secure storage
creates a new server-side identity on the next bootstrap and cannot recover
the old one.

## HTTP authentication and renewal

Both clients use a single-flight token provider. Before authenticated HTTP or
WebSocket work it returns a token with a small expiry margin. On a single 401,
the HTTP layer may force one token exchange and replay an idempotent request or
an explicitly replay-safe client operation once. It must not blindly replay
session creation, challenge creation, pairing completion, or code rotation.
Those operations surface the failure for deliberate retry to avoid duplicate
side effects.

Tokens and credential secrets are redacted from diagnostics. Pairing and
session codes, QR payloads, SDP, and complete ICE candidates are not logged.
Device IDs and session IDs follow the clients' existing diagnostic policy but
must not be presented as authentication secrets.

## Pairing precondition for sessions

The present backend pairing relationship is management data: Phase 2 session
join authorization is based on the `session:join` device scope and session
code, not a lookup requiring an active pairing to the publisher. Phase 3 does
not quietly add that backend rule. The UX pairs clients once as requested, but
the existing session code remains the authorization gate for each stream.
Enforcing “only actively paired viewers may join this publisher” would be a
separate backend security-policy change with migration and integration-test
impact and must be designed explicitly if desired.

## Error and lifecycle behavior

- Missing secure credential: bootstrap once.
- Valid credential and expired/missing access token: exchange for a token.
- Revoked/invalid credential: clear local credential, explain that device
  setup is required, and never attempt Identity login.
- Offline/token endpoint unavailable: preserve the credential and retry with
  bounded backoff or an explicit user action.
- Pairing challenge invalid, expired, or consumed: show one deliberately
  indistinguishable invalid/expired message, matching the backend's
  non-disclosure contract.
- Session join code invalid/expired: retain the existing session-specific
  error and do not send the user back through pairing.
- Pairing revoked: remove it from the active pairing list; current streaming
  behavior remains governed by the session/device token rules above.
- Device revoked during a session: the next HTTP request or WebSocket
  reconnect fails; the client stops reconnecting and returns to device setup.

## TDD and cross-repository verification

Implementation begins with failing client tests, in this order:

1. Secure credential serialization, atomic persistence, deletion, and
   restoration in both clients.
2. Bootstrap/token single-flight behavior, expiry refresh, invalid-credential
   reset, transient-failure retention, and secret redaction.
3. Exact Phase 2 session request/response contracts: no owner user or supplied
   device ID.
4. Exact signaling URI and reconnect token behavior: `sessionId` only.
5. Pairing challenge/completion/list/revoke repositories and view models,
   proving pairing and session codes stay independent.
6. Composition/navigation tests proving account login/register is not in the
   production flow and no Identity token can authenticate a session.

Focused tests run after each red/green cycle. Full client suites are reserved
for final verification. Cross-repository acceptance then runs the Phase 2
backend with clean persistence and drives this sequence against public APIs:

1. Windows first launch bootstraps and stores its credential.
2. Windows creates a pairing challenge.
3. Flutter first launch bootstraps and completes that pairing.
4. Both restart, exchange their persisted credentials for new tokens, and
   list the same active pairing without re-entering the pairing code.
5. Windows creates a stream and displays a new session join code.
6. Flutter joins using that session code, obtains TURN credentials, and both
   connect signaling with token plus `sessionId` only.
7. The signaling readiness/offer/answer/ICE flow completes.
8. Pairing revocation updates both management views without conflating it with
   the session code.
9. Device revocation prevents the affected client's next API/WebSocket
   reconnection.

Backend Phase 1/2 integration tests remain the contract oracle. No full suite
is run until the focused client tests and the real cross-repository flow pass.

## Documentation

Each client README will document first-run device setup, secure-storage
behavior, credential loss/reset, pairing, session joining, and the absence of
account login. Backend protocol/security documentation will only be changed if
the implementation reveals a stale client contract; no server behavior is
redesigned in this phase.

## Non-goals

- Restoring Identity-based session authentication or retaining it as fallback.
- Reintroducing client-supplied device identity to session or signaling calls.
- Unifying pairing challenges with session join codes.
- Removing backend Identity or old Device CRUD (Phase 4).
- Adding a backend pairing requirement to session join.
- Adding QR rendering/scanning packages without explicit dependency approval;
  the existing secure QR payload remains part of the client contract.
- Refactoring WebRTC media behavior unrelated to authentication.

## Design validation

The design was checked against issue #26, PR #28, the Phase 1/2 specs, current
backend endpoint contracts, and the current Windows and Flutter authentication,
secure-storage, session, signaling, and TURN implementations. It requires no
parallel authentication model, preserves the Phase 2 contract, maintains the
pairing/session-code boundary, and identifies no blocking backend addition.
The implementation plan must be written from this design and must retain the
TDD ordering and focused-test discipline above.
