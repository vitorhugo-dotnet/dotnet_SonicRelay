# Device Identity Phase 3 Flutter Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace Flutter account login with secure viewer-device authentication and support manual or camera QR pairing before session join.

**Architecture:** Persist one atomic `DeviceCredential` in `flutter_secure_storage`; keep short-lived tokens in a single-flight `DeviceIdentitySession`. Riverpod supplies that session to Dio and signaling. Pairing remains separate from session joining.

**Tech Stack:** Flutter, Dart 3.10+, Riverpod 3.3.2, Dio 5.10.0, flutter_secure_storage 10.3.1, mobile_scanner 7.4.0.

## Global Constraints

- Use branch `codex/issue-26-phase3` in `flutter_SonicRelay`.
- Add only `mobile_scanner: ^7.4.0`; do not update unrelated packages.
- Bootstrap `flutter_viewer` with runtime `android` or `ios`.
- Never send `deviceId` in join bodies or signaling queries.
- Never use `/auth/login` or `/auth/refresh` as fallback.
- Pairing QR JSON contains only `challengeId` and `code`.
- Request camera permission only when the scanner opens; keep manual entry.
- Never persist/log QR payloads, codes, credentials, tokens, SDP, or complete ICE candidates.

---

### Task 1: Atomic secure device credential

**Files:**
- Create: `lib/features/device_identity/domain/device_credential.dart`
- Create: `lib/features/device_identity/data/device_credential_storage.dart`
- Create: `test/features/device_identity/data/device_credential_storage_test.dart`

**Interfaces:**
- Produces: `DeviceCredential(deviceId, credentialSecret, credentialVersion, deviceType, platform)`.
- Produces: `DeviceCredentialStorage.read`, `write`, `clear`.

- [ ] **Step 1: Write RED storage tests**

```dart
test('round trips one atomic credential and clears it', () async {
  const value = DeviceCredential(
    deviceId: 'device-1', credentialSecret: 'secret', credentialVersion: 1,
    deviceType: 'flutter_viewer', platform: 'android');
  await storage.write(value);
  expect(await storage.read(), value);
  await storage.clear();
  expect(await storage.read(), isNull);
});
```

Corrupt the stored JSON and expect a typed failure whose message omits the
secret.

- [ ] **Step 2: Run and verify RED**

```powershell
flutter test test/features/device_identity/data/device_credential_storage_test.dart
```

- [ ] **Step 3: Implement one validated JSON record**

Use key `deviceIdentity.credential` so partial multi-key writes cannot mix
credentials. Validate ID, secret, positive version, type, and `android|ios`.

- [ ] **Step 4: Run GREEN and commit**

```powershell
flutter test test/features/device_identity/data/device_credential_storage_test.dart
git add lib/features/device_identity test/features/device_identity
git commit -m "feat(flutter): persist device credential securely"
```

### Task 2: Bootstrap and single-flight token session

**Files:**
- Create: `lib/features/device_identity/domain/device_access_token.dart`
- Create: `lib/features/device_identity/data/device_identity_api.dart`
- Create: `lib/features/device_identity/data/device_identity_session.dart`
- Create: `lib/features/device_identity/data/dto/bootstrap_device_request.dart`
- Create: `lib/features/device_identity/data/dto/bootstrap_device_response.dart`
- Create: `lib/features/device_identity/data/dto/device_token_request.dart`
- Create: `lib/features/device_identity/data/dto/device_token_response.dart`
- Create: `test/features/device_identity/data/device_identity_api_test.dart`
- Create: `test/features/device_identity/data/device_identity_session_test.dart`

**Interfaces:**
- Produces: `DeviceIdentityApi.bootstrap` and `token`.
- Produces: `DeviceIdentitySession.accessToken({bool forceRefresh = false})`, `reset()`.

- [ ] **Step 1: Write RED lifecycle/concurrency tests**

Cover absent bootstrap, stored exchange, 30-second expiry margin, five
concurrent callers/one request, forced exchange, `401` clear, and network
failure retention.

```dart
final values = await Future.wait(List.generate(5, (_) => session.accessToken()));
expect(values.toSet(), {'token-1'});
expect(api.tokenCalls, 1);
```

- [ ] **Step 2: Run and verify RED**

```powershell
flutter test test/features/device_identity/data/device_identity_session_test.dart
```

- [ ] **Step 3: Implement serialized bootstrap/exchange**

Share an in-flight `Future<String>?` and clear it with `whenComplete`. Cache
token/absolute expiry in memory. Token `401` clears storage; Dio network errors
do not. Never bootstrap on a transient failure.

- [ ] **Step 4: Run GREEN and commit**

```powershell
flutter test test/features/device_identity/data/device_identity_session_test.dart
git add lib/features/device_identity test/features/device_identity
git commit -m "feat(flutter): bootstrap viewer device identity"
```

### Task 3: DeviceBearer transport and Phase 2 wire contracts

**Files:**
- Modify: `lib/core/http/auth_interceptor.dart`
- Modify: `lib/app/di/app_providers.dart`
- Modify: `lib/features/sessions/data/dto/join_session_request.dart`
- Modify: `lib/features/sessions/data/sessions_repository.dart`
- Modify: `lib/features/signaling/data/signaling_client.dart`
- Modify: `lib/features/signaling/presentation/signaling_status_view_model.dart`
- Modify: `lib/features/listener/presentation/listener_view_model.dart`
- Modify: `lib/features/sessions/presentation/session_waiting_page.dart`
- Modify: `test/core/http/auth_interceptor_test.dart`
- Modify: `test/features/sessions/data/sessions_repository_test.dart`
- Modify: `test/features/signaling/data/signaling_client_test.dart`
- Modify: `test/features/listener/presentation/listener_view_model_test.dart`
- Create: `test/features/sessions/presentation/session_waiting_page_test.dart`

**Interfaces:**
- Consumes: `DeviceIdentitySession.accessToken`.
- Produces: `JoinSessionRequest(String code)`.
- Produces: `SignalingClient.connect({required StreamSession session})`.

- [ ] **Step 1: Write RED wire tests**

```dart
expect(JoinSessionRequest(code: 'ABC123').toJson(), {'code': 'ABC123'});
expect(socket.uri.queryParameters, {'sessionId': 'session-1'});
```

Prove reconnect receives `token-2` after initial `token-1`, and join never
reads `DevicesRepository.readCurrentDeviceId`.

- [ ] **Step 2: Run and verify RED**

```powershell
flutter test test/core/http/auth_interceptor_test.dart test/features/sessions/data/sessions_repository_test.dart test/features/signaling/data/signaling_client_test.dart
```

- [ ] **Step 3: Replace Identity renewal and supplied device identity**

Interceptor calls `accessToken()`. On one `401`, force exchange and replay only
GET/HEAD or requests marked `replaySafe`. Remove `/auth/refresh`, refresh-token
and device-ID parameters from active paths. Keep local `from` metadata only if
the signaling envelope needs it.

- [ ] **Step 4: Run GREEN and commit**

```powershell
flutter test test/core/http/auth_interceptor_test.dart test/features/sessions/data/sessions_repository_test.dart test/features/signaling/data/signaling_client_test.dart test/core/webrtc/ice_servers_repository_test.dart
git add lib test
git commit -m "feat(flutter): use device bearer for streaming"
```

### Task 4: Pairing API, strict QR parser, and scanner

**Files:**
- Modify: `pubspec.yaml`
- Modify: `pubspec.lock`
- Create: `lib/features/pairing/domain/pairing_challenge_payload.dart`
- Create: `lib/features/pairing/domain/device_pairing.dart`
- Create: `lib/features/pairing/data/pairing_api.dart`
- Create: `lib/features/pairing/data/pairing_repository.dart`
- Create: `lib/features/pairing/presentation/pairing_page.dart`
- Create: `lib/features/pairing/presentation/pairing_view_model.dart`
- Create: `lib/features/pairing/presentation/qr_scanner_page.dart`
- Modify: `android/app/src/main/AndroidManifest.xml`
- Modify: `ios/Runner/Info.plist`
- Create: `test/features/pairing/domain/pairing_challenge_payload_test.dart`
- Create: `test/features/pairing/data/pairing_repository_test.dart`
- Create: `test/features/pairing/presentation/pairing_view_model_test.dart`
- Create: `test/features/pairing/presentation/pairing_page_test.dart`
- Create: `test/features/pairing/presentation/qr_scanner_page_test.dart`

**Interfaces:**
- Produces: `PairingChallengePayload.parse(String raw)`.
- Produces: `PairingRepository.complete`, `list`, `revoke`.
- Produces: pairing state with no session join code.

- [ ] **Step 1: Write RED strict parser tests**

Accept only:

```json
{"challengeId":"00000000-0000-0000-0000-000000000001","code":"ABC12345"}
```

Reject invalid JSON, missing/extra fields, invalid GUID, blank code, and any
payload containing `credentialSecret`, `accessToken`, or `deviceId`.

- [ ] **Step 2: Write RED repository/widget tests**

Assert `POST /api/pairings/complete`, generic invalid/expired copy,
list/revoke paths, one submit after scan, camera-denied manual fallback, and
scanner-controller disposal.

- [ ] **Step 3: Run and verify RED**

```powershell
flutter test test/features/pairing
```

- [ ] **Step 4: Add scanner and platform declarations**

Add `mobile_scanner: ^7.4.0`, Android `android.permission.CAMERA`, and iOS
`NSCameraUsageDescription` explaining device-pairing QR use. Do not request
permission at startup.

- [ ] **Step 5: Implement manual/scan complete, list, and revoke**

Use `formats: const [BarcodeFormat.qrCode]`. Stop after the first accepted
payload before API/navigation to prevent duplicate frame submissions. Never
persist decoded payload.

- [ ] **Step 6: Run GREEN and commit**

```powershell
flutter pub get
flutter test test/features/pairing
git add pubspec.yaml pubspec.lock android/app/src/main/AndroidManifest.xml ios/Runner/Info.plist lib/features/pairing test/features/pairing
git commit -m "feat(flutter): pair devices by QR code"
```

### Task 5: Login-free routing and composition

**Files:**
- Modify: `lib/app/di/app_providers.dart`
- Modify: `lib/app/router/app_router.dart`
- Modify: `lib/app/sonic_relay_app.dart`
- Modify: `lib/features/settings/presentation/settings_page.dart`
- Modify: `lib/features/sessions/presentation/join_session_view_model.dart`
- Modify: `test/app/app_router_test.dart`
- Modify: `test/app/sonic_relay_app_test.dart`
- Create: `test/app/di/device_identity_provider_test.dart`
- Modify: `test/features/sessions/presentation/join_session_view_model_test.dart`
- Modify: `README.md`

**Interfaces:**
- Consumes: Tasks 1-4.
- Produces: `/device-setup`, `/pair`, `/join`, `/session/waiting`, `/listener`, `/settings`; no production `/login`.

- [ ] **Step 1: Write RED router/composition tests**

Restoring routes to `/loading`; invalid/missing credential to `/device-setup`;
ready-but-unpaired to `/pair`; paired to `/join`. Assert `/login` absent and
startup never constructs `AuthRepository`.

- [ ] **Step 2: Replace providers and redirect state**

Expose credential storage, raw Device Identity Dio, shared session,
authenticated Dio, pairing repository, and readiness notifier. Remove active
Identity and old owner-scoped device-registration providers.

- [ ] **Step 3: Document and verify**

Document secure storage, reset consequences, QR/manual pairing, separate
session code, camera permission, and no account login.

```powershell
flutter test test/app test/features/device_identity test/features/pairing test/features/sessions test/features/signaling
flutter analyze
git diff --check
git add lib test README.md
git commit -m "feat(flutter): replace login with device setup"
```
