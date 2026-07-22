#!/usr/bin/env bash
set -Eeuo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$ROOT_DIR"

CONTAINER_NAME="late-fee-box"
BACKUP_DIR="$ROOT_DIR/backups"
STAMP="$(date +%Y%m%d-%H%M%S)"
TEMP_DIR="$BACKUP_DIR/keyfetakhir-$STAMP"
ARCHIVE="$BACKUP_DIR/keyfetakhir-$STAMP.tar.gz"

mkdir -p "$TEMP_DIR"
docker container inspect "$CONTAINER_NAME" >/dev/null 2>&1 || {
  echo "Container $CONTAINER_NAME does not exist." >&2
  exit 1
}

docker cp "$CONTAINER_NAME:/app/data/." "$TEMP_DIR/"
tar -C "$BACKUP_DIR" -czf "$ARCHIVE" "keyfetakhir-$STAMP"
rm -rf "$TEMP_DIR"
chmod 600 "$ARCHIVE"
echo "Backup created: $ARCHIVE"
