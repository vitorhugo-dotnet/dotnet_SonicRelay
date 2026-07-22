# SonicRelay

Backend/control plane for low-latency audio streaming between a Windows publisher and mobile viewers. The API uses ASP.NET Core Minimal API, PostgreSQL, Redis and authenticated WebSocket signaling; WebRTC/Opus media stays between clients, directly or through coturn.

## Project suite

| Project | Repository | Stack | Responsibility |
| --- | --- | --- | --- |
| Backend API | [dotnet_SonicRelay](https://github.com/vitorhugo-java/dotnet_SonicRelay) | .NET 10, ASP.NET Core, PostgreSQL, Redis | Authentication, devices, sessions, join codes and signaling. |
| Mobile Viewer | [flutter_SonicRelay](https://github.com/vitorhugo-java/flutter_SonicRelay) | Flutter, `flutter_webrtc` | Join a session and play WebRTC audio. |
| Windows Publisher | [windows_SonicRelay](https://github.com/vitorhugo-java/windows_SonicRelay) | C#/.NET Desktop, WASAPI, WebRTC | Capture system audio and publish it to viewers. |

This repository contains only the backend and its infrastructure.

## Current status

| Area | Status | Current implementation |
| --- | --- | --- |
| Identity/authentication | Implemented | ASP.NET Core Identity with PostgreSQL and opaque bearer/refresh tokens. |
| Sessions | Implemented | Create, list, read, join, rotate code, end and background expiry/cleanup. |
| WebSocket signaling | Implemented | Authenticated participant validation and in-process, participant-targeted routing. |
| Devices | Implemented | Authenticated, owner-scoped create, list, read, update, delete and revocation endpoints. |
| Account deletion | Implemented | Self-service (`DELETE /api/account`) and admin (`DELETE /api/admin/users/{id}`) soft delete with device/session revocation, audit log and n8n webhook. See [account deletion](docs/account-deletion.md). |
| Observability | Implemented | Prometheus `/metrics`, client WebRTC stats ingestion (`POST /api/webrtc/stats`), structured signaling logs, Grafana dashboard and alerts. See [observability](docs/observability.md). |
| PostgreSQL | Implemented | Identity, device, session, participant and signaling-event schema plus initial migration. |
| Redis | Implemented | Expiring HMAC-derived session-code lookup. |
| WebRTC media | Client responsibility | No media capture, transcoding or relay is implemented in this API. |
| CI/CD | Implemented with scope noted | GitHub Actions builds/tests/publishes and deploys the API-only Compose stack over SSH. |

See the [client integration protocol](docs/protocol.md) for exact routes and WebRTC signaling flows, the [beginner guide](docs/beginner-guide.md) for a plain-language introduction, and [Security](docs/security.md) for implemented controls and known gaps.

## Quick start

Requirements: .NET 10 SDK, PostgreSQL and Redis.

```bash
dotnet restore SonicRelay.sln
dotnet ef database update \
  --project src/SonicRelay.Infrastructure/SonicRelay.Infrastructure.csproj \
  --startup-project services/SonicRelay.Api/SonicRelay.Api.csproj
dotnet run --project services/SonicRelay.Api/SonicRelay.Api.csproj
```

Health endpoints:

```bash
curl http://localhost:8080/health/live
curl http://localhost:8080/health/ready
```

Docker development stack:

The root `Dockerfile` is the canonical image definition. Run `docker build .` from the repository root; it publishes `services/SonicRelay.Api/SonicRelay.Api.csproj` using a multi-stage, non-root runtime image. Compose and CI/CD use the same Dockerfile and project path.

```bash
cp infra/.env.example infra/.env
docker compose \
  --env-file infra/.env \
  -f infra/compose.yml \
  -f infra/compose.dev.yml \
  --profile dev \
  up --build
```

Run the API integration/E2E tests:

```bash
dotnet test tests/SonicRelay.Api.IntegrationTests/SonicRelay.Api.IntegrationTests.csproj
```

Validate the real authentication, device, session, and WebSocket signaling flow without audio or WebRTC clients using the [fake signaling client](tools/SonicRelay.SignalingClient/README.md).

## Configuration

Set the following high-entropy secrets in production deployments (outside Git):

| Secret | Purpose |
| --- | --- |
| `Sessions:CodeHmacKey` | Server-side pepper for hashing session join codes. |
| `DeviceIdentity:CredentialHmacKey` | Server-side pepper for hashing device credential secrets. |
| `DeviceIdentity:PairingCodeHmacKey` | Server-side pepper for hashing pairing codes. |
| `DeviceIdentity:TokenSigningKey` | Symmetric signing key for `DeviceBearer` JWTs. |

See [device identity configuration](docs/device-identity.md#configuration) for details on the `DeviceIdentity:*` keys.

`DeviceIdentity:TokenSigningKey` (and the other `DeviceIdentity:*` keys above) are effectively required in any real deployment now: sessions, signaling and TURN credential issuance authenticate exclusively via `DeviceBearer` and have no fallback authentication path, so `DeviceIdentity:Enabled=false` no longer provides a way to run the product without configuring them — it only removes the bootstrap/token/rotate-credential/revoke/pairing HTTP surface.

## Documentation

- [Architecture](docs/architecture.md)
- [HTTP, WebSocket and WebRTC client integration protocol](docs/protocol.md)
- [Guia para leigos: WebSocket, WebRTC, Signaling, Opus e arquitetura](docs/beginner-guide.md)
- [Security](docs/security.md)
- [VPS deployment over SSH](docs/deployment-vps-ssh.md)
- [Architecture decision records](docs/adr/)

## CI/CD summary

`.github/workflows/vps-ci-cd.yml` runs build and tests on pull requests and pushes. Non-PR runs publish immutable `sha-<commit>` images to GHCR; `main` also publishes `latest`. A push to `main`, or a manual run with deployment enabled, copies `deploy/docker-compose.prod.yml` and `deploy/deploy.sh` to the VPS and starts the API image over SSH.

The automated deployment Compose file contains only the API. PostgreSQL, Redis, coturn and reverse proxy must already be reachable/configured, or operators must deploy the separate full stack from `infra/`. Details and required secrets are in the [deployment guide](docs/deployment-vps-ssh.md).

## License

See [LICENSE](LICENSE).
