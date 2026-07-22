#!/usr/bin/env bash
set -euo pipefail

readonly TRADING_CONTAINER="${TRADING_CONTAINER:-b3-trading-host}"
readonly TRADING_VOLUME="${TRADING_VOLUME:-b3-trading-data}"
readonly BACKUP_DIR="${BACKUP_DIR:-backup-artifacts}"
readonly RESTORE_PORT="${RESTORE_PORT:-18081}"
readonly ORIGINAL_BASE_URL="${TRADING_BASE_URL:-http://127.0.0.1:5000}"
readonly RESTORE_NETWORK="${RESTORE_NETWORK:-b3-net}"
readonly RECOVERY_USER="${TRADING_SEED_USER:-alice}"
readonly RECOVERY_PASSWORD_HASH="${TRADING_SEED_PASSWORD_HASH:?TRADING_SEED_PASSWORD_HASH is required}"
readonly RECOVERY_PASSWORD_SALT="${TRADING_SEED_PASSWORD_SALT:?TRADING_SEED_PASSWORD_SALT is required}"
readonly RECOVERY_CREDENTIAL_ID="${RECOVERY_CREDENTIAL_ID:?RECOVERY_CREDENTIAL_ID is required}"
readonly timestamp="$(date -u +%Y%m%dT%H%M%SZ)"
readonly archive_name="trading-data-${timestamp}.tar.gz"
readonly manifest_name="trading-data-${timestamp}.sha256"
readonly restore_volume="b3-trading-restore-${timestamp,,}"
readonly restore_container="b3-trading-restore-drill"

original_stopped=0
restore_container_exists=0

log() { printf '%s [backup-drill] %s\n' "$(date -u +%Y-%m-%dT%H:%M:%SZ)" "$*" >&2; }

cleanup() {
  if (( restore_container_exists == 1 )); then
    docker rm -f "$restore_container" >/dev/null 2>&1 || true
  fi
  docker volume rm "$restore_volume" >/dev/null 2>&1 || true
  if (( original_stopped == 1 )); then
    log "restarting original trading-host"
    docker start "$TRADING_CONTAINER" >/dev/null || true
  fi
}
trap cleanup EXIT

mint_token() {
  SIGNING_KEY="$TRADING_AUTH_SIGNING_KEY" SUBJECT="$RECOVERY_USER" python3 -c '
import base64,hashlib,hmac,json,os,time,uuid
def enc(value):
    raw=json.dumps(value,separators=(",",":")).encode()
    return base64.urlsafe_b64encode(raw).rstrip(b"=").decode()
now=int(time.time())
header=enc({"alg":"HS256","typ":"JWT"})
payload=enc({
    "sub":os.environ["SUBJECT"],
    "jti":uuid.uuid4().hex,
    "role":"user",
    "firm":"FIRM01",
    "iss":"b3-trading",
    "aud":"b3-trading-clients",
    "nbf":now-5,
    "iat":now,
    "exp":now+900,
})
unsigned=f"{header}.{payload}"
signature=hmac.new(os.environ["SIGNING_KEY"].encode(),unsigned.encode(),hashlib.sha256).digest()
encoded_signature=base64.urlsafe_b64encode(signature).rstrip(b"=").decode()
print(unsigned+"."+encoded_signature)
'
}

assert_exchange_ready() {
  local base_url="$1" health
  curl -fsS --max-time 5 "${base_url}/ready" >/dev/null
  health="$(curl -fsS --max-time 5 "${base_url}/health")"
  python3 -c '
import json,sys
h=json.load(sys.stdin)
assert h["persistence"]["healthy"] is True, h
assert h["identityDirectory"]["ready"] is True, h
exchange=h.get("exchange") or {}
assert exchange.get("mode") == "Real", h
assert exchange.get("readyForOrders") is True, h
firms=exchange.get("firms") or []
assert firms and all(str(f.get("state","")).lower()=="established" for f in firms), h
' <<<"$health"
}

assert_credential_restored() {
  local base_url="$1" token auth_header response status credentials
  token="$(mint_token)"
  printf -v auth_header 'Authorization: Bearer %s' "$token"
  response="$(curl -sS --max-time 5 -w $'\n%{http_code}' \
    -H "$auth_header" "${base_url}/api/user-bot-credentials")"
  status="${response##*$'\n'}"
  credentials="${response%$'\n'*}"
  if [[ "$status" != "200" ]]; then
    log "ERROR: credential query returned HTTP $status: $credentials"
    return 1
  fi
  EXPECTED_CREDENTIAL_ID="$RECOVERY_CREDENTIAL_ID" python3 -c '
import json,os,sys
expected=os.environ["EXPECTED_CREDENTIAL_ID"].lower()
rows=json.load(sys.stdin)
assert any(str(row.get("id","")).lower()==expected and row.get("revokedAt") is None for row in rows), rows
' <<<"$credentials"
}

submit_order() {
  local base_url="$1" auth_header="$2" side="$3" price="$4"
  local response status body
  response="$(curl -sS --max-time 10 -w $'\n%{http_code}' \
    -H "$auth_header" \
    -H 'Content-Type: application/json' \
    -d "{\"symbol\":\"PETR4\",\"securityId\":900000000001,\"side\":\"${side}\",\"type\":\"Limit\",\"quantity\":100,\"price\":${price}}" \
    "${base_url}/api/orders")"
  status="${response##*$'\n'}"
  body="${response%$'\n'*}"
  if [[ "$status" != "202" ]]; then
    log "ERROR: restored ${side} order returned HTTP $status: $body"
    return 1
  fi
  python3 -c 'import json,sys; print(json.load(sys.stdin)["clOrdId"])' <<<"$body"
}

assert_real_trade() {
  local base_url="$1" token auth_header price buy sell deadline orders
  token="$(mint_token)"
  printf -v auth_header 'Authorization: Bearer %s' "$token"
  price="$(python3 -c 'import time; print(f"{31.00 + (int(time.time()) % 50) / 100:.2f}")')"
  buy="$(submit_order "$base_url" "$auth_header" Buy "$price")"
  sell="$(submit_order "$base_url" "$auth_header" Sell "$price")"
  deadline=$((SECONDS + 30))
  while (( SECONDS < deadline )); do
    orders="$(curl -fsS --max-time 5 -H "$auth_header" "${base_url}/api/orders")"
    if BUY="$buy" SELL="$sell" python3 -c '
import json,os,sys
by_id={str(o.get("clOrdId")):o for o in json.load(sys.stdin)}
for key in (os.environ["BUY"], os.environ["SELL"]):
    order=by_id.get(key)
    assert order and order.get("status")=="Filled" and order.get("cumulativeQuantity")==100
' <<<"$orders" 2>/dev/null; then
      log "restored real-mode trade filled (buy=${buy}, sell=${sell}, price=${price})"
      return 0
    fi
    sleep 1
  done
  log "ERROR: restored real-mode orders did not both reach Filled"
  return 1
}

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
docker network inspect "$RESTORE_NETWORK" >/dev/null
mkdir -p "$BACKUP_DIR"

deadline=$((SECONDS + 90))
until assert_exchange_ready "$ORIGINAL_BASE_URL" 2>/dev/null; do
  if (( SECONDS >= deadline )); then
    log "ERROR: original host is not real-mode exchange-ready; refusing recovery drill"
    exit 2
  fi
  sleep 1
done
if ! assert_credential_restored "$ORIGINAL_BASE_URL"; then
  log "ERROR: seeded credential $RECOVERY_CREDENTIAL_ID is not queryable before backup"
  exit 2
fi

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

log "booting the restored data set in real mode"
docker run -d --name "$restore_container" -p "${RESTORE_PORT}:5000" \
  --network "$RESTORE_NETWORK" \
  -v "$restore_volume:/var/lib/b3trading" \
  -e Trading__Auth__SigningKey="${TRADING_AUTH_SIGNING_KEY:?TRADING_AUTH_SIGNING_KEY is required}" \
  -e Trading__Auth__Users__0__Username="$RECOVERY_USER" \
  -e Trading__Auth__Users__0__PasswordHash="$RECOVERY_PASSWORD_HASH" \
  -e Trading__Auth__Users__0__Salt="$RECOVERY_PASSWORD_SALT" \
  -e Trading__Auth__Users__0__Iterations=600000 \
  -e Trading__Auth__Users__0__Role=user \
  -e Trading__Auth__Users__0__Firm=FIRM01 \
  -e Trading__Reports__Cvm__OwnerHashSalt=restore-drill-cvm-salt \
  -e Trading__DropCopy__ClOrdIdMaskSalt=restore-drill-clordid-mask-salt \
  -e Trading__OutboundCommandProtection__ActiveKeyId=restore-drill \
  -e Trading__OutboundCommandProtection__StableReferenceKeyId=restore-drill \
  -e Trading__OutboundCommandProtection__Keys__0__KeyId=restore-drill \
  -e Trading__OutboundCommandProtection__Keys__0__KeyBase64=N2tjXhiFZ61TrXVa78oABf6mKVhmZV9xV4IxWA4QB9Y= \
  -e Trading__Exchange__Mode=Real \
  -e Trading__Exchange__UseRealEntryPointClient=true \
  -e Trading__Exchange__Firms__0__FirmId=FIRM01 \
  -e Trading__Exchange__Firms__0__Endpoint=matching-platform:9876 \
  -e Trading__Exchange__Firms__0__SessionId=10101 \
  -e Trading__Exchange__Firms__0__SessionVerId=1 \
  -e Trading__Exchange__Firms__0__EnteringFirm=100 \
  -e Trading__Exchange__Firms__0__AccessKey='{"auth_type":"basic","username":"10101","access_key":"dev-key-1"}' \
  -e Trading__Exchange__Firms__0__SenderLocation=SP \
  -e Trading__Exchange__Firms__0__EnteringTrader=ALICE \
  -e Trading__MarketData__WsUrl=ws://marketdata:8080/ws \
  -e Trading__MarketData__Symbols__0=PETR4 \
  -e Trading__Risk__PerEndClient__alice__AllowShortSell=true \
  -e Trading__Risk__PerEndClient__alice__AllowSelfTrade=true \
  "$image" >/dev/null
restore_container_exists=1

deadline=$((SECONDS + 90))
restore_base_url="http://127.0.0.1:${RESTORE_PORT}"
until assert_exchange_ready "$restore_base_url" 2>/dev/null; do
  if (( SECONDS >= deadline )); then
    log "ERROR: restored host did not expose /health"
    docker logs "$restore_container" >&2 || true
    exit 1
  fi
  sleep 1
done

if docker logs "$restore_container" 2>&1 | grep -qE 'Persistence recovery: (no snapshot found|WAL record .* aborting replay|terminal fault)'; then
  log "ERROR: restored host reported an invalid or missing recovery set"
  docker logs "$restore_container" >&2
  exit 1
fi

if ! assert_credential_restored "$restore_base_url"; then
  log "ERROR: durable bot credential $RECOVERY_CREDENTIAL_ID was lost across restore"
  exit 1
fi
assert_real_trade "$restore_base_url"

log "stopping restored host before returning the venue session to the original"
docker stop --time 45 "$restore_container" >/dev/null
docker rm "$restore_container" >/dev/null
restore_container_exists=0

log "restarting original trading-host and verifying readiness"
docker start "$TRADING_CONTAINER" >/dev/null
deadline=$((SECONDS + 90))
until assert_exchange_ready "$ORIGINAL_BASE_URL" >/dev/null 2>&1; do
  if (( SECONDS >= deadline )); then
    log "ERROR: original trading-host did not become live after the drill"
    exit 1
  fi
  sleep 1
done
original_stopped=0

log "PASS: manifest, durable credential, real-mode recovery, and post-restore trade verified"
