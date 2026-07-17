#!/usr/bin/env bash
set -euo pipefail

readonly TRADING_CONTAINER="${TRADING_CONTAINER:-b3-trading-host}"
readonly TRADING_VOLUME="${TRADING_VOLUME:-b3-trading-data}"
readonly BACKUP_DIR="${BACKUP_DIR:-backup-artifacts}"
readonly RESTORE_PORT="${RESTORE_PORT:-18081}"
readonly ORIGINAL_BASE_URL="${TRADING_BASE_URL:-http://127.0.0.1:5000}"
readonly timestamp="$(date -u +%Y%m%dT%H%M%SZ)"
readonly archive_name="trading-data-${timestamp}.tar.gz"
readonly manifest_name="trading-data-${timestamp}.sha256"
readonly restore_volume="b3-trading-restore-${timestamp,,}"
readonly restore_container="b3-trading-restore-drill"

original_stopped=0

log() { printf '%s [backup-drill] %s\n' "$(date -u +%Y-%m-%dT%H:%M:%SZ)" "$*" >&2; }

cleanup() {
  docker rm -f "$restore_container" >/dev/null 2>&1 || true
  docker volume rm "$restore_volume" >/dev/null 2>&1 || true
  if (( original_stopped == 1 )); then
    log "restarting original trading-host"
    docker start "$TRADING_CONTAINER" >/dev/null || true
  fi
}
trap cleanup EXIT

for command in docker curl python3; do
  command -v "$command" >/dev/null 2>&1 || {
    log "ERROR: required command not found: $command"
    exit 2
  }
done

if [[ "$(docker inspect -f '{{.State.Running}}' "$TRADING_CONTAINER" 2>/dev/null || true)" != "true" ]]; then
  log "ERROR: $TRADING_CONTAINER must be running"
  exit 2
fi
docker volume inspect "$TRADING_VOLUME" >/dev/null
mkdir -p "$BACKUP_DIR"

image="$(docker inspect -f '{{.Config.Image}}' "$TRADING_CONTAINER")"
if [[ -z "$image" ]]; then
  log "ERROR: could not resolve the trading-host image"
  exit 1
fi

log "gracefully stopping $TRADING_CONTAINER to quiesce ingress, flush WAL, and force the final snapshot"
docker stop --time 45 "$TRADING_CONTAINER" >/dev/null
original_stopped=1

exit_code="$(docker inspect -f '{{.State.ExitCode}}' "$TRADING_CONTAINER")"
if [[ "$exit_code" != "0" && "$exit_code" != "143" ]]; then
  log "ERROR: trading-host did not stop cleanly (exit=$exit_code); refusing backup"
  exit 1
fi

docker run --rm -v "$TRADING_VOLUME:/data:ro" python:3.13-alpine python3 -c '
import json
from pathlib import Path
roots = list(Path("/data").glob("*/snapshots/latest.txt"))
if len(roots) != 1:
    raise SystemExit(f"expected exactly one snapshot pointer, found {len(roots)}")
pointer = roots[0]
seq = int(pointer.read_text().strip())
snapshot = pointer.parent / f"snap-{seq:012d}.json"
if not snapshot.is_file():
    raise SystemExit(f"snapshot pointer references missing file: {snapshot}")
payload = json.loads(snapshot.read_text())
payload_seq = payload.get("seq")
if payload_seq != seq:
    raise SystemExit(f"snapshot seq mismatch: pointer={seq}, payload={payload_seq}")
wal = pointer.parents[1] / "wal"
if not any(wal.rglob("*.log")):
    raise SystemExit("no WAL segment found; refusing an empty recovery drill")
print(f"validated quiesced snapshot seq={seq} with WAL present")
'

log "archiving quiesced volume $TRADING_VOLUME"
docker run --rm \
  -v "$TRADING_VOLUME:/data:ro" \
  -v "$PWD/$BACKUP_DIR:/backup" \
  alpine:3.21 sh -euc "
    cd /data
    find . -type f -print0 | sort -z | xargs -0 sha256sum > '/backup/$manifest_name'
    tar -czf '/backup/$archive_name' .
  "
test -s "$BACKUP_DIR/$archive_name"
test -s "$BACKUP_DIR/$manifest_name"

log "restoring archive into isolated volume $restore_volume"
docker volume create "$restore_volume" >/dev/null
docker run --rm \
  -v "$restore_volume:/restore" \
  -v "$PWD/$BACKUP_DIR:/backup:ro" \
  alpine:3.21 sh -euc "cd /restore && tar -xzf '/backup/$archive_name'"
docker run --rm \
  -v "$restore_volume:/data:ro" \
  -v "$PWD/$BACKUP_DIR:/backup:ro" \
  alpine:3.21 sh -euc "cd /data && sha256sum -c '/backup/$manifest_name'"

log "booting the restored data set with exchange ingress disabled"
docker run -d --name "$restore_container" -p "${RESTORE_PORT}:5000" \
  -v "$restore_volume:/var/lib/b3trading" \
  -e Trading__Auth__SigningKey="${TRADING_AUTH_SIGNING_KEY:?TRADING_AUTH_SIGNING_KEY is required}" \
  -e Trading__Reports__Cvm__OwnerHashSalt=restore-drill-cvm-salt \
  -e Trading__DropCopy__ClOrdIdMaskSalt=restore-drill-clordid-mask-salt \
  -e Trading__Exchange__Mode=Unavailable \
  "$image" >/dev/null

deadline=$((SECONDS + 90))
until health="$(curl --fail --silent --max-time 3 "http://127.0.0.1:${RESTORE_PORT}/health" 2>/dev/null)"; do
  if (( SECONDS >= deadline )); then
    log "ERROR: restored host did not expose /health"
    docker logs "$restore_container" >&2 || true
    exit 1
  fi
  sleep 1
done

python3 -c '
import json,sys
h=json.load(sys.stdin)
assert h["persistence"]["healthy"] is True, h
assert h["identityDirectory"]["ready"] is True, h
assert h["exchange"]["mode"] == "Unavailable", h
' <<<"$health"

if docker logs "$restore_container" 2>&1 | grep -qE 'Persistence recovery: (no snapshot found|WAL record .* aborting replay|terminal fault)'; then
  log "ERROR: restored host reported an invalid or missing recovery set"
  docker logs "$restore_container" >&2
  exit 1
fi

log "restarting original trading-host and verifying liveness"
docker start "$TRADING_CONTAINER" >/dev/null
deadline=$((SECONDS + 90))
until curl --fail --silent --max-time 3 "${ORIGINAL_BASE_URL}/live" >/dev/null 2>&1; do
  if (( SECONDS >= deadline )); then
    log "ERROR: original trading-host did not become live after the drill"
    exit 1
  fi
  sleep 1
done
original_stopped=0

log "PASS: manifest verified and restored host replayed the application-consistent snapshot/WAL set"
