# VPS deployment over SSH

GitHub Actions implements this pipeline:

```text
Build -> Test -> Publish GHCR image -> Deploy API to VPS over SSH
```

The automated deployment is intentionally API-only. It copies `deploy/docker-compose.prod.yml` and `deploy/deploy.sh`; it does not provision PostgreSQL, Redis, coturn, nginx, certificates or backups. Use the full stack under `infra/` separately, or provide equivalent external services.

## Workflow behavior

`.github/workflows/vps-ci-cd.yml` runs on pull requests, pushes to `main` and its legacy architecture branch, and manual dispatch.

- Build restores and compiles the configured API project at `services/SonicRelay.Api/SonicRelay.Api.csproj`, failing if that exact path is missing.
- Test discovers every `*Test.csproj`/`*Tests.csproj`, restores it and uploads TRX results.
- Non-PR runs build the canonical root `Dockerfile` and publish `ghcr.io/vitorhugo-java/sonicrelay-api:sha-<commit>`.
- `main` additionally publishes `:latest`.
- Pushes to `main`, and manual runs with `deploy=true`, deploy the immutable SHA image to the production environment.

## GitHub secrets

| Secret | Required | Default/example |
| --- | --- | --- |
| `VPS_HOST` | Yes | VPS hostname or IP |
| `VPS_USER` | Yes | `deploy` |
| `VPS_SSH_KEY` | Yes | Private key for the deployment user |
| `VPS_PORT` | No | `22` |
| `VPS_APP_DIR` | No | `/opt/sonicrelay` |

`GITHUB_TOKEN` publishes the GHCR package. The package must be public for an unauthenticated VPS pull because the deployment script does not perform `docker login`.

## VPS bootstrap

Install Docker Engine and the Docker Compose plugin, then create a restricted deployment user and directory:

```bash
sudo adduser --disabled-password --gecos "" deploy
sudo usermod -aG docker deploy
sudo mkdir -p /opt/sonicrelay
sudo chown -R deploy:deploy /opt/sonicrelay
sudo install -d -m 700 -o deploy -g deploy /home/deploy/.ssh
```

Place the matching public key in `/home/deploy/.ssh/authorized_keys` with mode `600`. Re-login after adding the user to the Docker group.

## Runtime configuration

Create `/opt/sonicrelay/.env` before the first deployment. `deploy.sh` refuses to start without it.

```env
ASPNETCORE_ENVIRONMENT=Production
ASPNETCORE_URLS=http://+:8080
API_BIND=127.0.0.1:8080

ConnectionStrings__Postgres=Host=postgres.example.internal;Port=5432;Database=sonicrelay;Username=sonicrelay;Password=CHANGE_ME
Redis__ConnectionString=redis.example.internal:6379,password=CHANGE_ME,abortConnect=false

Auth__AccessTokenMinutes=15
Auth__RefreshTokenDays=30
Sessions__CodeTtlMinutes=10
Sessions__CodeHmacKey=CHANGE_ME_TO_A_HIGH_ENTROPY_SECRET
Sessions__MaxViewersPerSession=3
Sessions__CleanupEnabled=true
Sessions__CleanupIntervalSeconds=60
Sessions__DisconnectedParticipantRetentionHours=24
Swagger__Enabled=false
```

The compose service binds to loopback by default. Put nginx, Caddy or another TLS reverse proxy in front of `127.0.0.1:8080` and forward WebSocket upgrades for `/ws/signaling`.

## Database migration

The application does not call `Database.Migrate()` at startup. Apply migrations as a separate release step using the same PostgreSQL connection before starting a schema-dependent image:

```bash
dotnet ef database update \
  --project src/SonicRelay.Infrastructure/SonicRelay.Infrastructure.csproj \
  --startup-project services/SonicRelay.Api/SonicRelay.Api.csproj
```

The current GitHub Actions workflow does not run this command on the VPS.

## Deployment execution

GitHub Actions copies the two deployment files into the app directory and runs:

```bash
cd /opt/sonicrelay
chmod +x deploy.sh
IMAGE=ghcr.io/vitorhugo-java/sonicrelay-api:sha-<commit> ./deploy.sh
```

The script validates `.env`, Docker and Compose; pulls the API image; runs `docker compose up -d --remove-orphans`; prunes dangling images; and prints service status.

## Verification

On the VPS:

```bash
docker compose -f /opt/sonicrelay/docker-compose.prod.yml ps
docker logs --tail 100 sonicrelay-api
curl --fail http://127.0.0.1:8080/health/live
curl --fail http://127.0.0.1:8080/health/ready
```

`/health/live` proves the API process responds. `/health/ready` additionally proves PostgreSQL and Redis are reachable.

From outside the VPS, verify TLS and the reverse proxy:

```bash
curl --fail https://stream.example.com/health/ready
```

## Rollback

Redeploy a previous immutable image tag:

```bash
cd /opt/sonicrelay
IMAGE=ghcr.io/vitorhugo-java/sonicrelay-api:sha-<previous-commit> ./deploy.sh
```

Database rollback is separate. Do not downgrade an image across an incompatible schema change without a tested migration rollback or restored backup.

## Full infrastructure stack

The repository also contains a separate full-stack Compose topology:

```bash
cp infra/.env.prod.example infra/.env.prod
docker compose \
  --env-file infra/.env.prod \
  -f infra/compose.yml \
  -f infra/compose.prod.yml \
  --profile prod \
  up -d
```

It includes API, PostgreSQL, Redis, coturn and nginx. It is not copied or invoked by the GitHub Actions SSH deployment. Review `infra/nginx/default.conf`, `infra/coturn/turnserver.conf`, exposed ports, DNS and every example secret before production use.

TURN/STUN should use DNS-only records and native ports (`3478/udp`, `3478/tcp`, `5349/tcp`, and the configured UDP relay range), not a normal HTTP proxy.

### DNS: API/WSS vs TURN/STUN

The API/WSS hostname (e.g. `sonicrelay-api.hugodotnet.dev`) and the TURN/STUN hostname (e.g. `sonicrelay-turn.hugodotnet.dev`) are separate DNS records with different proxy settings:

- **API/WSS domain** — Cloudflare proxied (orange cloud). HTTP/WSS traffic goes through Cloudflare's edge as usual.
- **TURN/STUN domain** — DNS-only (grey cloud). Cloudflare cannot proxy raw UDP/TCP TURN traffic; a proxied record would break NAT traversal entirely. Point it directly at the coturn host's IP.

Required inbound ports on the TURN/STUN host:

| Port | Protocol | Purpose |
| --- | --- | --- |
| 3478 | UDP | STUN / TURN |
| 3478 | TCP | TURN (TCP fallback) |
| 5349 | TCP | TURN over TLS (`turns:`) |
| 49160-49200 | UDP | TURN relay media range |

Set `WEBRTC_TURN_HOST`, `WEBRTC_TURN_REALM` and `WEBRTC_TURN_SECRET` in `.env` to match the coturn deployment (see `deploy/docker-compose.prodcoturn.yml`); the API derives the STUN/TURN URLs it hands to clients from `WEBRTC_TURN_HOST` and never returns `WEBRTC_TURN_SECRET` itself. Keep `WEBRTC_ENABLE_GOOGLE_STUN_FALLBACK=false` in production — Google's public STUN server is a development-only fallback for when no TURN host is configured, not a fallback the clients or API should depend on once coturn is live.

To debug NAT traversal, clients support a "force relay" mode that restricts ICE to relay candidates only (`iceTransportPolicy: "relay"`), which exercises the coturn relay path end-to-end at the cost of extra latency and VPS bandwidth. Leave it off (`"all"`) by default.
