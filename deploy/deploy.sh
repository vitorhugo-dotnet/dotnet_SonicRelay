#!/usr/bin/env bash
set -euo pipefail

APP_DIR="${APP_DIR:-/opt/sonicrelay}"
COMPOSE_FILE="${COMPOSE_FILE:-docker-compose.prod.yml}"
IMAGE="${IMAGE:?IMAGE is required}"

cd "$APP_DIR"

if [[ ! -f .env ]]; then
  echo "Missing $APP_DIR/.env"
  echo "Create it before the first deploy."
  exit 1
fi

command -v docker >/dev/null 2>&1 || {
  echo "Docker is not installed on the VPS."
  exit 1
}

docker compose version >/dev/null 2>&1 || {
  echo "Docker Compose plugin is not available on the VPS."
  exit 1
}

export IMAGE

echo "Pulling image: $IMAGE"
docker compose -f "$COMPOSE_FILE" pull api

echo "Starting SonicRelay stack"
docker compose -f "$COMPOSE_FILE" up -d --remove-orphans

echo "Pruning dangling images"
docker image prune -f >/dev/null

echo "Deployment finished"
docker compose -f "$COMPOSE_FILE" ps
