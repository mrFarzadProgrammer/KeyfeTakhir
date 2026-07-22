#!/usr/bin/env bash
set -Eeuo pipefail
ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$ROOT_DIR"
docker compose -f docker-compose.server.yml --env-file .env ps
printf '\nHealth:\n'
curl -fsS http://127.0.0.1:8080/health && printf '\n'
printf '\nRecent logs:\n'
docker compose -f docker-compose.server.yml --env-file .env logs --tail 80 late-fee-box
