# Fake signaling client

This development tool simulates one publisher and one viewer against a running SonicRelay API. It creates real test users, devices, and a session through the public HTTP API, then opens two authenticated WebSockets and verifies routed signaling messages.

It does **not** capture, transmit, or play audio. The SDP and ICE payloads are strings named `fake-test-*`; no peer connection or real WebRTC implementation is present.

## Start the dependencies and API

The simplest option starts PostgreSQL, Redis, coturn, and the API with Docker Compose:

```bash
cp infra/.env.example infra/.env
docker compose \
  --env-file infra/.env \
  -f infra/compose.yml \
  -f infra/compose.dev.yml \
  --profile dev \
  up --build
```

Wait until `http://localhost:8080/health/ready` reports healthy. To run the API from the .NET SDK instead, start PostgreSQL and Redis with settings compatible with `services/SonicRelay.Api`, apply the EF Core migration, then run:

```bash
dotnet ef database update \
  --project src/SonicRelay.Infrastructure/SonicRelay.Infrastructure.csproj \
  --startup-project services/SonicRelay.Api/SonicRelay.Api.csproj
dotnet run --project services/SonicRelay.Api/SonicRelay.Api.csproj
```

## Run the client

From the repository root:

```bash
dotnet run --project tools/SonicRelay.SignalingClient -- \
  --base-url http://localhost:8080
```

`--base-url` is optional and defaults to `http://localhost:8080`.

Representative output:

```text
[ok] authenticated fake publisher and viewer
[ok] registered fake publisher and viewer devices
[ok] created and joined session <session-id> with test code <code>
[ok] opened two authenticated signaling WebSockets
[ok] routed publisher.ready
[ok] routed viewer.ready
[ok] routed webrtc.offer with fake test SDP
[ok] routed webrtc.answer with fake test SDP
[ok] routed webrtc.ice_candidate with fake test ICE data
[ok] fake signaling flow completed; no audio or real WebRTC was used
```

The process exits non-zero when an HTTP operation, WebSocket connection, routed envelope, or fake payload validation fails. Accounts and records created by a run remain test data in the configured development database.
