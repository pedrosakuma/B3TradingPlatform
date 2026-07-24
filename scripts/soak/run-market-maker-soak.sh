#!/usr/bin/env bash
# Reproducible real-stack market-maker soak driver (#719).
set -euo pipefail

readonly ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT"

profile=""
dry_run=false
keep_stack="${SOAK_KEEP_STACK:-false}"
build_images="${SOAK_BUILD_IMAGES:-true}"
stack_started=false

usage() {
    cat <<'EOF'
Usage: scripts/soak/run-market-maker-soak.sh --profile PROFILE [options]

Profiles:
  baseline            Static/default strategy compatibility.
  inventory-skew      Inventory skew enabled; volatility spread disabled.
  volatility-spread   Volatility spread enabled; inventory skew disabled.
  pause-and-cancel    PauseAndCancel outage/cancel/reconnect exercise.

Options:
  --dry-run            Validate the merged Compose model without starting it.
  --keep-stack         Keep containers and isolated volumes after the run.
  --no-build           Use existing/published images instead of building the bot.
  --help

Required for an actual run:
  docker/.env or exported Compose secrets, plus SOAK_TRADING_PASSWORD.

Operator controls:
  SOAK_WARMUP_SECONDS=60
  SOAK_DURATION_SECONDS=300
  SOAK_SAMPLE_INTERVAL_SECONDS=15
  SOAK_WORKLOAD_INTERVAL_SECONDS=1
  SOAK_PROJECT_NAME=b3tp-soak-<unique>
  SOAK_ARTIFACTS_DIR=soak-artifacts/<run-id>
  SOAK_OUTAGE_SECONDS=20
  SOAK_RECOVERY_TIMEOUT_SECONDS=90
  SOAK_INVENTORY_BIAS_LOTS=12
EOF
}

while (($#)); do
    case "$1" in
        --profile)
            [[ $# -ge 2 ]] || { echo "ERROR: --profile needs a value" >&2; exit 3; }
            profile="$2"
            shift 2
            ;;
        --dry-run) dry_run=true; shift ;;
        --keep-stack) keep_stack=true; shift ;;
        --no-build) build_images=false; shift ;;
        --help) usage; exit 0 ;;
        *) echo "ERROR: unknown argument: $1" >&2; usage >&2; exit 3 ;;
    esac
done

[[ -n "$profile" ]] || { echo "ERROR: --profile is required" >&2; usage >&2; exit 3; }

require_cmd() {
    command -v "$1" >/dev/null 2>&1 || {
        echo "ERROR: required command not found: $1" >&2
        exit 2
    }
}

require_uint() {
    local name="$1" value="$2"
    [[ "$value" =~ ^[0-9]+$ ]] || {
        echo "ERROR: $name must be a non-negative integer, got '$value'" >&2
        exit 3
    }
}

require_cmd docker
require_cmd curl
require_cmd jq
docker compose version >/dev/null

readonly warmup_seconds="${SOAK_WARMUP_SECONDS:-60}"
readonly duration_seconds="${SOAK_DURATION_SECONDS:-300}"
readonly sample_interval_seconds="${SOAK_SAMPLE_INTERVAL_SECONDS:-15}"
readonly workload_interval_seconds="${SOAK_WORKLOAD_INTERVAL_SECONDS:-1}"
readonly outage_seconds="${SOAK_OUTAGE_SECONDS:-20}"
readonly recovery_timeout_seconds="${SOAK_RECOVERY_TIMEOUT_SECONDS:-90}"
readonly inventory_bias_lots="${SOAK_INVENTORY_BIAS_LOTS:-12}"
readonly fill_timeout_seconds="${SOAK_FILL_TIMEOUT_SECONDS:-20}"
readonly trading_user="${SOAK_TRADING_USER:-alice}"
readonly counterparty_user="${SOAK_COUNTERPARTY_USER:-bob}"
readonly symbol="${SOAK_SYMBOL:-PETR4}"
readonly quantity="${SOAK_QUANTITY:-100}"
readonly marketable_buy_price="${SOAK_MARKETABLE_BUY_PRICE:-32.80}"
readonly marketable_sell_price="${SOAK_MARKETABLE_SELL_PRICE:-29.30}"
readonly reference_cross_price="${SOAK_REFERENCE_CROSS_PRICE:-30.00}"
readonly deposit_amount="${SOAK_DEPOSIT_AMOUNT:-100000.00}"

require_uint SOAK_WARMUP_SECONDS "$warmup_seconds"
require_uint SOAK_DURATION_SECONDS "$duration_seconds"
require_uint SOAK_SAMPLE_INTERVAL_SECONDS "$sample_interval_seconds"
require_uint SOAK_OUTAGE_SECONDS "$outage_seconds"
require_uint SOAK_RECOVERY_TIMEOUT_SECONDS "$recovery_timeout_seconds"
require_uint SOAK_INVENTORY_BIAS_LOTS "$inventory_bias_lots"
require_uint SOAK_FILL_TIMEOUT_SECONDS "$fill_timeout_seconds"
(( sample_interval_seconds > 0 )) || { echo "ERROR: sample interval must be positive" >&2; exit 3; }
(( duration_seconds > 0 )) || { echo "ERROR: duration must be positive" >&2; exit 3; }

run_id="$(date -u +%Y%m%dT%H%M%SZ)-${profile}"
readonly project_name="${SOAK_PROJECT_NAME:-b3tp-soak-${run_id,,}}"
[[ "$project_name" =~ ^[a-z0-9][a-z0-9_-]*$ ]] || {
    echo "ERROR: SOAK_PROJECT_NAME must match ^[a-z0-9][a-z0-9_-]*$" >&2
    exit 3
}

export MM_SOAK_PROJECT_NAME="$project_name"
export TRADING_HOST_PORT="${SOAK_TRADING_HOST_PORT:-15000}"
export FRONTEND_PORT="${SOAK_FRONTEND_PORT:-18080}"
export MARKETDATA_PORT="${SOAK_MARKETDATA_PORT:-18081}"
export OTEL_OTLP_GRPC_PORT="${SOAK_OTEL_GRPC_PORT:-14317}"
export OTEL_OTLP_HTTP_PORT="${SOAK_OTEL_HTTP_PORT:-14318}"
export PROMETHEUS_PORT="${SOAK_PROMETHEUS_PORT:-19090}"
export ALERTMANAGER_PORT="${SOAK_ALERTMANAGER_PORT:-19093}"
export ALERT_RECEIVER_PORT="${SOAK_ALERT_RECEIVER_PORT:-18093}"
export GRAFANA_PORT="${SOAK_GRAFANA_PORT:-13000}"
if $build_images; then
    export TRADING_IMAGE="${SOAK_TRADING_IMAGE:-${project_name}-trading-host:dev}"
    export MARKET_MAKER_BOT_IMAGE="${SOAK_MARKET_MAKER_BOT_IMAGE:-${project_name}-market-maker-bot:dev}"
    export MM_SOAK_ALERT_RECEIVER_IMAGE="${SOAK_ALERT_RECEIVER_IMAGE:-${project_name}-alert-receiver:dev}"
else
    [[ -z "${SOAK_TRADING_IMAGE:-}" ]] || export TRADING_IMAGE="$SOAK_TRADING_IMAGE"
    [[ -z "${SOAK_MARKET_MAKER_BOT_IMAGE:-}" ]] || export MARKET_MAKER_BOT_IMAGE="$SOAK_MARKET_MAKER_BOT_IMAGE"
    export MM_SOAK_ALERT_RECEIVER_IMAGE="${SOAK_ALERT_RECEIVER_IMAGE:-b3-alert-receiver:dev}"
fi

case "$profile" in
    baseline)
        export MM_SOAK_INVENTORY_SKEW_ENABLED=false
        export MM_SOAK_VOLATILITY_SPREAD_ENABLED=false
        export MM_SOAK_FEED_LOSS_POLICY=StaticRefPrice
        ;;
    inventory-skew)
        export MM_SOAK_INVENTORY_SKEW_ENABLED=true
        export MM_SOAK_VOLATILITY_SPREAD_ENABLED=false
        export MM_SOAK_FEED_LOSS_POLICY=StaticRefPrice
        ;;
    volatility-spread)
        export MM_SOAK_INVENTORY_SKEW_ENABLED=false
        export MM_SOAK_VOLATILITY_SPREAD_ENABLED=true
        export MM_SOAK_FEED_LOSS_POLICY=StaticRefPrice
        ;;
    pause-and-cancel)
        export MM_SOAK_INVENTORY_SKEW_ENABLED=false
        export MM_SOAK_VOLATILITY_SPREAD_ENABLED=false
        export MM_SOAK_FEED_LOSS_POLICY=PauseAndCancel
        ;;
    *)
        echo "ERROR: unknown profile '$profile'" >&2
        usage >&2
        exit 3
        ;;
esac

readonly compose=(
    docker compose
    --project-name "$project_name"
    -f docker/docker-compose.yml
    -f docker/docker-compose.market-maker.yml
    -f docker/docker-compose.observability.yml
    -f docker/docker-compose.market-maker-soak.yml
)

echo "Validating Compose profile '$profile' (project '$project_name')..."
"${compose[@]}" config --quiet
if $dry_run; then
    printf 'PASS: profile=%s project=%s inventorySkew=%s volatilitySpread=%s feedLossPolicy=%s\n' \
        "$profile" "$project_name" "$MM_SOAK_INVENTORY_SKEW_ENABLED" \
        "$MM_SOAK_VOLATILITY_SPREAD_ENABLED" "$MM_SOAK_FEED_LOSS_POLICY"
    exit 0
fi

[[ -n "${SOAK_TRADING_PASSWORD:-}" ]] || {
    echo "ERROR: set SOAK_TRADING_PASSWORD to the plaintext matching the configured seed hash/salt." >&2
    exit 2
}
readonly trading_password="$SOAK_TRADING_PASSWORD"
readonly counterparty_password="${SOAK_COUNTERPARTY_PASSWORD:-$SOAK_TRADING_PASSWORD}"
readonly base_url="http://127.0.0.1:${TRADING_HOST_PORT}"
readonly prometheus_url="http://127.0.0.1:${PROMETHEUS_PORT}"
readonly artifacts_dir="${SOAK_ARTIFACTS_DIR:-soak-artifacts/${run_id}}"
mkdir -p "$artifacts_dir"
readonly samples_jsonl="${artifacts_dir}/samples.jsonl"
readonly samples_csv="${artifacts_dir}/samples.csv"
readonly workload_csv="${artifacts_dir}/workload.csv"
readonly checks_tsv="${artifacts_dir}/checks.tsv"
printf 'timestamp_utc,phase,metric,symbol,side,reason,available,source,value\n' >"$samples_csv"
printf 'timestamp_utc,phase,user,side,clOrdId,fillLatencySeconds\n' >"$workload_csv"
: >"$samples_jsonl"
: >"$checks_tsv"

log() {
    printf '%s [mm-soak] %s\n' "$(date -u +%Y-%m-%dT%H:%M:%SZ)" "$*" >&2
}

capture_logs() {
    $stack_started || return 0
    "${compose[@]}" logs --no-color --timestamps \
        matching-platform marketdata trading-host market-maker-bot otel-collector prometheus \
        >"${artifacts_dir}/compose.log" 2>&1 || true
}

cleanup() {
    local status=$?
    capture_logs
    if $stack_started && ! $keep_stack; then
        "${compose[@]}" down -v --remove-orphans >/dev/null 2>&1 || true
    fi
    if (( status != 0 )); then
        log "FAILED (exit $status); evidence retained in $artifacts_dir"
    elif $keep_stack; then
        log "PASS; stack retained for project $project_name and evidence is in $artifacts_dir"
    else
        log "PASS; isolated stack removed and evidence is in $artifacts_dir"
    fi
    exit "$status"
}
trap cleanup EXIT

git_sha="$(git rev-parse HEAD)"
jq -n \
    --arg schemaVersion "1" \
    --arg runId "$run_id" \
    --arg profile "$profile" \
    --arg projectName "$project_name" \
    --arg gitSha "$git_sha" \
    --arg symbol "$symbol" \
    --arg inventorySkew "$MM_SOAK_INVENTORY_SKEW_ENABLED" \
    --arg volatilitySpread "$MM_SOAK_VOLATILITY_SPREAD_ENABLED" \
    --arg feedLossPolicy "$MM_SOAK_FEED_LOSS_POLICY" \
    --argjson warmupSeconds "$warmup_seconds" \
    --argjson durationSeconds "$duration_seconds" \
    --argjson sampleIntervalSeconds "$sample_interval_seconds" \
    '{
      schemaVersion: $schemaVersion,
      runId: $runId,
      profile: $profile,
      projectName: $projectName,
      gitSha: $gitSha,
      symbol: $symbol,
      startedAtUtc: (now | todateiso8601),
      settings: {
        inventorySkewEnabled: ($inventorySkew == "true"),
        volatilitySpreadEnabled: ($volatilitySpread == "true"),
        feedLossPolicy: $feedLossPolicy,
        warmupSeconds: $warmupSeconds,
        durationSeconds: $durationSeconds,
        sampleIntervalSeconds: $sampleIntervalSeconds
      },
      acceptanceEligible: (
        $warmupSeconds >= 600 and
        $durationSeconds >= 7200 and
        $sampleIntervalSeconds <= 30
      ),
      evidenceClass: (
        if ($warmupSeconds >= 600 and $durationSeconds >= 7200 and $sampleIntervalSeconds <= 30)
        then "acceptance"
        else "smoke"
        end
      )
    }' >"${artifacts_dir}/run.json"

"${compose[@]}" down -v --remove-orphans >/dev/null 2>&1 || true
if $build_images; then
    log "building trading-host, market-maker-bot, and local alert receiver"
    "${compose[@]}" build trading-host market-maker-bot alert-receiver
fi

services=(trading-host market-maker-bot prometheus)
if [[ "${SOAK_WITH_GRAFANA:-false}" == "true" ]]; then
    services+=(grafana)
fi
log "starting isolated real stack"
stack_started=true
"${compose[@]}" up -d --no-build --wait "${services[@]}"

image_id() {
    local container_id
    container_id="$("${compose[@]}" ps -q "$1")"
    docker inspect -f '{{.Image}}' "$container_id"
}

jq \
    --arg tradingHost "$(image_id trading-host)" \
    --arg marketMakerBot "$(image_id market-maker-bot)" \
    --arg matching "$(image_id matching-platform)" \
    --arg marketData "$(image_id marketdata)" \
    '.images = {
      tradingHost: $tradingHost,
      marketMakerBot: $marketMakerBot,
      matching: $matching,
      marketData: $marketData
    }' "${artifacts_dir}/run.json" >"${artifacts_dir}/run.json.next"
mv "${artifacts_dir}/run.json.next" "${artifacts_dir}/run.json"

wait_http() {
    local url="$1" timeout="$2" started
    started=$SECONDS
    until curl -fsS --max-time 3 "$url" >/dev/null 2>&1; do
        (( SECONDS - started < timeout )) || return 1
        sleep 1
    done
}

wait_http "${base_url}/ready" 90 || { log "ERROR: trading host did not become ready"; exit 1; }
wait_http "${prometheus_url}/-/ready" 60 || { log "ERROR: Prometheus did not become ready"; exit 1; }

login() {
    local user="$1" password="$2"
    curl -fsS --max-time 10 \
        -H 'Content-Type: application/json' \
        -d "$(jq -cn --arg username "$user" --arg password "$password" '{username:$username,password:$password}')" \
        "${base_url}/api/auth/login" | jq -er '.token'
}

auth_header() {
    printf '%s: %s %s' 'Authoriz''ation' 'Bear''er' "$1"
}

trading_token="$(login "$trading_user" "$trading_password")"
readonly trading_token
counterparty_token="$(login "$counterparty_user" "$counterparty_password")"
readonly counterparty_token

curl -fsS --max-time 10 \
    -H "$(auth_header "$trading_token")" \
    -H 'Content-Type: application/json' \
    -d "$(jq -cn --argjson amount "$deposit_amount" '{amount:$amount}')" \
    "${base_url}/api/balance/deposit" >/dev/null

wait_order_filled() {
    local token="$1" clordid="$2" started orders status cum
    started=$SECONDS
    while (( SECONDS - started < fill_timeout_seconds )); do
        orders="$(curl -fsS --max-time 10 -H "$(auth_header "$token")" "${base_url}/api/orders")"
        status="$(jq -r --arg id "$clordid" '.[] | select((.clOrdId|tostring)==$id) | .status' <<<"$orders" | tail -n1)"
        cum="$(jq -r --arg id "$clordid" '.[] | select((.clOrdId|tostring)==$id) | .cumulativeQuantity' <<<"$orders" | tail -n1)"
        if [[ "$status" == "Filled" && "$cum" == "$quantity" ]]; then
            printf '%s\n' "$((SECONDS - started))"
            return 0
        fi
        if [[ "$status" == "Rejected" || "$status" == "Cancelled" ]]; then
            log "ERROR: order $clordid terminated as $status before filling"
            return 1
        fi
        sleep 0.25
    done
    log "ERROR: order $clordid did not fill within ${fill_timeout_seconds}s (last status=${status:-missing}, cum=${cum:-missing})"
    return 1
}

submit_order() {
    local token="$1" user="$2" side="$3" price="$4" phase="$5"
    local response clordid latency
    response="$(curl -fsS --max-time 10 \
        -H "$(auth_header "$token")" \
        -H 'Content-Type: application/json' \
        -d "$(jq -cn \
            --arg symbol "$symbol" \
            --arg side "$side" \
            --argjson quantity "$quantity" \
            --argjson price "$price" \
            '{symbol:$symbol,side:$side,type:"Limit",quantity:$quantity,price:$price}')" \
        "${base_url}/api/orders")"
    [[ "$(jq -r '.status // ""' <<<"$response")" != "Rejected" ]] || {
        log "ERROR: $side order was rejected: $response"
        return 1
    }
    clordid="$(jq -er '.clOrdId | tostring' <<<"$response")"
    latency="$(wait_order_filled "$token" "$clordid")"
    printf '%s,%s,%s,%s,%s,%s\n' \
        "$(date -u +%Y-%m-%dT%H:%M:%SZ)" "$phase" "$user" "$side" "$clordid" "$latency" \
        >>"$workload_csv"
}

prom_query() {
    local body
    body="$(curl -sS --max-time 10 --get \
        --data-urlencode "query=$1" \
        "${prometheus_url}/api/v1/query")"
    if ! jq -e '.status == "success"' >/dev/null <<<"$body"; then
        log "ERROR: Prometheus query failed: $(jq -r '.error // .' <<<"$body")"
        return 1
    fi
    printf '%s\n' "$body"
}

metric_names=(
    bot_position_net_quantity
    bot_position_average_entry_price
    bot_orders_open
    bot_strategy_configured_half_spread_ticks
    bot_strategy_effective_half_spread_ticks
    bot_strategy_inventory_skew_ticks
    bot_strategy_volatility_move_estimate_ticks
    bot_strategy_volatility_additional_half_spread_ticks
    bot_pnl_realized
    bot_pnl_unrealized
    bot_pnl_total
    bot_orders_submitted_total
    bot_orders_submit_failed_total
    bot_fills_received_total
    bot_pnl_fills_applied_total
    bot_pnl_fills_unknown_order_total
    bot_pnl_fills_duplicate_total
    bot_pnl_fills_invalid_total
    bot_pnl_fills_inconsistent_total
    bot_pnl_fill_delta_mismatch_total
    bot_orders_rejected_total
    bot_orders_cancelled_total
    bot_orders_stale_cancelled_total
    bot_orders_stale_cancel_rejected_total
    bot_orders_stale_cancel_submit_failed_total
    bot_orders_safety_cap_hit_total
    bot_orders_book_driven_requote_total
    bot_orders_book_driven_requote_submit_failed_total
    bot_orders_book_driven_requote_cancel_rejected_total
    bot_market_data_availability_transition_total
    bot_market_data_quote_suppressed_total
    bot_market_data_reference_age_seconds
    bot_market_data_reference_eligible
    bot_orders_feed_unavailable_cancel_total
    bot_orders_feed_unavailable_cancel_rejected_total
    bot_orders_feed_unavailable_cancel_submit_failed_total
    bot_orders_feed_unavailable_cancel_retry_total
    bot_orders_cancel_ack_expired_total
)
metric_value() {
    local query="$1" body
    body="$(prom_query "$query")"
    jq -r 'if (.data.result | length) == 0 then "0" else ([.data.result[].value[1] | tonumber] | add | tostring) end' \
        <<<"$body"
}

sum_metric_values() {
    local total=0 value name
    for name in "$@"; do
        value="$(metric_value "sum(${name})")"
        total="$(awk -v total="$total" -v value="$value" 'BEGIN { print total + value }')"
    done
    printf '%s\n' "$total"
}

collect_all_metrics() {
    local results='[]' response more metric_name
    for metric_name in "${metric_names[@]}"; do
        response="$(prom_query "$metric_name")"
        more="$(jq -c '.data.result' <<<"$response")"
        results="$(jq -cn --argjson current "$results" --argjson more "$more" '$current + $more')"
    done
    jq -cn --argjson result "$results" '{data:{result:$result}}'
}

capture_metrics() {
    local phase="$1" timestamp body started normalized csv_rows
    timestamp="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
    started=$SECONDS
    while true; do
        body="$(collect_all_metrics)"
        if jq -e '(.data.result | length) > 0' >/dev/null <<<"$body"; then
            break
        fi
        (( SECONDS - started < 30 )) || {
            log "ERROR: no market-maker metric series were available for phase '$phase'"
            return 1
        }
        sleep 1
    done
    if ! normalized="$(jq -c --arg timestamp "$timestamp" --arg phase "$phase" '
      .data.result[] |
      {
        timestamp_utc: $timestamp,
        phase: $phase,
        metric: .metric.__name__,
        labels: (.metric | del(.__name__)),
        value: (.value[1] as $value | if $value == "NaN" then null else ($value | tonumber) end)
      }' <<<"$body")"; then
        printf '%s\n' "$body" >"${artifacts_dir}/metric-normalization-error.json"
        log "ERROR: failed to normalize market-maker metrics for phase '$phase'"
        return 1
    fi
    printf '%s\n' "$normalized" >>"$samples_jsonl"
    if ! csv_rows="$(jq -r --arg timestamp "$timestamp" --arg phase "$phase" '
      .data.result[] |
      [
        $timestamp,
        $phase,
        .metric.__name__,
        (.metric.symbol // ""),
        (.metric.side // ""),
        (.metric.reason // ""),
        (.metric.available // ""),
        (.metric.source // ""),
        .value[1]
      ] | @csv' <<<"$body")"; then
        printf '%s\n' "$body" >"${artifacts_dir}/metric-csv-error.json"
        log "ERROR: failed to write market-maker metric CSV for phase '$phase'"
        return 1
    fi
    printf '%s\n' "$csv_rows" >>"$samples_csv"
}

wait_metric_equals() {
    local query="$1" expected="$2" timeout="$3" started value
    started=$SECONDS
    while (( SECONDS - started < timeout )); do
        value="$(metric_value "$query")"
        if awk -v actual="$value" -v expected="$expected" 'BEGIN { exit !(actual == expected) }'; then
            return 0
        fi
        sleep 1
    done
    log "ERROR: metric did not reach $expected: $query (last=${value:-missing})"
    return 1
}

next_side=Buy
run_window() {
    local seconds="$1" phase="$2" sample="$3" started next_sample now price side
    started=$SECONDS
    next_sample=$SECONDS
    while (( SECONDS - started < seconds )); do
        now=$SECONDS
        if [[ "$sample" == "true" ]] && (( now >= next_sample )); then
            capture_metrics "$phase"
            next_sample=$((now + sample_interval_seconds))
        fi
        side="$next_side"
        if [[ "$side" == "Buy" ]]; then
            price="$marketable_buy_price"
            next_side=Sell
        else
            price="$marketable_sell_price"
            next_side=Buy
        fi
        submit_order "$trading_token" "$trading_user" "$side" "$price" "$phase"
        sleep "$workload_interval_seconds"
    done
    if [[ "$sample" == "true" ]]; then
        capture_metrics "$phase"
    fi
}

log "waiting for initial two-sided PETR4 quotes"
wait_metric_equals \
    "bot_orders_open{service_name=\"b3-market-maker-bot\",symbol=\"$symbol\"}" \
    2 60

inventory_long_pass=true
inventory_short_pass=true
if [[ "$profile" == "inventory-skew" ]]; then
    log "applying ${inventory_bias_lots}-lot sell bias so the bot becomes long and reaches skew saturation"
    for ((i = 0; i < inventory_bias_lots; i++)); do
        submit_order "$trading_token" "$trading_user" Sell "$marketable_sell_price" "inventory-bias"
    done
    if ! wait_metric_equals \
        "bot_strategy_inventory_skew_ticks{service_name=\"b3-market-maker-bot\",symbol=\"$symbol\"}" \
        5 30; then
        inventory_long_pass=false
    fi
    capture_metrics inventory-long-saturation

    log "reversing through flat to ${inventory_bias_lots} lots short to prove skew direction"
    for ((i = 0; i < inventory_bias_lots * 2; i++)); do
        submit_order "$trading_token" "$trading_user" Buy "$marketable_buy_price" "inventory-reversal"
    done
    if ! wait_metric_equals \
        "bot_strategy_inventory_skew_ticks{service_name=\"b3-market-maker-bot\",symbol=\"$symbol\"}" \
        -5 30; then
        inventory_short_pass=false
    fi
    capture_metrics inventory-short-saturation
fi

log "warmup for ${warmup_seconds}s"
run_window "$warmup_seconds" warmup false
capture_metrics warmup-complete

pause_outage_pass=true
pause_recovery_pass=true
if [[ "$profile" == "pause-and-cancel" ]]; then
    log "stopping marketdata to exercise PauseAndCancel"
    "${compose[@]}" stop marketdata
    if ! wait_metric_equals \
        "bot_market_data_reference_eligible{service_name=\"b3-market-maker-bot\",symbol=\"$symbol\"}" \
        0 "$recovery_timeout_seconds"; then
        pause_outage_pass=false
    fi
    if ! wait_metric_equals \
        "bot_orders_open{service_name=\"b3-market-maker-bot\",symbol=\"$symbol\"}" \
        0 "$recovery_timeout_seconds"; then
        pause_outage_pass=false
    fi
    capture_metrics outage
    sleep "$outage_seconds"

    log "starting marketdata and printing an end-client cross to supply a fresh current-epoch trade"
    "${compose[@]}" start marketdata
    sleep 3
    sell_response="$(curl -fsS --max-time 10 \
        -H "$(printf 'Authorization: Bearer %s' "$counterparty_token")" \
        -H 'Content-Type: application/json' \
        -d "$(jq -cn --arg symbol "$symbol" --argjson quantity "$quantity" --argjson price "$reference_cross_price" \
            '{symbol:$symbol,side:"Sell",type:"Limit",quantity:$quantity,price:$price}')" \
        "${base_url}/api/orders")"
    [[ "$(jq -r '.status // ""' <<<"$sell_response")" != "Rejected" ]] || {
        log "ERROR: recovery-cross sell order was rejected: $sell_response"
        exit 1
    }
    sell_id="$(jq -er '.clOrdId | tostring' <<<"$sell_response")"
    submit_order "$trading_token" "$trading_user" Buy "$reference_cross_price" "feed-recovery-cross"
    wait_order_filled "$counterparty_token" "$sell_id" >/dev/null

    if ! wait_metric_equals \
        "bot_market_data_reference_eligible{service_name=\"b3-market-maker-bot\",symbol=\"$symbol\"}" \
        1 "$recovery_timeout_seconds"; then
        pause_recovery_pass=false
    fi
    if ! wait_metric_equals \
        "count(bot_market_data_reference_age_seconds{service_name=\"b3-market-maker-bot\",symbol=\"$symbol\",source=\"trade\"})" \
        1 "$recovery_timeout_seconds"; then
        pause_recovery_pass=false
    fi
    if ! wait_metric_equals \
        "bot_orders_open{service_name=\"b3-market-maker-bot\",symbol=\"$symbol\"}" \
        2 "$recovery_timeout_seconds"; then
        pause_recovery_pass=false
    fi
    capture_metrics recovered
fi

log "evidence window for ${duration_seconds}s"
run_window "$duration_seconds" duration true
capture_metrics final
"${compose[@]}" ps --format json >"${artifacts_dir}/compose-ps.json"

record_check() {
    local id="$1" passed="$2" expected="$3" observed="$4"
    printf '%s\t%s\t%s\t%s\n' "$id" "$passed" "$expected" "$observed" >>"$checks_tsv"
}

numeric_test() {
    local expression="$1"
    awk "BEGIN { exit !($expression) }"
}

final_open_symbol="$(metric_value "bot_orders_open{service_name=\"b3-market-maker-bot\",symbol=\"$symbol\"}")"
final_open_total="$(metric_value 'sum(bot_orders_open{service_name="b3-market-maker-bot"})')"
corruption_total="$(sum_metric_values \
    bot_pnl_fills_unknown_order_total \
    bot_pnl_fills_duplicate_total \
    bot_pnl_fills_invalid_total \
    bot_pnl_fills_inconsistent_total \
    bot_pnl_fill_delta_mismatch_total)"
safety_total="$(metric_value 'sum(bot_orders_safety_cap_hit_total{service_name="b3-market-maker-bot"})')"
fill_received="$(metric_value 'sum(bot_fills_received_total{service_name="b3-market-maker-bot"})')"
fills_applied="$(metric_value 'sum(bot_pnl_fills_applied_total{service_name="b3-market-maker-bot"})')"
operational_errors="$(sum_metric_values \
    bot_orders_submit_failed_total \
    bot_orders_rejected_total \
    bot_orders_stale_cancel_rejected_total \
    bot_orders_stale_cancel_submit_failed_total \
    bot_orders_book_driven_requote_submit_failed_total \
    bot_orders_book_driven_requote_cancel_rejected_total \
    bot_orders_feed_unavailable_cancel_rejected_total \
    bot_orders_feed_unavailable_cancel_submit_failed_total)"
continuity="$(jq -s --arg symbol "$symbol" '
    [.[] | select(.phase == "duration" and .metric == "bot_orders_open" and .labels.symbol == $symbol)] as $rows |
    if ($rows | length) == 0 then 0
    else (([$rows[] | select(.value == 2)] | length) / ($rows | length))
    end' "$samples_jsonl")"

record_check "open-orders-per-symbol" \
    "$(numeric_test "$final_open_symbol <= 2" && echo true || echo false)" \
    "<=2" "$final_open_symbol"
record_check "open-orders-total" \
    "$(numeric_test "$final_open_total <= 6" && echo true || echo false)" \
    "<=6" "$final_open_total"
record_check "quote-continuity" \
    "$(numeric_test "$continuity >= 0.90" && echo true || echo false)" \
    ">=90% duration samples have two PETR4 quotes" "$continuity"
record_check "accounting-corruption-counters" \
    "$(numeric_test "$corruption_total == 0" && echo true || echo false)" \
    "0" "$corruption_total"
record_check "safety-cap-hits" \
    "$(numeric_test "$safety_total == 0" && echo true || echo false)" \
    "0" "$safety_total"
record_check "own-fills-accounted" \
    "$(numeric_test "$fill_received > 0 && $fill_received == $fills_applied" && echo true || echo false)" \
    "received=applied>0" "received=$fill_received applied=$fills_applied"
record_check "operational-error-counters" \
    "$(numeric_test "$operational_errors == 0" && echo true || echo false)" \
    "0 submit/reject/cancel error events" "$operational_errors"

configured_spread="$(metric_value "bot_strategy_configured_half_spread_ticks{service_name=\"b3-market-maker-bot\",symbol=\"$symbol\"}")"
effective_spread="$(metric_value "bot_strategy_effective_half_spread_ticks{service_name=\"b3-market-maker-bot\",symbol=\"$symbol\"}")"
additional_spread="$(metric_value "bot_strategy_volatility_additional_half_spread_ticks{service_name=\"b3-market-maker-bot\",symbol=\"$symbol\"}")"

case "$profile" in
    baseline)
        record_check "default-static-spread" \
            "$(numeric_test "$effective_spread == $configured_spread && $additional_spread == 0" && echo true || echo false)" \
            "effective=configured and additional=0" \
            "configured=$configured_spread effective=$effective_spread additional=$additional_spread"
        ;;
    inventory-skew)
        net_quantity="$(metric_value "bot_position_net_quantity{service_name=\"b3-market-maker-bot\",symbol=\"$symbol\"}")"
        skew_ticks="$(metric_value "bot_strategy_inventory_skew_ticks{service_name=\"b3-market-maker-bot\",symbol=\"$symbol\"}")"
        record_check "inventory-skew-direction-saturation" \
            "$([[ "$inventory_long_pass" == "true" && "$inventory_short_pass" == "true" ]] && echo true || echo false)" \
            "long inventory skew=+5, short inventory skew=-5" \
            "longPassed=$inventory_long_pass shortPassed=$inventory_short_pass finalNetQuantity=$net_quantity finalSkewTicks=$skew_ticks"
        ;;
    volatility-spread)
        record_check "adaptive-spread-floor-cap-response" \
            "$(numeric_test "$additional_spread > 0 && $additional_spread <= 20 && $effective_spread == $configured_spread + $additional_spread" && echo true || echo false)" \
            "0<additional<=20 and effective=configured+additional" \
            "configured=$configured_spread effective=$effective_spread additional=$additional_spread"
        ;;
    pause-and-cancel)
        record_check "pause-and-cancel-outage" "$pause_outage_pass" \
            "eligibility=0 and openOrders=0 during outage" "passed=$pause_outage_pass"
        record_check "pause-and-cancel-recovery" "$pause_recovery_pass" \
            "fresh current-epoch trade, eligibility=1, openOrders=2" "passed=$pause_recovery_pass"
        ;;
esac

jq -Rn \
    --slurpfile run "${artifacts_dir}/run.json" '
    [inputs | split("\t") | {
      id: .[0],
      passed: (.[1] == "true"),
      expected: .[2],
      observed: .[3]
    }] as $checks |
    $run[0] + {
      finishedAtUtc: (now | todateiso8601),
      checks: $checks,
      passed: ($checks | all(.passed))
    }' <"$checks_tsv" >"${artifacts_dir}/summary.json"

capture_logs
if ! jq -e '.passed' "${artifacts_dir}/summary.json" >/dev/null; then
    jq '.checks[] | select(.passed == false)' "${artifacts_dir}/summary.json" >&2
    exit 1
fi

log "all profile checks passed"
