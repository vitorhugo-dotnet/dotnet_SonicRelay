# Device Identity Auth (Phase 3) Design

## Status and scope

This design continues issue #26 from the Phase 1-2 backend delivered by PR
#28. Phase 3 migrates the Windows publisher and Flutter viewer from human
Identity login to persistent device credentials and short-lived DeviceBearer
tokens. It also makes device pairing an authorization requirement for joining
a stream and delivers the complete QR pairing experience.

The work spans three repositories:

- `dotnet_SonicRelay`: require an active publisher/viewer pairing during
  session join and remain the contract oracle;
- `windows_SonicRelay`: bootstrap the publisher identity, display pairing
  challenges as code and QR, and use DeviceBearer for sessions, signaling,
  and TURN;
- `flutter_SonicRelay`: bootstrap the viewer identity, scan or enter a pairing
  challenge, and use DeviceBearer for sessions, signaling, and TURN.

The repositories remain independently buildable and testable. Backend work is
implemented first, followed by Windows and Flutter, then cross-repository
acceptance verification.

## Approved product and security decisions

- There is no Identity login or refresh-token fallback in the production
  client flow.
- A viewer must have an active `DevicePairing` with the publisher that owns a
  session before `POST /api/sessions/join` succeeds.
- Windows renders the backend-provided QR payload. Flutter scans it with the
  camera. Manual `challengeId + pairing code` entry remains available.
- Pairing challenges and session join codes are separate contracts, state,
  labels, and screens.
- Revoking a pairing prevents future joins but does not terminate a stream
  already joined. Device revocation continues to stop later authenticated
  HTTP calls and WebSocket reconnections.
- QR payloads contain only a challenge identifier and its short-lived,
  single-use code. They never contain a device credential or access token.

## Architecture

### Shared client model

Each installation owns one secure device credential record:

```text
deviceId
credentialSecret
credentialVersion
deviceType
platform
```

The persistent secret is stored only through the client's existing
platform-secure storage. DeviceBearer access tokens are short-lived and cached
in memory with an absolute expiration time. There is no refresh token.

Each client introduces two bounded components:

1. `DeviceCredentialStore` atomically saves, loads, and deletes the durable
   credential record.
2. `DeviceIdentitySession` serializes bootstrap and token exchange so
   concurrent HTTP, signaling, and TURN calls cannot create multiple device
   identities or refresh the same token multiple times.

At startup, `DeviceIdentitySession` loads the credential. If none exists, it
calls `POST /api/devices/bootstrap`, persists the complete returned credential
before continuing, and then exchanges it at `POST /api/devices/token`. If a
cached token is absent or close to expiry, it performs one token exchange and
shares the result with all waiters.

### Backend pairing authorization

`POST /api/sessions/join` continues to accept only `{ code }`; the viewer is
derived from the DeviceBearer principal. After resolving a valid live session
from the code, the endpoint queries for an active pairing where:

```text
publisherDeviceId == session.sourceDeviceId
viewerDeviceId == authenticated viewer deviceId
status == active
```

If no pairing exists, the endpoint returns the same public status and error
body used for an invalid or expired session code. The response does not reveal
whether the session exists or which publisher owns it. The consumed session
code behavior must remain compatible with a permitted viewer joining; the
authorization check must not make a valid code unusable solely because an
unpaired device attempted it.

Existing participants may reconnect to their already joined session even if
the pairing is later revoked. Pairing is checked when creating a new viewer
participation, not as a mechanism for terminating established streams.

### Windows publisher

The Windows application bootstraps as `windows_publisher/windows`. Its secure
credential replaces the active `TokenSet`/refresh-token flow; legacy Identity
types may remain temporarily only when unreachable from production
composition pending Phase 4.

Session creation sends only `{ maxViewers }`. Session response models no
longer require `ownerUserId`. Signaling connects with only `sessionId` in the
query and obtains a current DeviceBearer token for every initial connection
and reconnect. TURN uses the same token source.

The pairing surface creates challenges, displays the pairing code and expiry,
and renders the exact `qrPayload` using QRCoder 1.8.0. It supports explicit
refresh, active-pairing listing, and confirmed revocation. Challenge refresh
never replaces or mutates an active session join code.

### Flutter viewer

The Flutter application bootstraps as `flutter_viewer` with runtime platform
`android` or `ios`. The credential is stored atomically in
`flutter_secure_storage`. The router opens device setup/pairing rather than a
human login screen.

The pairing screen accepts manual challenge ID and code or scans the Windows
QR with `mobile_scanner` 7.4.0. Camera permission is requested only when the
scanner opens. The decoded payload is strictly validated before the API call:
it must be the expected JSON object containing a valid GUID challenge ID and a
pairing code of the expected format. Successful redemption stores no code or
QR payload because the durable relationship is server-side.

After pairing, the existing session screen accepts the distinct session join
code. Join sends only `{ code }`. Signaling removes `deviceId` from the URI,
resolves a current DeviceBearer token for every reconnect, and may retain the
device ID only as non-authoritative message metadata required by the current
envelope. ICE and stats calls use the same token provider.

## HTTP replay and token renewal

Authenticated HTTP, WebSocket, TURN, and stats transports ask
`DeviceIdentitySession` for a current token with a small expiry margin. A
single 401 may force one token exchange and replay an idempotent request once.
The clients do not automatically replay bootstrap, session creation, pairing
challenge creation/completion, pairing revocation, or session-code rotation.

An unauthorized token exchange means the durable credential is invalid,
revoked, or rotated elsewhere. The client deletes the unusable local record
and returns to device setup. Transient network failures preserve the record
and expose retry with bounded backoff; they never trigger automatic bootstrap.

## Error and lifecycle behavior

- Missing credential: bootstrap exactly once.
- Missing or near-expiry access token: perform one shared token exchange.
- Invalid durable credential: clear it and return to device setup.
- Offline token endpoint: retain the credential and allow retry.
- Malformed QR: reject locally without calling the backend.
- Denied camera permission: retain manual pairing entry.
- Invalid, expired, consumed, or incorrect pairing challenge: show one generic
  pairing failure.
- Invalid/expired session code or absent active pairing: show one generic
  session join failure.
- Revoked pairing: remove it from management views and reject future joins;
  do not terminate an established stream.
- Revoked device: stop authenticated retry/reconnect and return to setup when
  the revocation is observed.

## Logging and secret handling

The clients and backend must not log credential secrets, access tokens,
pairing codes, session codes, QR payloads, SDP, or complete ICE candidates.
Diagnostics must redact those values from errors and serialized request
bodies. Device and session identifiers follow the existing identifier-masking
policy and are never treated as authentication credentials.

The Windows QR image is generated in memory from the API payload and is not
written to disk. Flutter does not retain camera frames or the decoded payload
after pairing completes or the screen closes.

## Test-driven implementation

Every behavior change is implemented with a focused failing test first, the
smallest production change needed to pass, and a focused green rerun.

### Backend tests

- an actively paired viewer can join its publisher's session;
- an unpaired viewer gets the same response as an invalid code;
- a viewer paired to a different publisher cannot join;
- a revoked pairing prevents a new join;
- an attempted unpaired join does not consume a valid session code for the
  correctly paired viewer;
- revoking a pairing does not remove an existing participant;
- independent device pairs remain isolated.

### Windows tests

- atomic credential save/load/delete and secret redaction;
- bootstrap, token expiry margin, single-flight exchange, invalid-credential
  reset, and transient-failure retention;
- session creation serializes only `maxViewers`;
- signaling URI contains only `sessionId` and reconnect obtains a fresh token;
- QR rendering uses the exact API payload without persisting it;
- pairing challenge and session-code states cannot overwrite each other;
- production composition has no reachable human login path.

### Flutter tests

- atomic secure credential persistence and reset;
- bootstrap/token single-flight and one safe 401 renewal;
- QR payload parsing rejects malformed or unexpected data;
- scanner success and camera-denied manual fallback;
- join serializes only normalized `code`;
- signaling uses only `sessionId` and renews its token on reconnect;
- pairing and session state remain independent;
- production routing does not expose account login.

### Cross-repository acceptance

Against a clean Phase 3 backend:

1. Windows bootstraps and securely persists its device credential.
2. Windows creates a pairing challenge and displays code and QR.
3. Flutter bootstraps, scans the QR, and completes pairing.
4. Both clients restart and obtain tokens from persisted credentials.
5. Both list the same active pairing without re-entering the challenge.
6. Windows creates a stream and displays a separate session join code.
7. Flutter joins using that code, obtains TURN credentials, and connects
   signaling using a DeviceBearer token plus `sessionId` only.
8. Offer/answer/ICE and audio streaming complete.
9. Pairing revocation blocks a subsequent new join without terminating the
   already established stream.
10. Device revocation prevents the affected client's next authenticated
    request or WebSocket reconnection.

## Dependency choices

- Windows: QRCoder 1.8.0, using an in-memory renderer compatible with .NET 10
  and WinUI image presentation.
- Flutter: `mobile_scanner` 7.4.0 for Android/iOS camera scanning.

No QR-generation dependency is needed in Flutter, and Windows needs no camera
dependency. Platform camera declarations are limited to the Flutter targets
that require them.

## Documentation

Each client README documents first-run device setup, secure storage,
credential loss/reset, manual and QR pairing, the distinct session-code flow,
and the absence of account login. Backend protocol/security documentation is
updated for the active-pairing join requirement and its non-disclosing error
behavior.

## Non-goals

- Removing backend Identity tables and endpoints; that remains Phase 4.
- Restoring Identity-based session authentication as fallback.
- Sending client-selected device identity in session or signaling requests.
- Combining pairing challenges with session join codes.
- Terminating existing sessions when only a pairing is revoked.
- Refactoring WebRTC media behavior unrelated to authentication.
- Account recovery or synchronization across unrelated installations.
