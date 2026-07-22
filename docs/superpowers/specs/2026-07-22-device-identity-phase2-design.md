# Device Identity Auth (Phase 2) Design

## Scope

Implements Phase 2 of issue #26: migrate session ownership, WebSocket signaling,
and TURN credential issuance from ASP.NET Core Identity (`ApplicationUser`) and
the old owner-scoped `Device` entity to the Phase 1 `DeviceIdentity` model
(`docs/superpowers/specs/2026-07-21-device-identity-phase1-design.md`, PR #28).

Per the issue's explicit preference to change the contract directly during the
MVP rather than maintain two parallel session-ownership architectures, this
phase makes `DeviceBearer` the **only** way to create/join a session, connect to
signaling, or obtain TURN credentials. Identity-based session ownership is
removed, not kept in parallel. This intentionally breaks the current Windows
and Flutter clients until Phase 3 rewires them — acceptable pre-launch, with a
single developer operating both clients.

This PR does **not** touch: the Identity login/register/refresh endpoints
themselves, the old owner-scoped `Device` entity or its CRUD endpoints
(`DeviceEndpoints.cs`/`DeviceAccess.cs` — now orphaned from session logic but
left in place), or the Windows/Flutter clients. Those are Phase 3/4.

## Domain model changes

`src/SonicRelay.Domain/Sessions/StreamSession.cs`:
- Remove `OwnerUserId`.
- `SourceDeviceId` now references `DeviceIdentity.Id` instead of the old
  `Device.Id` (no behavior change to the column itself, only what it points at
  — no EF FK exists today for either, and none is added now, matching the
  existing bare-Guid-plus-app-check convention used throughout this table).

`src/SonicRelay.Domain/Sessions/SessionParticipant.cs`:
- Remove `UserId`.
- `DeviceId` now references `DeviceIdentity.Id` instead of the old `Device.Id`.

One EF Core migration drops the `OwnerUserId`/`UserId` columns and their
indexes, and adds replacement indexes keyed on `SourceDeviceId`/`DeviceId`
where the old indexes included the removed columns. This is a breaking,
non-reversible-with-data migration (existing session rows lose their owner
association) — acceptable per the issue's stated MVP breaking-change
preference; there is no requirement to preserve historical session data across
this migration.

The old `Device` entity, `DeviceEndpoints.cs`, and `DeviceAccess.cs` are not
modified or removed. They become dead code from the session/signaling/TURN
path's perspective after this phase but stay in place; their removal is
Phase 4 cleanup work, tracked by the issue itself.

## Scopes

Added to `DeviceCredentialService.ScopesFor`:

- `windows_publisher` gains: `session:create`, `session:end`,
  `signaling:connect`, `turn:credentials`.
- `flutter_viewer` gains: `session:join`, `signaling:connect`,
  `turn:credentials`.

(Existing Phase 1 scopes — `device:read`, `device:manage`, `pairing:*` — are
unchanged.)

## Authorization policies (`Program.cs`)

New scoped policies follow the exact `DeviceScopeRequirement` +
`AddAuthenticationSchemes("DeviceBearer")` pattern Phase 1 established for
`pairing:*`: `session:create`, `session:join`, `session:end`,
`signaling:connect`, `turn:credentials`.

A separate `DeviceAuthenticated` policy is also added for routes that only
need "caller is some currently-active device," not a specific capability:
`GET /api/sessions/active`, `GET /api/sessions/{id}`, and
`POST /api/webrtc/stats`. This matters because the session/webrtc endpoint
groups today use bare `RequireAuthorization()`, which authenticates against
the app's *default* scheme (`Identity.Bearer`) — the same scheme-pinning gap
Phase 1 hit once already (see the Phase 1 spec's `MapInboundClaims`
finding). Every device-facing route in this phase must explicitly reference
a policy that pins `DeviceBearer`; none may rely on bare
`RequireAuthorization()`.

Naively defining `DeviceAuthenticated` as `RequireAuthenticatedUser()` alone
would reopen a gap: `DeviceScopeAuthorizationHandler`'s live
status/credential-version re-check (the mechanism that makes revocation
take effect immediately without a token blocklist) only runs for policies
that carry a `DeviceScopeRequirement` — a bare `RequireAuthenticatedUser()`
policy would let an already-revoked device keep calling these three routes
until its token naturally expires. `DeviceScopeRequirement`'s `Scope`
becomes nullable (`DeviceScopeRequirement(string? scope = null)`), and
`DeviceScopeAuthorizationHandler` skips the scope-membership check only when
`Scope` is null, while still performing the live active-status/credential-version
check unconditionally. `DeviceAuthenticated` adds a scope-less
`DeviceScopeRequirement()`, so it enforces "currently-active device" with no
particular capability — reusing the same live-check code path as every
scoped policy instead of adding a second, divergent one.

`POST /api/sessions/{id}/end` and `POST /api/sessions/{id}/rotate-code` are
publisher-exclusive session-lifecycle mutations, so they require the
`session:end` policy rather than the generic `DeviceAuthenticated` one —
this is also what makes `session:end` (granted only to `windows_publisher`)
an actually-enforced scope rather than a name that appears on issued tokens
with no policy checking it.

`CanCreateSession`, `CanJoinSession`, `CanPublishSession`, `CanViewSession`
are removed — undifferentiated `RequireAuthenticatedUser()` stubs, with
`CanPublishSession`/`CanViewSession` never referenced by any route.
`CanRegisterDevice` and `AuthenticatedUser` are **not** removed: grep confirms
`CanRegisterDevice` still gates `POST /api/devices` in the untouched, old
owner-scoped `DeviceEndpoints.cs:15`, so removing it would silently break
that unrelated, out-of-scope feature — exactly the class of mistake Phase 1
caught once already with `MapDeviceEndpoints()`.

Registration of the `DeviceBearer` scheme and the new session/signaling/TURN
policies moves **outside** the `if (deviceIdentityEnabled)` block in
`Program.cs` (see Feature flag section below). The Phase 1 `device:*`/`pairing:*`
policies and their endpoint mappings remain inside it, unchanged.

## Session endpoints (`SessionEndpoints.cs`)

- `POST /api/sessions` — requires `session:create`. Resolves the caller via
  the existing `RequireDeviceAsync` helper (from
  `DeviceIdentityEndpoints.cs`, Phase 1) instead of
  `UserManager<ApplicationUser>.GetUserAsync`. `SourceDeviceId` is always set
  to the caller's own device ID — a device can only publish for itself, so the
  separate `DeviceAccess.CheckAsync` ownership/type check this endpoint does
  today is removed entirely (the `windows_publisher` scope requirement on
  `session:create` already establishes device type).
- `POST /api/sessions/join` — requires `session:join`; same pattern, viewer
  device.
- `GET /api/sessions/active`, `GET /api/sessions/{id}` — require the
  `DeviceAuthenticated` policy (see Authorization policies above), with the
  ownership/participant check rewritten from `OwnerUserId == user.Id` /
  `UserId == user.Id` to `SourceDeviceId == callerDeviceId` /
  `DeviceId == callerDeviceId`.
- `POST /api/sessions/{id}/end`, `POST /api/sessions/{id}/rotate-code` —
  require the `session:end` policy, with the ownership check rewritten from
  `OwnerUserId == user.Id` to `SourceDeviceId == callerDeviceId`.

## WebSocket signaling (`SignalingWebSocketEndpoint.cs`)

- The route requires the `signaling:connect` policy before the WebSocket
  upgrade (replacing the current `AuthenticatedUser` policy).
- Device ID is resolved from the JWT `sub` claim via `RequireDeviceAsync`, not
  from a `deviceId` query string parameter — removing a value that today is
  caller-supplied and only cross-checked, not authoritative. Only `sessionId`
  remains as a query parameter.
- Participant lookup changes from `(SessionId, UserId, DeviceId)` to
  `(SessionId, DeviceId)`.
- The current re-validation against the old `Device` table
  (`DeviceAccess.CheckAsync`) before upgrade is removed: `DeviceScopeAuthorizationHandler`
  (Phase 1) already re-reads `DeviceIdentity.Status`/`CredentialVersion` from
  the database on every request, including this one, so a mid-session
  revocation is still caught with no additional check needed.
- Connection registry identity (`ConnectionDescriptor`) drops the `user.Id`
  field, keyed on `participant.Id` + `deviceId` only.

## TURN credentials (`TurnCredentialService.cs`, `WebRtcEndpoints.cs`)

- `TurnCredentialService.Build` keys the coturn REST-API username on the
  device ID string instead of the ASP.NET Identity user ID string; HMAC
  computation is otherwise unchanged.
- `GET /api/webrtc/ice-servers` requires the `turn:credentials` policy.
- `POST /api/webrtc/stats` requires `DeviceAuthenticated`; its participant
  check changes from `UserId` to `DeviceId` matching, same as the session
  endpoints above.

## Feature flag semantics (narrowed from Phase 1)

`DeviceIdentity:Enabled` keeps gating exactly what Phase 1 documented: the
`/api/devices/bootstrap`, `/api/devices/token`, `/api/devices/rotate-credential`,
`/api/devices/revoke`, and `/api/pairings/*` HTTP surface, plus their
`device:*`/`pairing:*` authorization policies.

It does **not** extend to gate sessions, signaling, or TURN. Those now
unconditionally require `DeviceBearer`, since after this phase there is no
other authentication path for them — making them conditional on a flag would
mean disabling the flag also disables the product's entire core flow, which
is not this flag's purpose (it exists so device-management/pairing endpoints
can be pulled without a deploy, not to disable sessions). Concretely: the
`AddJwtBearer("DeviceBearer", ...)` registration and the new
`session:*`/`signaling:connect`/`turn:credentials` policies move outside the
`if (deviceIdentityEnabled)` block; `MapSessionEndpoints()`,
`MapWebRtcEndpoints()`, and `MapSignalingWebSocketEndpoint()` stay
unconditionally mapped as they are today.

`docs/device-identity.md` and `docs/security.md` are updated to state this
narrowed flag scope explicitly, so it doesn't read as still covering sessions.

## Testing

Rewritten (bootstrap-and-token a `DeviceIdentity` instead of registering an
`ApplicationUser` + old `Device`):

- `SessionEndpointsTests.cs`
- `SignalingWebSocketTests.cs`
- `WebRtcEndpointsTests.cs`
- `WebRtcObservabilityTests.cs`

A shared test helper (new file, e.g. `DeviceIdentityTestFixture.cs` or similar
under the test project) factors out the bootstrap-then-token flow that today
is duplicated ad hoc across the Phase 1 device-identity test files, since it
will now be needed by every session/signaling/TURN test too.

New coverage added per the issue's explicit test requirements not already
exercised by Phase 1:

- two independent publisher/viewer device pairs, each in their own session,
  do not observe or interfere with each other (session list, WebSocket
  broadcast, TURN credentials all scoped correctly);
- a device revoked mid-session is rejected on its next signaling/HTTP request
  (reusing the Phase 1 `DeviceScopeAuthorizationHandler` live-check, now
  exercised through the session/signaling path instead of only
  device-management endpoints);
- WebSocket upgrade rejects missing/expired/wrong-scope tokens and a
  `sessionId` the caller's device isn't a participant of.

Unchanged: `AuthEndpointsTests.cs`, `AccountDeletionTests.cs`,
`DeviceEndpointsTests.cs` (old `Device` CRUD), and all Phase 1
device-identity/pairing test files.

## Non-goals

- Removing the old `Device` entity, its CRUD endpoints, or any Identity
  endpoint (Phase 4).
- Windows/Flutter client changes (Phase 3).
- Data migration/preservation for existing session rows across the breaking
  schema change.
- Asymmetric (public-key) device credentials (unchanged from Phase 1's
  deferral).
- Reviewing or closing issue #20.
