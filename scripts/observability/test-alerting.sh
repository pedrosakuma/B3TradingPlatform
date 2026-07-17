#!/usr/bin/env bash
set -euo pipefail

# Base compose uses fail-closed interpolation even when only observability
# services are selected. These values are parse-only; trading-host is not
# started by this smoke.
export TRADING_AUTH_SIGNING_KEY="${TRADING_AUTH_SIGNING_KEY:-alert-smoke-placeholder-key-min-32-bytes}"
export TRADING_SEED_PASSWORD_HASH="${TRADING_SEED_PASSWORD_HASH:-ZDzDHANAHq8NDQK3BWk/YZjybKLCMKdRzw0z9Da5wic=}"
export TRADING_SEED_PASSWORD_SALT="${TRADING_SEED_PASSWORD_SALT:-rXA+be7/gEYYZQrQDsUr2g==}"
export ALERT_RECEIVER_PORT="${ALERT_RECEIVER_PORT:-18093}"
export ALERT_SMOKE_RUN_ID="${ALERT_SMOKE_RUN_ID:-run-${GITHUB_RUN_ID:-local}-${GITHUB_RUN_ATTEMPT:-0}-$(date -u +%s)-$$}"
export COMPOSE_PROJECT_NAME="b3alert-${ALERT_SMOKE_RUN_ID}"
readonly started_epoch="$(date -u +%s)"

readonly compose=(
  docker compose
  -f docker/docker-compose.yml
  -f docker/docker-compose.observability.yml
  -f docker/docker-compose.alert-smoke.yml
)

cleanup() {
  "${compose[@]}" down -v >/dev/null 2>&1 || true
}
trap cleanup EXIT

mapfile -t stale_containers < <(
  docker ps -aq --filter label=com.b3trading.alert-smoke=true
)
if (( ${#stale_containers[@]} > 0 )); then
  docker rm -f "${stale_containers[@]}" >/dev/null
fi
for legacy_name in \
  b3-alert-receiver b3-alertmanager b3-prometheus \
  b3-otel-collector b3-synthetic-alert-source; do
  if docker inspect "$legacy_name" >/dev/null 2>&1; then
    docker rm -f "$legacy_name" >/dev/null
  fi
done
cleanup

docker run --rm \
  -v "$PWD/docker/observability/prometheus/rules/v1:/rules:ro" \
  -w /rules \
  --entrypoint promtool \
  prom/prometheus:v2.55.1 \
  test rules b3-trading.rules.test.yaml

docker run --rm \
  -v "$PWD/docker/observability:/etc/b3-observability:ro" \
  -v "$PWD/docker/observability/prometheus/rules:/etc/prometheus/rules:ro" \
  --entrypoint promtool \
  prom/prometheus:v2.55.1 \
  check config /etc/b3-observability/prometheus.alert-smoke.yml

if ! "${compose[@]}" up -d --build --wait \
  alert-receiver alertmanager synthetic-alert-source prometheus; then
  "${compose[@]}" logs --no-color alert-receiver alertmanager synthetic-alert-source prometheus >&2 || true
  exit 1
fi

deadline=$((SECONDS + 90))
while (( SECONDS < deadline )); do
  received="$(curl --fail --silent --max-time 3 "http://127.0.0.1:${ALERT_RECEIVER_PORT}/received" || printf '[]')"
  if RUN_ID="$ALERT_SMOKE_RUN_ID" STARTED_EPOCH="$started_epoch" python3 -c '
import datetime,json,os,sys
data=json.load(sys.stdin)
started=float(os.environ["STARTED_EPOCH"])
run_id=os.environ["RUN_ID"]
def current(alert):
    labels=alert.get("labels",{})
    raw=alert.get("startsAt","").replace("Z","+00:00")
    try:
        alert_started=datetime.datetime.fromisoformat(raw).timestamp()
    except ValueError:
        return False
    return labels.get("alertname")=="B3SyntheticAlert" and labels.get("run_id")==run_id and alert_started >= started
assert any(current(alert) for batch in data for alert in batch.get("alerts",[]))
' <<<"$received"; then
    echo "Synthetic B3SyntheticAlert for run_id=$ALERT_SMOKE_RUN_ID reached the receiver after this run started."
    exit 0
  fi
  sleep 2
done

echo "ERROR: synthetic alert did not reach the configured receiver within 90s" >&2
curl --silent --show-error "http://127.0.0.1:${ALERT_RECEIVER_PORT}/received" >&2 || true
exit 1
