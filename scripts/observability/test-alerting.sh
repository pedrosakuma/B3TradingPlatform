#!/usr/bin/env bash
set -euo pipefail

# Base compose uses fail-closed interpolation even when only observability
# services are selected. These values are parse-only; trading-host is not
# started by this smoke.
export TRADING_AUTH_SIGNING_KEY="${TRADING_AUTH_SIGNING_KEY:-alert-smoke-placeholder-key-min-32-bytes}"
export TRADING_SEED_PASSWORD_HASH="${TRADING_SEED_PASSWORD_HASH:-ZDzDHANAHq8NDQK3BWk/YZjybKLCMKdRzw0z9Da5wic=}"
export TRADING_SEED_PASSWORD_SALT="${TRADING_SEED_PASSWORD_SALT:-rXA+be7/gEYYZQrQDsUr2g==}"
export ALERT_RECEIVER_PORT="${ALERT_RECEIVER_PORT:-18093}"

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
  if python3 -c 'import json,sys; data=json.load(sys.stdin); assert any(a.get("labels",{}).get("alertname")=="B3SyntheticAlert" for batch in data for a in batch.get("alerts",[]))' <<<"$received"; then
    echo "Synthetic B3SyntheticAlert reached the configured Alertmanager receiver."
    exit 0
  fi
  sleep 2
done

echo "ERROR: synthetic alert did not reach the configured receiver within 90s" >&2
curl --silent --show-error "http://127.0.0.1:${ALERT_RECEIVER_PORT}/received" >&2 || true
exit 1
