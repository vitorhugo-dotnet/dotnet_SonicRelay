# ADR 0006: Remove ASP.NET Core Identity and owner-scoped Device CRUD

- Status: Accepted
- Date: 2026-08-01

## Context

Issue #26 (Phase 1-3) introduced device-identity authentication (ADR 0005) as a
parallel scheme alongside the original Identity opaque bearer tokens (ADR
0002), migrated session/signaling/TURN ownership to `DeviceIdentity` (Phase
2), and shipped device-identity pairing in both the Windows and Flutter
clients (Phase 3). Both clients now do real device-identity pairing and have
no login/register UI left; nothing in the product calls `/auth/register`,
`/auth/login`, `/auth/refresh`, `/auth/me`, `/api/account`,
`/api/admin/users/{id}` or the old owner-scoped `/api/devices` CRUD anymore.
Issue #26 explicitly prefers changing the contract directly over introducing
a `/v2`, since the project is still pre-MVP, and explicitly puts a human-user
admin panel out of scope for this phase.

## Decision

Remove ASP.NET Core Identity and everything that only existed to support it,
directly and without a versioned transition period:

- `MapIdentityApi<ApplicationUser>()` and the custom `/auth/logout`,
  `/auth/me` endpoints (`AuthEndpoints.cs`).
- Self-service and admin account deletion (`AccountEndpoints.cs`,
  `AdminEndpoints.cs`, `AccountDeletionService`, the account-deletion
  webhook notifier, and `docs/account-deletion.md`).
- The old owner-scoped `Device` entity and its `/api/devices` CRUD
  (`DeviceEndpoints.cs`, `Device.cs`, `DeviceAccess.cs`) — distinct from, and
  unrelated to, the `DeviceIdentity` entity and endpoints from Phase 1/2,
  which are unaffected.
- `ApplicationUser`, `IdentitySeeder`, the `AdminOnly`/`AuthenticatedUser`/
  `CanRegisterDevice` authorization policies, the `login`/`refresh` rate-limit
  policies, `Auth:AccessTokenMinutes`/`Auth:RefreshTokenDays`
  `BearerTokenOptions` configuration, and the
  `Microsoft.AspNetCore.Identity.EntityFrameworkCore` package reference.
- The `DeviceIdentity:Enabled` feature flag in `Program.cs`: the
  device-identity/pairing endpoints and their `device:*`/`pairing:*`
  authorization policies are now mapped unconditionally, matching how
  sessions/signaling/TURN have required `DeviceBearer` unconditionally since
  Phase 2. There is no longer any code path that runs with Identity but
  without device identity, so the flag has nothing left to gate.
- The `application_users`, `identity_roles`, `identity_user_claims`,
  `identity_user_roles`, `identity_user_logins`, `identity_role_claims`,
  `identity_user_tokens` and `devices` (old owner-scoped) tables, via a
  destructive EF Core migration
  (`RemoveLegacyIdentityAndOwnerScopedDevices`) that drops them outright.

No replacement admin UI/API for human user management is introduced — issue
#26 explicitly scopes that out. Device-level revocation remains fully
available through the Phase 1/2 device-identity endpoints
(`POST /api/devices/rotate-credential`, `POST /api/devices/revoke`).

### Migration strategy for existing dev-environment data

This is a pre-MVP dev project, and issue #26 explicitly authorizes changing
the contract directly rather than maintaining a compatibility path. Existing
dev-environment accounts and their owner-scoped devices are dropped outright
by the migration; no data-preserving migration path is provided, since there
is no production data and no client left that could read Identity-issued
tokens or call the removed endpoints.

## Consequences

The API now has exactly one authentication scheme (`DeviceBearer`) instead of
two coexisting ones. There is no human user account, login, password, email
or admin-role concept anywhere in the backend; all authorization is
device-scoped. Operators who previously seeded an admin via `Admin:Email`/
`Admin:Password` no longer need to (and that configuration is no longer
read). Deployments that still have the old tables/rows must run the new
migration, which is destructive: any real `ApplicationUser`/owner-scoped
`Device` data must be exported first if it is ever needed again — none is
expected to exist outside development environments at this stage. ADR 0002
is superseded by this decision; ADR 0005 is unaffected except that the
`DeviceIdentity:Enabled` gate it described no longer exists.
