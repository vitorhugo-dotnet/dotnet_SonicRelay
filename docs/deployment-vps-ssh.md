# VPS CI/CD over SSH

This repository deploys SonicRelay through GitHub Actions using this flow:

```text
Build -> Test -> Publish GHCR image -> Deploy to VPS over SSH
```

Each stage is a separate GitHub Actions job, so failures can be retried independently.

## GitHub secrets

Create these repository secrets:

| Secret | Required | Example |
| --- | --- | --- |
| `VPS_HOST` | yes | `203.0.113.10` |
| `VPS_USER` | yes | `deploy` |
| `VPS_SSH_KEY` | yes | private key for the deploy user |
| `VPS_PORT` | no | `22` |
| `VPS_APP_DIR` | no | `/opt/sonicrelay` |

The workflow uses `GITHUB_TOKEN` to publish to GHCR.

## VPS bootstrap

Run once on the VPS:

```bash
sudo adduser --disabled-password --gecos "" deploy
sudo usermod -aG docker deploy
sudo mkdir -p /opt/sonicrelay
sudo chown -R deploy:deploy /opt/sonicrelay
```

Add your public SSH key to:

```bash
/home/deploy/.ssh/authorized_keys
```

The VPS needs Docker Engine with the Docker Compose plugin installed.

## Runtime `.env`

Create `/opt/sonicrelay/.env` on the VPS:

```env
ASPNETCORE_ENVIRONMENT=Production
API_BIND=127.0.0.1:8080
```

Keep production secrets only on the VPS. Do not commit them.

## GHCR package visibility

The workflow publishes:

```text
ghcr.io/vitorhugo-java/sonicrelay-api:sha-<commit>
ghcr.io/vitorhugo-java/sonicrelay-api:latest
```

`latest` is only published from `main`.

If GitHub creates the container package as private after the first publish, open the package settings and change visibility to public.

## Notes

- The workflow auto-detects the first non-test `.csproj` if `APP_PROJECT` does not exist.
- Default project path is `src/SonicRelay.Api/SonicRelay.Api.csproj`.
- The Dockerfile expects the published DLL to be `SonicRelay.Api.dll`. Adjust the `ENTRYPOINT` if the API project assembly name changes.
- The first compose file deploys only the API. Add PostgreSQL, Redis and coturn services when the backend implementation lands.
