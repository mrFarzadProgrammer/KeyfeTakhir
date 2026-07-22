#!/usr/bin/env bash
set -Eeuo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$ROOT_DIR"

IMAGE_TAG="latefeebox-stable:1.2.1"
COMPOSE_FILE="docker-compose.server.yml"
ENV_FILE=".env"
CONTAINER_NAME="late-fee-box"

fail() {
  echo "ERROR: $*" >&2
  exit 1
}

command -v docker >/dev/null 2>&1 || fail "Docker is not installed."
docker info >/dev/null 2>&1 || fail "Docker daemon is not running or current user cannot access it."
docker compose version >/dev/null 2>&1 || fail "Docker Compose plugin is not installed."
[[ -f "$ENV_FILE" ]] || fail ".env file is missing."

required_keys=(
  BALE_BOT_TOKEN
  BALE_PROVIDER_TOKEN
  BALE_BOT_USERNAME
  BALE_ADMIN_USER_ID
  BALE_GROUP_CHAT_ID
  ADMIN_PASSWORD
)

for key in "${required_keys[@]}"; do
  value="$(grep -E "^${key}=" "$ENV_FILE" | tail -n 1 | cut -d= -f2- || true)"
  [[ -n "$value" ]] || fail "$key is missing or empty in .env."
done

chmod 600 "$ENV_FILE"

echo "1/5 - Pulling official .NET base images..."
docker pull mcr.microsoft.com/dotnet/sdk:10.0
docker pull mcr.microsoft.com/dotnet/aspnet:10.0

echo "2/5 - Building KeyfeTakhir image..."
docker build --network=none -t "$IMAGE_TAG" -f src/LateFeeBox.Web/Dockerfile .

echo "3/5 - Removing stale container name if needed..."
if docker container inspect "$CONTAINER_NAME" >/dev/null 2>&1; then
  docker rm -f "$CONTAINER_NAME" >/dev/null
fi

echo "4/5 - Starting application..."
docker compose -f "$COMPOSE_FILE" --env-file "$ENV_FILE" up -d --force-recreate

echo "5/5 - Waiting for health endpoint..."
for attempt in $(seq 1 40); do
  if curl -fsS http://127.0.0.1:8080/health >/dev/null 2>&1; then
    echo "KeyfeTakhir started successfully."
    echo "Local admin endpoint: http://127.0.0.1:8080/admin/"
    echo "Use an SSH tunnel or Nginx HTTPS to access it remotely."
    exit 0
  fi
  sleep 2
done

echo "Application did not become healthy. Recent logs:" >&2
docker compose -f "$COMPOSE_FILE" --env-file "$ENV_FILE" logs --tail 120 late-fee-box >&2 || true
exit 1
