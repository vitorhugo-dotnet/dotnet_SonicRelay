# Device Endpoints Design

## Scope

Implement authenticated device management for SonicRelay:

- `POST /api/devices`
- `GET /api/devices`
- `GET /api/devices/{deviceId}`
- `PATCH /api/devices/{deviceId}`
- `DELETE /api/devices/{deviceId}`
- `POST /api/devices/{deviceId}/revoke`

The implementation must use API DTOs, isolate each user's devices, validate the documented type and platform values, and prevent revoked devices from later session or signaling use.

## API contract

Create accepts `name`, `type`, `platform`, and optional `publicKey`. Valid device types are `windows_publisher` and `flutter_viewer`; valid platforms are `windows`, `android`, and `ios`. Names must be non-empty and fit the existing 120-character database limit.

Patch accepts optional `name` and `publicKey`. It does not allow changing ownership, type, platform, trust, or revocation state. An empty patch or invalid supplied name returns `400 Bad Request`.

Responses use a device DTO containing `id`, `name`, `type`, `platform`, `publicKey`, `trusted`, `revoked`, `lastSeenAt`, and `createdAt`. Domain entities are never serialized directly.

Create returns `201 Created` with a location header. Reads and revoke return `200 OK`; delete returns `204 No Content`. A missing device and a device owned by another user both return `404 Not Found`, preventing ownership disclosure. Invalid input returns `400 Bad Request`. Every route requires bearer authentication and returns `401 Unauthorized` to anonymous callers.

Delete physically removes the owned device. Revoke is a retained, idempotent state transition setting `revoked` to true. Revoked devices remain visible to their owner through device-management reads.

## Architecture and data flow

Device endpoint handlers obtain the authenticated Identity user ID from the request principal, query `AppDbContext.Devices` with both device ID and owner ID, map entities to response DTOs, and persist changes asynchronously.

A focused device-access component centralizes eligibility checks used outside device management. It validates that a device exists, belongs to the authenticated user, is not revoked, and is compatible with the requested role. Session creation requires an active `windows_publisher`; session join requires an active `flutter_viewer`; signaling requires an active owned device before accepting the WebSocket. Checks are explicit rather than implemented as a global EF query filter so revoked records remain manageable and security-sensitive call sites remain visible.

The current session and signaling placeholders will receive only the minimum changes needed to reject missing, foreign, or revoked device IDs according to their documented request/query contracts. Full session persistence and signaling routing remain outside this change.

## Validation

Validation occurs at the API boundary against allowlists backed by the domain constants. The accepted combinations are constrained by role semantics:

- `windows_publisher` uses platform `windows`.
- `flutter_viewer` uses platform `android` or `ios`.

Malformed JSON and invalid DTO values produce a `400` response. No dependency changes or unrelated refactors are included.

## Testing

Focused integration tests use the existing authenticated Identity flow and in-memory EF database. Tests first establish failures, then drive implementation for:

- authentication on all six routes;
- create/list/get/update/delete behavior using DTO-shaped JSON;
- type, platform, type/platform combination, and name validation;
- ownership isolation for list, get, patch, delete, and revoke;
- idempotent revocation;
- rejection of revoked devices by session creation/join and WebSocket signaling eligibility paths.

Only the device integration-test class and the smallest directly related tests will run. The repository-wide test suite is out of scope unless explicitly requested.

## Non-goals

- Full StreamSession CRUD, session-code generation, or signaling routing.
- Trusted-device approval workflows.
- Database migration changes; the existing `Revoked` column is already modeled.
- Dependency upgrades or unrelated architectural refactoring.
