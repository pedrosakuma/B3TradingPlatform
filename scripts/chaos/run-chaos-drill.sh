#!/usr/bin/env bash
# Q4.15 (#315) — Chaos drill for the trading-host slice.
#
# Exercises a small set of named failure scenarios against a running
# docker-compose stack (`docker/docker-compose.yml` + overlays). Each
# scenario captures pre/post-drill state from REST endpoints and asserts
# the system converged. Exit codes:
#   0  PASS
#   1  scenario assertion failed (state diverged, /ready never came back)
#   2  precondition failed (stack not up, container missing, missing tool)
#   3  usage error
#
# Doc: docs/operations/runbook-failover-recovery.md §6.
set -euo pipefail

readonly TRADING_CONTAINER="${TRADING_CONTAINER:-b3-trading-host}"
readonly MARKETDATA_CONTAINER="${MARKETDATA_CONTAINER:-b3-marketdata}"
readonly DOCKER_NETWORK="${DOCKER_NETWORK:-b3-net}"
readonly TRADING_BASE_URL="${TRADING_BASE_URL:-http://localhost:5000}"
readonly TRADING_USER="${TRADING_SEED_USER:-alice}"
readonly TRADING_PASSWORD="${TRADING_SEED_PASSWORD:-wonderland}"
readonly READY_TIMEOUT_S="${READY_TIMEOUT_S:-60}"
readonly PARTITION_HOLD_S="${PARTITION_HOLD_S:-10}"
readonly POST_KILL_WAIT_S="${POST_KILL_WAIT_S:-5}"
readonly ARTIFACTS_DIR="${CHAOS_ARTIFACTS_DIR:-./chaos-artifacts}"
readonly COMPOSE_FILES=(
    "-f" "docker/docker-compose.yml"
    "-f" "docker/docker-compose.real.yml"
)

usage() {
    cat >&2 <<EOF
Usage: $0 --scenario <name> [--up]

Scenarios:
  host-kill           SIGKILL trading-host, restart, assert /ready and WAL seq monotonic.
  marketdata-kill     SIGKILL marketdata, restart it, assert exchange readiness and a fresh trade.
  network-partition   Disconnect trading-host from ${DOCKER_NETWORK} for ${PARTITION_HOLD_S}s, reconnect, assert no event loss.

Options:
  --up                Bring the compose stack up before the scenario.
  --help              Show this help.

Env overrides:
  TRADING_CONTAINER, MARKETDATA_CONTAINER, DOCKER_NETWORK,
  TRADING_BASE_URL, READY_TIMEOUT_S, PARTITION_HOLD_S, POST_KILL_WAIT_S,
  CHAOS_ARTIFACTS_DIR.
EOF
    exit 3
}

log() { printf '%s [chaos] %s\n' "$(date -u +%Y-%m-%dT%H:%M:%SZ)" "$*" >&2; }
banner_start() { printf '\n===== START scenario: %s =====\n' "$1" >&2; }
banner_end() { printf '===== END   scenario: %s — %s =====\n\n' "$1" "$2" >&2; }

require_cmd() {
    command -v "$1" >/dev/null 2>&1 || {
        log "FATAL: required command not found: $1"
        exit 2
    }
}

ensure_container_running() {
    local name="$1"
    if ! docker inspect -f '{{.State.Running}}' "$name" 2>/dev/null | grep -q true; then
        log "FATAL: container '$name' is not running. Bring up the stack (or pass --up)."
        exit 2
    fi
}

capture_state() {
    # $1 = label (e.g. "pre", "post"); $2 = scenario name
    local label="$1" scenario="$2"
    local out="${ARTIFACTS_DIR}/${scenario}-${label}.json"
    mkdir -p "$ARTIFACTS_DIR"
    local health="{}"
    if health="$(curl -fsS --max-time 5 "${TRADING_BASE_URL}/health" 2>/dev/null)"; then
        :
    else
        health='{"error":"health endpoint unreachable"}'
    fi
    local wal_seq
    wal_seq="$(read_wal_seq || echo '{"snapshot_seq":null,"wal_bytes":null}')"
    printf '{"label":"%s","scenario":"%s","ts":"%s","health":%s,"wal":%s}\n' \
        "$label" "$scenario" "$(date -u +%Y-%m-%dT%H:%M:%SZ)" "$health" "$wal_seq" >"$out"
    log "captured ${label} state → ${out}"
    echo "$out"
}

read_wal_seq() {
    # Pass-1 review (#326) P1 fix. `latest.txt` is a PLAIN integer
    # (see SnapshotStore.Write — `snapshot.Seq.ToString(...)`), not
    # JSON; the previous regex always returned null. We now also
    # compute a WAL footprint proxy (total .log byte count across
    # day directories) so the monotonic assertion still has a
    # signal even before the first snapshot tick. Emits a JSON
    # object with both fields; the caller pulls whichever is
    # appropriate. Returns null fields when nothing is on disk yet.
    local data_dir firm root snap_path snap_seq wal_bytes
    data_dir="$(docker exec "$TRADING_CONTAINER" sh -c 'echo "${Trading__Persistence__DataDirectory:-/var/lib/b3trading}"' 2>/dev/null || echo /var/lib/b3trading)"
    firm="$(docker exec "$TRADING_CONTAINER" sh -c 'echo "${Trading__Persistence__FirmId:-default}"' 2>/dev/null || echo default)"
    root="${data_dir}/${firm}"
    snap_path="${root}/snapshots/latest.txt"
    snap_seq="$(docker exec "$TRADING_CONTAINER" sh -c "test -f '$snap_path' && cat '$snap_path' 2>/dev/null || true" 2>/dev/null | tr -d '[:space:]')"
    if ! [[ "$snap_seq" =~ ^[0-9]+$ ]]; then
        snap_seq=null
    fi
    # Pass-2 review (#326) P1. FileEventStore creates the WAL root
    # at startup, so a fresh stack with no events yields an empty
    # dir. `find ... | awk` then prints `0`, which would pass the
    # numeric regex below and bypass the all-null FAIL — masking a
    # drill against an empty data dir. Require at least one *.log
    # file before reporting a byte count; otherwise emit null.
    local wal_log_count
    wal_log_count="$(docker exec "$TRADING_CONTAINER" sh -c "test -d '${root}/wal' && find '${root}/wal' -type f -name '*.log' 2>/dev/null | wc -l || echo 0" 2>/dev/null | tr -d '[:space:]')"
    if [[ "$wal_log_count" =~ ^[1-9][0-9]*$ ]]; then
        wal_bytes="$(docker exec "$TRADING_CONTAINER" sh -c "find '${root}/wal' -type f -name '*.log' -printf '%s\n' 2>/dev/null | awk 'BEGIN{s=0}{s+=\$1}END{print s}' || echo ''" 2>/dev/null | tr -d '[:space:]')"
        if ! [[ "$wal_bytes" =~ ^[0-9]+$ ]]; then
            wal_bytes=null
        fi
    else
        wal_bytes=null
    fi
    printf '{"snapshot_seq":%s,"wal_bytes":%s}\n' "$snap_seq" "$wal_bytes"
}

wait_for_ready() {
    local timeout="$1"
    local started
    started="$(date +%s)"
    while true; do
        if curl -fsS --max-time 2 "${TRADING_BASE_URL}/ready" >/dev/null 2>&1; then
            return 0
        fi
        if (( $(date +%s) - started >= timeout )); then
            return 1
        fi
        sleep 1
    done
}

assert_exchange_ready() {
    local health
    health="$(curl -fsS --max-time 5 "${TRADING_BASE_URL}/health")" || return 1
    python3 -c '
import json,sys
h=json.load(sys.stdin)
exchange=h.get("exchange") or {}
assert exchange.get("readyForOrders") is True, h
firms=exchange.get("firms") or []
assert firms and all(str(f.get("state","")).lower()=="established" for f in firms), h
' <<<"$health"
}

login_token() {
    curl -fsS --max-time 5 \
        -H 'Content-Type: application/json' \
        -d "{\"username\":\"${TRADING_USER}\",\"password\":\"${TRADING_PASSWORD}\"}" \
        "${TRADING_BASE_URL}/auth/login" \
        | python3 -c 'import json,sys; print(json.load(sys.stdin)["token"])'
}

submit_order() {
    local token="$1" side="$2" price="$3"
    curl -fsS --max-time 10 \
        -H "Authorization: Bearer ${token}" \
        -H 'Content-Type: application/json' \
        -d "{\"symbol\":\"PETR4\",\"side\":\"${side}\",\"type\":\"Limit\",\"quantity\":100,\"price\":${price}}" \
        "${TRADING_BASE_URL}/orders" \
        | python3 -c 'import json,sys; print(json.load(sys.stdin)["clOrdId"])'
}

assert_post_recovery_trade() {
    local token price buy sell deadline orders
    token="$(login_token)"
    price="$(python3 -c 'import time; print(f"{31.00 + (int(time.time()) % 50) / 100:.2f}")')"
    buy="$(submit_order "$token" Buy "$price")"
    sell="$(submit_order "$token" Sell "$price")"
    deadline=$((SECONDS + 30))
    while (( SECONDS < deadline )); do
        orders="$(curl -fsS --max-time 5 -H "Authorization: Bearer ${token}" "${TRADING_BASE_URL}/orders")"
        if BUY="$buy" SELL="$sell" python3 -c '
import json,os,sys
orders=json.load(sys.stdin)
by_id={str(o.get("clOrdId")):o for o in orders}
for key in (os.environ["BUY"], os.environ["SELL"]):
    order=by_id.get(key)
    assert order and order.get("status")=="Filled" and order.get("cumulativeQuantity")==100
' <<<"$orders" 2>/dev/null; then
            log "post-recovery real trade filled (buy=${buy}, sell=${sell}, price=${price})"
            return 0
        fi
        sleep 1
    done
    log "FAIL: post-recovery orders did not both reach Filled (buy=${buy}, sell=${sell})"
    return 1
}

assert_recovered_and_trading() {
    if ! wait_for_ready "$READY_TIMEOUT_S"; then
        log "FAIL: /ready did not recover within ${READY_TIMEOUT_S}s"
        return 1
    fi
    if ! assert_exchange_ready; then
        log "FAIL: /health does not show readyForOrders=true with every firm Established"
        return 1
    fi
    assert_post_recovery_trade
}

extract_firm() {
    local file="$1"
    grep -oE '"firmId"[[:space:]]*:[[:space:]]*"[^"]*"' "$file" | head -n1 | sed -E 's/.*"firmId"[[:space:]]*:[[:space:]]*"([^"]*)".*/\1/'
}

extract_wal_seq() {
    local file="$1"
    # Pass-1 review (#326) P1. The captured JSON now exposes both
    # the snapshot pointer and a WAL bytes proxy under a `wal`
    # object. We expose two extractors: snapshot_seq (the snapshot
    # cursor `latest.txt`, plain integer) and wal_bytes (sum of
    # *.log file sizes).
    grep -oE '"snapshot_seq"[[:space:]]*:[[:space:]]*(null|[0-9]+)' "$file" | head -n1 \
        | sed -E 's/.*"snapshot_seq"[[:space:]]*:[[:space:]]*//'
}

extract_wal_bytes() {
    local file="$1"
    grep -oE '"wal_bytes"[[:space:]]*:[[:space:]]*(null|[0-9]+)' "$file" | head -n1 \
        | sed -E 's/.*"wal_bytes"[[:space:]]*:[[:space:]]*//'
}

assert_health_block() {
    # Asserts the post-drill /health body parsed cleanly and persistence.firmId
    # matches pre-drill (catches "host came up against a fresh data dir").
    local pre="$1" post="$2"
    local pre_firm post_firm
    pre_firm="$(extract_firm "$pre" || true)"
    post_firm="$(extract_firm "$post" || true)"
    if [[ -z "$post_firm" ]]; then
        log "FAIL: post-drill /health did not include persistence.firmId"
        diff -u "$pre" "$post" || true
        return 1
    fi
    if [[ -n "$pre_firm" && "$pre_firm" != "$post_firm" ]]; then
        log "FAIL: persistence.firmId changed across drill (pre='${pre_firm}', post='${post_firm}')"
        return 1
    fi
    return 0
}

assert_wal_seq_monotonic() {
    local pre="$1" post="$2"
    local pre_seq post_seq pre_bytes post_bytes
    pre_seq="$(extract_wal_seq "$pre" || echo null)"
    post_seq="$(extract_wal_seq "$post" || echo null)"
    pre_bytes="$(extract_wal_bytes "$pre" || echo null)"
    post_bytes="$(extract_wal_bytes "$post" || echo null)"
    # Pass-1 review (#326) P1. When BOTH proxies are null on both
    # sides, the chaos drill is running against an effectively empty
    # data dir — silently treating this as PASS hides bugs in the
    # very recovery path the drill is supposed to exercise. Fail it
    # so the operator either pre-seeds load OR moves to a
    # scenario-specific assertion (see network-partition for the
    # duplicate-fill check).
    if [[ "$pre_seq" == "null" && "$post_seq" == "null"
       && "$pre_bytes" == "null" && "$post_bytes" == "null" ]]; then
        log "FAIL: data dir is empty on both sides of the drill (no snapshot + no WAL bytes). Pre-seed load before running this scenario."
        return 1
    fi
    if [[ "$post_seq" == "null" && "$pre_seq" != "null" ]]; then
        log "FAIL: post-drill snapshot pointer is null but pre was '${pre_seq}' (snapshot pointer lost)"
        return 1
    fi
    if [[ "$pre_seq" != "null" && "$post_seq" != "null" ]] && (( post_seq < pre_seq )); then
        log "FAIL: snapshot seq regressed (pre=${pre_seq}, post=${post_seq})"
        return 1
    fi
    if [[ "$pre_bytes" != "null" && "$post_bytes" != "null" ]] && (( post_bytes < pre_bytes )); then
        log "FAIL: WAL footprint shrank across drill (pre=${pre_bytes}B, post=${post_bytes}B) — possible data loss"
        return 1
    fi
    log "WAL monotonic check ✓ (snapshot pre=${pre_seq}, post=${post_seq}; WAL bytes pre=${pre_bytes}, post=${post_bytes})"
    return 0
}

bring_up_stack() {
    log "bringing up compose stack (real overlay)..."
    docker compose "${COMPOSE_FILES[@]}" up -d --wait trading-host
    log "stack up."
}

scenario_host_kill() {
    local scenario="host-kill"
    banner_start "$scenario"
    ensure_container_running "$TRADING_CONTAINER"
    local pre post
    pre="$(capture_state pre "$scenario")"

    log "SIGKILL ${TRADING_CONTAINER}..."
    docker kill -s SIGKILL "$TRADING_CONTAINER" >/dev/null
    log "waiting ${POST_KILL_WAIT_S}s post-kill..."
    sleep "$POST_KILL_WAIT_S"

    log "restarting ${TRADING_CONTAINER}..."
    docker start "$TRADING_CONTAINER" >/dev/null

    log "waiting for exchange readiness and post-recovery trading..."
    if ! assert_recovered_and_trading; then
        banner_end "$scenario" "FAIL"
        return 1
    fi
    post="$(capture_state post "$scenario")"

    if ! assert_health_block "$pre" "$post"; then
        banner_end "$scenario" "FAIL"
        return 1
    fi
    if ! assert_wal_seq_monotonic "$pre" "$post"; then
        banner_end "$scenario" "FAIL"
        return 1
    fi
    banner_end "$scenario" "PASS"
    return 0
}

scenario_marketdata_kill() {
    local scenario="marketdata-kill"
    banner_start "$scenario"
    ensure_container_running "$TRADING_CONTAINER"
    ensure_container_running "$MARKETDATA_CONTAINER"
    local pre post
    pre="$(capture_state pre "$scenario")"

    log "SIGKILL ${MARKETDATA_CONTAINER}..."
    docker kill -s SIGKILL "$MARKETDATA_CONTAINER" >/dev/null
    sleep "$POST_KILL_WAIT_S"

    # Trading-host must still answer /health while the read-side feed is down.
    if ! curl -fsS --max-time 5 "${TRADING_BASE_URL}/health" >/dev/null; then
        log "FAIL: trading-host /health unreachable after marketdata kill"
        banner_end "$scenario" "FAIL"
        return 1
    fi
    log "trading-host /health still 200 after marketdata kill (degraded ok)."

    # Bring marketdata back so subsequent scenarios are sane.
    log "restarting ${MARKETDATA_CONTAINER}..."
    docker start "$MARKETDATA_CONTAINER" >/dev/null
    if ! assert_recovered_and_trading; then
        banner_end "$scenario" "FAIL"
        return 1
    fi
    post="$(capture_state post "$scenario")"

    if ! assert_health_block "$pre" "$post"; then
        banner_end "$scenario" "FAIL"
        return 1
    fi
    banner_end "$scenario" "PASS"
    return 0
}

scenario_network_partition() {
    local scenario="network-partition"
    banner_start "$scenario"
    ensure_container_running "$TRADING_CONTAINER"
    local pre post
    pre="$(capture_state pre "$scenario")"

    log "disconnecting ${TRADING_CONTAINER} from ${DOCKER_NETWORK}..."
    docker network disconnect "$DOCKER_NETWORK" "$TRADING_CONTAINER" >/dev/null
    log "holding partition for ${PARTITION_HOLD_S}s..."
    sleep "$PARTITION_HOLD_S"
    log "reconnecting..."
    docker network connect "$DOCKER_NETWORK" "$TRADING_CONTAINER" >/dev/null

    log "waiting for exchange readiness and post-recovery trading after reconnect..."
    if ! assert_recovered_and_trading; then
        banner_end "$scenario" "FAIL"
        return 1
    fi
    post="$(capture_state post "$scenario")"

    if ! assert_health_block "$pre" "$post"; then
        banner_end "$scenario" "FAIL"
        return 1
    fi
    if ! assert_wal_seq_monotonic "$pre" "$post"; then
        banner_end "$scenario" "FAIL"
        return 1
    fi
    # Pass-1 review (#326) P2. End-to-end "no duplicate fills in
    # projection" requires authenticated /orders submission against
    # a seed user + observation of `/fills/{id}/touch` and is
    # environment-specific (seed credentials, listed symbols).
    # Drive that flow externally and feed the pre/post snapshots via
    # CHAOS_PRE_FILL_TOUCH / CHAOS_POST_FILL_TOUCH; when both are
    # set we assert the touch payload is bit-identical, which is the
    # canonical "no replay clobbering" invariant from Q4.7 (#307).
    if [[ -n "${CHAOS_PRE_FILL_TOUCH:-}" && -n "${CHAOS_POST_FILL_TOUCH:-}" ]]; then
        # Pass-2 review (#326) P2. Process substitution swallows
        # curl exit codes, so a 4xx/5xx on BOTH sides would yield
        # two empty bodies and `diff` would falsely PASS. Fetch
        # both payloads explicitly, fail loudly on any HTTP error,
        # and only then compare.
        local pre_body post_body
        if ! pre_body="$(curl -fsS --max-time 5 "${TRADING_BASE_URL}${CHAOS_PRE_FILL_TOUCH}")"; then
            log "FAIL: pre-partition GET ${CHAOS_PRE_FILL_TOUCH} failed"
            banner_end "$scenario" "FAIL"
            return 1
        fi
        if ! post_body="$(curl -fsS --max-time 5 "${TRADING_BASE_URL}${CHAOS_POST_FILL_TOUCH}")"; then
            log "FAIL: post-partition GET ${CHAOS_POST_FILL_TOUCH} failed"
            banner_end "$scenario" "FAIL"
            return 1
        fi
        if ! diff -u <(printf '%s' "$pre_body") <(printf '%s' "$post_body"); then
            log "FAIL: fill touch payload diverged across partition (replay clobber?)"
            banner_end "$scenario" "FAIL"
            return 1
        fi
        log "fill touch payload identical across partition ✓"
    else
        log "NOTE: CHAOS_PRE_FILL_TOUCH/CHAOS_POST_FILL_TOUCH not set — skipping bit-identical fill-touch assertion (run with these env vars for full coverage)."
    fi
    banner_end "$scenario" "PASS"
    return 0
}

main() {
    local scenario=""
    local do_up=0
    while [[ $# -gt 0 ]]; do
        case "$1" in
            --scenario) scenario="${2:-}"; shift 2 ;;
            --scenario=*) scenario="${1#*=}"; shift ;;
            --up) do_up=1; shift ;;
            --help|-h) usage ;;
            *) log "unknown argument: $1"; usage ;;
        esac
    done

    [[ -n "$scenario" ]] || usage

    require_cmd docker
    require_cmd curl
    require_cmd python3

    if (( do_up == 1 )); then
        bring_up_stack
    fi

    log "waiting up to ${READY_TIMEOUT_S}s for initial /ready before scenario..."
    if ! wait_for_ready "$READY_TIMEOUT_S"; then
        log "FATAL: trading-host is live but order ingress never became ready."
        exit 2
    fi
    if ! assert_exchange_ready || ! assert_post_recovery_trade; then
        log "FATAL: pre-drill exchange readiness/trading probe failed."
        exit 2
    fi

    case "$scenario" in
        host-kill)         scenario_host_kill ;;
        marketdata-kill)   scenario_marketdata_kill ;;
        network-partition) scenario_network_partition ;;
        *) log "unknown scenario: ${scenario}"; usage ;;
    esac
}

main "$@"
