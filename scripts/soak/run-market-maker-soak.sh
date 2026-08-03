#!/usr/bin/env bash
# Reproducible real-stack market-maker soak driver (#719).
set -euo pipefail
case "$-" in *x*) set +x ;; esac

trading_password="${SOAK_TRADING_PASSWORD:-}"
counterparty_password="${SOAK_COUNTERPARTY_PASSWORD:-${SOAK_TRADING_PASSWORD:-}}"
unset SOAK_TRADING_PASSWORD SOAK_COUNTERPARTY_PASSWORD
export -n trading_password counterparty_password
readonly trading_password counterparty_password

readonly ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd -P)"
cd "$ROOT"
source "$ROOT/scripts/soak/market-maker-soak-lib.sh"
if ! soak_child_environment_is_secret_free trading_password counterparty_password; then
    echo "ERROR: password input remained visible to child processes after environment scrubbing" >&2
    exit 2
fi
credential_environment_check_count=1

profile=""
dry_run=false
keep_stack="${SOAK_KEEP_STACK:-false}"
build_images="${SOAK_BUILD_IMAGES:-true}"
with_grafana="${SOAK_WITH_GRAFANA:-false}"
stack_started=false
runtime_event_monitor_pid=""

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
  SOAK_RECOVERY_TIMEOUT_SECONDS=120
  SOAK_RECOVERY_CROSS_ATTEMPTS=3
  SOAK_RECOVERY_CROSS_INTERVAL_SECONDS=10
  SOAK_RECONNECT_STALE_HOLD_SECONDS=<derived export+scrape cycle>
  SOAK_STRICT_REFRESH_INTERVAL_SECONDS=<derived from MaxReferenceAge>
  SOAK_STRICT_REFRESH_MARGIN_SECONDS=<derived telemetry+execution margin>
  SOAK_STRICT_REFRESH_CYCLE_BUDGET_SECONDS=5
  SOAK_PRE_OUTAGE_STABILIZATION_CYCLES=2
  SOAK_PRE_OUTAGE_STABILIZATION_INTERVAL_SECONDS=<derived export+scrape cycle>
  SOAK_PRE_OUTAGE_STABILIZATION_TIMEOUT_SECONDS=<derived>
  SOAK_INVENTORY_BIAS_LOTS=12
  SOAK_MARKETABLE_PRICE_EXTRA_TICKS=1
  SOAK_SUITE_MANIFEST=soak-artifacts/<suite-id>/suite-manifest.json
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

require_bool() {
    local name="$1" value="$2"
    [[ "$value" == "true" || "$value" == "false" ]] || {
        echo "ERROR: $name must be true or false, got '$value'" >&2
        exit 3
    }
}

require_cmd docker
require_cmd curl
require_cmd jq
require_cmd realpath
docker compose version >/dev/null

readonly warmup_seconds="${SOAK_WARMUP_SECONDS:-60}"
readonly duration_seconds="${SOAK_DURATION_SECONDS:-300}"
readonly sample_interval_seconds="${SOAK_SAMPLE_INTERVAL_SECONDS:-15}"
readonly workload_interval_seconds="${SOAK_WORKLOAD_INTERVAL_SECONDS:-1}"
readonly outage_seconds="${SOAK_OUTAGE_SECONDS:-20}"
readonly recovery_timeout_seconds="${SOAK_RECOVERY_TIMEOUT_SECONDS:-120}"
readonly recovery_cross_attempts="${SOAK_RECOVERY_CROSS_ATTEMPTS:-3}"
readonly recovery_cross_interval_seconds="${SOAK_RECOVERY_CROSS_INTERVAL_SECONDS:-10}"
readonly metric_export_interval_ms="${MM_SOAK_METRIC_EXPORT_INTERVAL_MS:-5000}"
require_uint MM_SOAK_METRIC_EXPORT_INTERVAL_MS "$metric_export_interval_ms"
readonly prometheus_scrape_interval_seconds=5
readonly metric_export_interval_seconds="$(((metric_export_interval_ms + 999) / 1000))"
readonly full_telemetry_cycle_seconds="$((metric_export_interval_seconds + prometheus_scrape_interval_seconds))"
readonly max_reference_age="${MM_SOAK_MAX_REFERENCE_AGE:-00:00:30}"
max_reference_age_seconds="$(soak_duration_to_seconds "$max_reference_age")" || {
    echo "ERROR: MM_SOAK_MAX_REFERENCE_AGE must use HH:MM:SS, got '$max_reference_age'" >&2
    exit 3
}
readonly max_reference_age_seconds
readonly strict_refresh_cycle_budget_seconds="${SOAK_STRICT_REFRESH_CYCLE_BUDGET_SECONDS:-5}"
readonly strict_refresh_margin_seconds="${SOAK_STRICT_REFRESH_MARGIN_SECONDS:-$((full_telemetry_cycle_seconds + strict_refresh_cycle_budget_seconds))}"
readonly derived_strict_refresh_interval_seconds="$(((max_reference_age_seconds - strict_refresh_margin_seconds) / 2))"
readonly strict_refresh_interval_seconds="${SOAK_STRICT_REFRESH_INTERVAL_SECONDS:-$derived_strict_refresh_interval_seconds}"
readonly pre_outage_stabilization_cycles="${SOAK_PRE_OUTAGE_STABILIZATION_CYCLES:-2}"
require_uint SOAK_PRE_OUTAGE_STABILIZATION_CYCLES "$pre_outage_stabilization_cycles"
readonly pre_outage_stabilization_interval_seconds="${SOAK_PRE_OUTAGE_STABILIZATION_INTERVAL_SECONDS:-$full_telemetry_cycle_seconds}"
require_uint SOAK_PRE_OUTAGE_STABILIZATION_INTERVAL_SECONDS "$pre_outage_stabilization_interval_seconds"
readonly pre_outage_stabilization_timeout_seconds="${SOAK_PRE_OUTAGE_STABILIZATION_TIMEOUT_SECONDS:-$((pre_outage_stabilization_interval_seconds * (pre_outage_stabilization_cycles + 4)))}"
require_uint SOAK_PRE_OUTAGE_STABILIZATION_TIMEOUT_SECONDS "$pre_outage_stabilization_timeout_seconds"
readonly reconnect_stale_hold_seconds="${SOAK_RECONNECT_STALE_HOLD_SECONDS:-$full_telemetry_cycle_seconds}"
require_uint SOAK_RECONNECT_STALE_HOLD_SECONDS "$reconnect_stale_hold_seconds"
readonly inventory_bias_lots="${SOAK_INVENTORY_BIAS_LOTS:-12}"
readonly fill_timeout_seconds="${SOAK_FILL_TIMEOUT_SECONDS:-20}"
readonly trading_user="${SOAK_TRADING_USER:-alice}"
readonly counterparty_user="${SOAK_COUNTERPARTY_USER:-bob}"
readonly admin_user="${SOAK_ADMIN_USER:-soak-admin}"
readonly symbol="${SOAK_SYMBOL:-PETR4}"
readonly quantity="${SOAK_QUANTITY:-100}"
readonly marketable_buy_price="${SOAK_MARKETABLE_BUY_PRICE:-32.80}"
readonly marketable_sell_price="${SOAK_MARKETABLE_SELL_PRICE:-29.30}"
readonly marketable_price_extra_ticks="${SOAK_MARKETABLE_PRICE_EXTRA_TICKS:-1}"
readonly reference_cross_price="${SOAK_REFERENCE_CROSS_PRICE:-30.00}"
deposit_amount="$(soak_normalize_brl_amount \
    "${SOAK_DEPOSIT_AMOUNT:-$SOAK_ACCEPTANCE_DEPOSIT_AMOUNT_DEFAULT}")" || {
    echo "ERROR: SOAK_DEPOSIT_AMOUNT must be a positive BRL decimal with at most 2 fractional digits" >&2
    exit 3
}
readonly deposit_amount
counterparty_deposit_amount="$(soak_normalize_brl_amount \
    "${SOAK_COUNTERPARTY_DEPOSIT_AMOUNT:-$deposit_amount}")" || {
    echo "ERROR: SOAK_COUNTERPARTY_DEPOSIT_AMOUNT must be a positive BRL decimal with at most 2 fractional digits" >&2
    exit 3
}
readonly counterparty_deposit_amount
explicit_sandbox_max_deposit_amount=""
if [[ -n "${SOAK_SANDBOX_MAX_DEPOSIT_AMOUNT:-}" ]]; then
    explicit_sandbox_max_deposit_amount="$(soak_normalize_brl_amount \
        "$SOAK_SANDBOX_MAX_DEPOSIT_AMOUNT")" || {
        echo "ERROR: SOAK_SANDBOX_MAX_DEPOSIT_AMOUNT must be a positive BRL decimal with at most 2 fractional digits" >&2
        exit 3
    }
fi
sandbox_max_deposit_amount="$(soak_resolve_sandbox_max_deposit \
    "$deposit_amount" "$counterparty_deposit_amount" \
    "$explicit_sandbox_max_deposit_amount")" || {
    echo "ERROR: SOAK_SANDBOX_MAX_DEPOSIT_AMOUNT must cover both SOAK_DEPOSIT_AMOUNT and SOAK_COUNTERPARTY_DEPOSIT_AMOUNT" >&2
    exit 3
}
readonly sandbox_max_deposit_amount
readonly auth_refresh_margin_seconds="${SOAK_AUTH_REFRESH_MARGIN_SECONDS:-300}"

for identity in "$trading_user" "$counterparty_user" "$admin_user"; do
    [[ "$identity" =~ ^[A-Za-z0-9][A-Za-z0-9._-]*$ ]] || {
        echo "ERROR: soak identities must match ^[A-Za-z0-9][A-Za-z0-9._-]*$" >&2
        exit 3
    }
done
[[ "$trading_user" != "$counterparty_user" ]] || {
    echo "ERROR: SOAK_TRADING_USER and SOAK_COUNTERPARTY_USER must differ" >&2
    exit 3
}
[[ "$admin_user" != "$trading_user" && "$admin_user" != "$counterparty_user" ]] || {
    echo "ERROR: SOAK_ADMIN_USER must differ from the trading and counterparty users" >&2
    exit 3
}

require_uint SOAK_WARMUP_SECONDS "$warmup_seconds"
require_uint SOAK_DURATION_SECONDS "$duration_seconds"
require_uint SOAK_SAMPLE_INTERVAL_SECONDS "$sample_interval_seconds"
require_uint SOAK_WORKLOAD_INTERVAL_SECONDS "$workload_interval_seconds"
require_uint SOAK_OUTAGE_SECONDS "$outage_seconds"
require_uint SOAK_RECOVERY_TIMEOUT_SECONDS "$recovery_timeout_seconds"
require_uint SOAK_RECOVERY_CROSS_ATTEMPTS "$recovery_cross_attempts"
require_uint SOAK_RECOVERY_CROSS_INTERVAL_SECONDS "$recovery_cross_interval_seconds"
require_uint SOAK_INVENTORY_BIAS_LOTS "$inventory_bias_lots"
require_uint SOAK_FILL_TIMEOUT_SECONDS "$fill_timeout_seconds"
require_uint SOAK_MARKETABLE_PRICE_EXTRA_TICKS "$marketable_price_extra_ticks"
require_uint SOAK_AUTH_REFRESH_MARGIN_SECONDS "$auth_refresh_margin_seconds"
require_uint SOAK_STRICT_REFRESH_INTERVAL_SECONDS "$strict_refresh_interval_seconds"
require_uint SOAK_STRICT_REFRESH_MARGIN_SECONDS "$strict_refresh_margin_seconds"
require_uint SOAK_STRICT_REFRESH_CYCLE_BUDGET_SECONDS "$strict_refresh_cycle_budget_seconds"
require_bool SOAK_KEEP_STACK "$keep_stack"
require_bool SOAK_BUILD_IMAGES "$build_images"
require_bool SOAK_WITH_GRAFANA "$with_grafana"
(( sample_interval_seconds > 0 )) || { echo "ERROR: sample interval must be positive" >&2; exit 3; }
(( workload_interval_seconds > 0 )) || { echo "ERROR: workload interval must be positive" >&2; exit 3; }
(( duration_seconds > 0 )) || { echo "ERROR: duration must be positive" >&2; exit 3; }
(( recovery_cross_attempts > 0 )) || { echo "ERROR: recovery cross attempts must be positive" >&2; exit 3; }
(( recovery_cross_interval_seconds > 0 )) || { echo "ERROR: recovery cross interval must be positive" >&2; exit 3; }
(( metric_export_interval_ms > 0 )) || { echo "ERROR: metric export interval must be positive" >&2; exit 3; }
(( strict_refresh_cycle_budget_seconds > 0 )) || { echo "ERROR: strict refresh cycle budget must be positive" >&2; exit 3; }
soak_validate_strict_refresh_timing \
    "$max_reference_age_seconds" \
    "$strict_refresh_interval_seconds" \
    "$strict_refresh_margin_seconds" \
    "$full_telemetry_cycle_seconds" || {
    echo "ERROR: strict refresh timing must satisfy interval>0, margin>=one telemetry cycle, and interval+margin<MaxReferenceAge (${strict_refresh_interval_seconds}+${strict_refresh_margin_seconds}<${max_reference_age_seconds})" >&2
    exit 3
}
(( pre_outage_stabilization_cycles >= 2 )) || {
    echo "ERROR: pre-outage stabilization requires at least two full telemetry cycles" >&2
    exit 3
}
(( pre_outage_stabilization_interval_seconds >= full_telemetry_cycle_seconds )) || {
    echo "ERROR: pre-outage stabilization interval must cover one OTLP export + Prometheus scrape cycle (${full_telemetry_cycle_seconds}s)" >&2
    exit 3
}
(( pre_outage_stabilization_timeout_seconds >=
    pre_outage_stabilization_interval_seconds * pre_outage_stabilization_cycles )) || {
    echo "ERROR: pre-outage stabilization timeout cannot cover the required stable cycles" >&2
    exit 3
}
(( reconnect_stale_hold_seconds >= full_telemetry_cycle_seconds )) || {
    echo "ERROR: reconnect stale-data hold must cover one OTLP export + Prometheus scrape cycle (${full_telemetry_cycle_seconds}s)" >&2
    exit 3
}
(( reconnect_stale_hold_seconds +
    (recovery_cross_attempts - 1) * recovery_cross_interval_seconds < recovery_timeout_seconds )) || {
    echo "ERROR: reconnect stale-data hold and recovery cross rounds consume the entire recovery timeout" >&2
    exit 3
}

run_id="$(date -u +%Y%m%dT%H%M%SZ)-${profile}"
readonly project_name="${SOAK_PROJECT_NAME:-b3tp-soak-${run_id,,}}"
[[ "$project_name" =~ ^[a-z0-9][a-z0-9_-]*$ ]] || {
    echo "ERROR: SOAK_PROJECT_NAME must match ^[a-z0-9][a-z0-9_-]*$" >&2
    exit 3
}

export MM_SOAK_PROJECT_NAME="$project_name"
export TRADING_SEED_USER="${TRADING_SEED_USER:-$trading_user}"
export TRADING_SEED_USER_2="${TRADING_SEED_USER_2:-$counterparty_user}"
export MM_SOAK_COUNTERPARTY_USER="$counterparty_user"
export MM_SOAK_ADMIN_USER="$admin_user"
export TRADING_HOST_PORT="${SOAK_TRADING_HOST_PORT:-15000}"
export FRONTEND_PORT="${SOAK_FRONTEND_PORT:-18080}"
export MARKETDATA_PORT="${SOAK_MARKETDATA_PORT:-18081}"
export OTEL_OTLP_GRPC_PORT="${SOAK_OTEL_GRPC_PORT:-14317}"
export OTEL_OTLP_HTTP_PORT="${SOAK_OTEL_HTTP_PORT:-14318}"
export PROMETHEUS_PORT="${SOAK_PROMETHEUS_PORT:-19090}"
export ALERTMANAGER_PORT="${SOAK_ALERTMANAGER_PORT:-19093}"
export ALERT_RECEIVER_PORT="${SOAK_ALERT_RECEIVER_PORT:-18093}"
export GRAFANA_PORT="${SOAK_GRAFANA_PORT:-13000}"
export MM_SOAK_SUBNET="${SOAK_SUBNET:-192.168.64.0/24}"
export MM_SOAK_MAX_DEPOSIT_AMOUNT="$sandbox_max_deposit_amount"
export MM_SOAK_MAX_REFERENCE_AGE="$max_reference_age"
export MM_SOAK_MARK_MAX_AGE="$max_reference_age"
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
if [[ "$profile" == "pause-and-cancel" ]] && (( outage_seconds == 0 )); then
    echo "ERROR: SOAK_OUTAGE_SECONDS must be positive for pause-and-cancel" >&2
    exit 3
fi

readonly compose=(
    docker compose
    --project-name "$project_name"
    -f docker/docker-compose.yml
    -f docker/docker-compose.market-maker.yml
    -f docker/docker-compose.observability.yml
    -f docker/docker-compose.market-maker-soak.yml
)

echo "Validating Compose profile '$profile' (project '$project_name')..."
rendered_compose="$("${compose[@]}" config --format json)"
if ! jq -e \
    --arg inventorySkew "$MM_SOAK_INVENTORY_SKEW_ENABLED" \
    --arg volatilitySpread "$MM_SOAK_VOLATILITY_SPREAD_ENABLED" \
    --arg feedLossPolicy "$MM_SOAK_FEED_LOSS_POLICY" \
    --arg maxReferenceAge "$MM_SOAK_MAX_REFERENCE_AGE" \
    --arg sandboxMaxDepositAmount "$MM_SOAK_MAX_DEPOSIT_AMOUNT" \
    --arg tradingUser "$trading_user" \
    --arg counterpartyUser "$counterparty_user" \
    --arg adminUser "$admin_user" '
      .services["trading-host"].environment as $hostEnvironment |
      .services["trading-host"].environment.Trading__Auth__Mode == "Local" and
      .services["trading-host"].environment.Trading__Auth__LocalLoginEnabled == "true" and
      $hostEnvironment.Trading__Sandbox__MaxDepositAmount == $sandboxMaxDepositAmount and
      $hostEnvironment["Trading__Risk__PerEndClient__" + $counterpartyUser + "__AllowShortSell"] == "true" and
      ([$hostEnvironment | to_entries[] |
        select(.key | test("^Trading__Auth__Users__[0-9]+__Username$")) |
        select(.value == $tradingUser)] | length) == 1 and
      ([$hostEnvironment | to_entries[] |
        select(.key | test("^Trading__Auth__Users__[0-9]+__Username$")) |
        select(.value == $counterpartyUser)] | length) == 1 and
      ([$hostEnvironment | to_entries[] |
        select(.key | test("^Trading__Auth__Users__[0-9]+__Username$")) |
        select(.value == $adminUser)] | length) == 1 and
      $hostEnvironment.Trading__Auth__Users__8__Role == "admin" and
      .services["market-maker-bot"].environment.MarketMaker__MarketData__FeedLossPolicy == $feedLossPolicy and
      .services["market-maker-bot"].environment.MarketMaker__MarketData__MaxReferenceAge == $maxReferenceAge and
      .services["market-maker-bot"].environment.MarketMaker__Telemetry__MarkMaxAge == $maxReferenceAge and
      ([.services["market-maker-bot"].environment
          | to_entries[]
          | select(.key | test("^MarketMaker__Instruments__[0-9]+__InventorySkew__Enabled$"))
          | .value] | length > 0 and all(. == $inventorySkew)) and
      ([.services["market-maker-bot"].environment
          | to_entries[]
          | select(.key | test("^MarketMaker__Instruments__[0-9]+__VolatilitySpread__Enabled$"))
          | .value] | length > 0 and all(. == $volatilitySpread))
    ' >/dev/null <<<"$rendered_compose"; then
    echo "ERROR: rendered Compose profile does not pin Local auth or the requested strategy settings" >&2
    exit 3
fi

sanitized_rendered_config="$(jq --arg counterpartyUser "$counterparty_user" '
  .services["trading-host"].environment as $hostEnvironment |
{
  services: {
    tradingHost: {
      image: .services["trading-host"].image,
      auth: {
        mode: .services["trading-host"].environment.Trading__Auth__Mode,
        localLoginEnabled: .services["trading-host"].environment.Trading__Auth__LocalLoginEnabled
      },
      seedUsers: [
        $hostEnvironment | to_entries[] |
        select(.key | test("^Trading__Auth__Users__[0-9]+__Username$")) |
        (.key | capture("^Trading__Auth__Users__(?<index>[0-9]+)__Username$").index | tonumber) as $index |
        {
          seedIndex: $index,
          username: .value,
          firm: $hostEnvironment["Trading__Auth__Users__" + ($index | tostring) + "__Firm"],
          role: $hostEnvironment["Trading__Auth__Users__" + ($index | tostring) + "__Role"]
        }
      ] | sort_by(.seedIndex),
      workloadRisk: {
        counterparty: $counterpartyUser,
        allowShortSell: $hostEnvironment["Trading__Risk__PerEndClient__" + $counterpartyUser + "__AllowShortSell"]
      },
      sandboxCash: {
        maxDepositAmount: $hostEnvironment.Trading__Sandbox__MaxDepositAmount,
        maxBalanceAfterDeposit: $hostEnvironment.Trading__Sandbox__MaxBalanceAfterDeposit
      }
    },
    marketMakerBot: {
      image: .services["market-maker-bot"].image,
      configuration: (
        .services["market-maker-bot"].environment
        | with_entries(select(
            (.key | startswith("MarketMaker__Instruments__")) or
            (.key | startswith("MarketMaker__MarketData__")) or
            (.key | startswith("MarketMaker__Telemetry__"))
          ))
      )
    },
    matchingPlatform: {image: .services["matching-platform"].image},
    marketData: {image: .services.marketdata.image},
    otelCollector: {image: .services["otel-collector"].image},
    prometheus: {image: .services.prometheus.image}
  },
  network: {
    name: .networks["b3-net"].name,
    ipam: .networks["b3-net"].ipam.config
  }
}' <<<"$rendered_compose")"

if ! identity_mappings_json="$(jq -ce \
    --arg tradingUser "$trading_user" \
    --arg counterpartyUser "$counterparty_user" \
    --arg adminUser "$admin_user" '
      .services.tradingHost as $host |
      ($host.seedUsers | map(select(.username == $tradingUser))) as $trading |
      ($host.seedUsers | map(select(.username == $counterpartyUser))) as $counterparty |
      ($host.seedUsers | map(select(.username == $adminUser))) as $admin |
      if ($trading | length) == 1 and ($counterparty | length) == 1 and
         ($admin | length) == 1 and $admin[0].role == "admin" and
         $host.workloadRisk.counterparty == $counterpartyUser and
         $host.workloadRisk.allowShortSell == "true"
      then {
        trading: $trading[0],
        counterparty: $counterparty[0],
        diagnosticsAdmin: $admin[0],
        risk: {
          counterparty: $counterpartyUser,
          allowShortSell: true
        }
      }
      else error("resolved seed/risk mapping is unavailable or ambiguous")
      end
    ' <<<"$sanitized_rendered_config")"; then
    echo "ERROR: rendered Compose profile lacks a unique secret-free seed/risk mapping for the soak identities" >&2
    exit 3
fi
sanitized_rendered_config="$(jq --argjson identities "$identity_mappings_json" \
    '.services.tradingHost.workloadIdentities = $identities' <<<"$sanitized_rendered_config")"

mapfile -t configured_symbols < <(jq -r '
  .services.marketMakerBot.configuration
  | to_entries
  | map(select(.key | test("^MarketMaker__Instruments__[0-9]+__Symbol$")))
  | sort_by(.key)
  | .[].value
' <<<"$sanitized_rendered_config")
configured_instruments_json="$(jq --arg targetSymbol "$symbol" --argjson targetPrice "$reference_cross_price" '
  .services.marketMakerBot.configuration as $config |
  [range(0; 3) as $index |
   ($config["MarketMaker__Instruments__" + ($index | tostring) + "__Symbol"]) as $symbol | {
    symbol: $symbol,
    quantity: ($config["MarketMaker__Instruments__" + ($index | tostring) + "__LotSize"] | tonumber),
    tickSize: ($config["MarketMaker__Instruments__" + ($index | tostring) + "__TickSize"] | tonumber),
    spreadTicks: ($config["MarketMaker__Instruments__" + ($index | tostring) + "__SpreadTicks"] | tonumber),
    maxSkewTicks: (
      if $config["MarketMaker__Instruments__" + ($index | tostring) + "__InventorySkew__Enabled"] == "true"
      then ($config["MarketMaker__Instruments__" + ($index | tostring) + "__InventorySkew__MaxSkewTicks"] | tonumber)
      else 0
      end
    ),
    maxVolatilityAdditionalSpreadTicks: (
      if $config["MarketMaker__Instruments__" + ($index | tostring) + "__VolatilitySpread__Enabled"] == "true"
      then ($config["MarketMaker__Instruments__" + ($index | tostring) + "__VolatilitySpread__MaxAdditionalSpreadTicks"] | tonumber)
      else 0
      end
    ),
    referencePrice: (
      if $symbol == $targetSymbol then $targetPrice
      else ($config["MarketMaker__Instruments__" + ($index | tostring) + "__RefPrice"] | tonumber)
      end
    )
  }]
' <<<"$sanitized_rendered_config")"
if [[ "${#configured_symbols[@]}" -ne 3 ]] ||
    ! jq -e --arg symbol "$symbol" '
      all(
        .tickSize > 0 and
        .spreadTicks > 1 and
        .maxSkewTicks >= 0 and
        .maxVolatilityAdditionalSpreadTicks >= 0
      ) and
      any(.symbol == $symbol)
    ' >/dev/null <<<"$configured_instruments_json"; then
    echo "ERROR: rendered profile must contain PETR4/target plus exactly three configured symbols" >&2
    exit 3
fi
readonly evidence_root="${ROOT}/soak-artifacts"
readonly artifacts_dir_input="${SOAK_ARTIFACTS_DIR:-soak-artifacts/${run_id}}"
readonly suite_manifest_input="${SOAK_SUITE_MANIFEST:-}"

git check-ignore -q --no-index "soak-artifacts/.integrity-probe" || {
    echo "ERROR: repository soak-artifacts/ path is not ignored by git" >&2
    exit 3
}
[[ "$(realpath -m "$evidence_root")" == "$evidence_root" ]] || {
    echo "ERROR: repository soak-artifacts path is a symlink or resolves outside the repository" >&2
    exit 3
}

canonical_evidence_path() {
    local label="$1" input="$2" candidate canonical
    [[ -n "$input" ]] || {
        echo "ERROR: $label must not be empty" >&2
        return 3
    }
    [[ ! "$input" =~ (^|/)\.\.(/|$) ]] || {
        echo "ERROR: $label must not contain '..' traversal: $input" >&2
        return 3
    }
    if [[ "$input" == /* ]]; then
        candidate="$input"
    else
        candidate="${ROOT}/${input}"
    fi
    [[ ! -L "$candidate" ]] || {
        echo "ERROR: $label must not overwrite a symlink: $input" >&2
        return 3
    }
    canonical="$(realpath -m "$candidate")" || return 3
    [[ "$canonical" == "$evidence_root"/* ]] || {
        echo "ERROR: $label must resolve below repository soak-artifacts/: $input" >&2
        return 3
    }
    printf '%s\n' "$canonical"
}

artifacts_dir="$(canonical_evidence_path SOAK_ARTIFACTS_DIR "$artifacts_dir_input")" || exit $?
readonly artifacts_dir
readonly artifacts_dir_relative="${artifacts_dir#"$ROOT/"}"
suite_manifest=""
suite_manifest_relative=""
if [[ -n "$suite_manifest_input" ]]; then
    suite_manifest="$(canonical_evidence_path SOAK_SUITE_MANIFEST "$suite_manifest_input")" || exit $?
    suite_manifest_relative="${suite_manifest#"$ROOT/"}"
    [[ "$suite_manifest" != "$artifacts_dir"/* ]] || {
        echo "ERROR: SOAK_SUITE_MANIFEST must be shared outside the per-run artifact directory" >&2
        exit 3
    }
    [[ ! -e "${suite_manifest}.next" && ! -L "${suite_manifest}.next" ]] || {
        echo "ERROR: unsafe stale suite-manifest overwrite target exists: ${suite_manifest}.next" >&2
        exit 3
    }
    [[ ! -e "$suite_manifest" || -f "$suite_manifest" ]] || {
        echo "ERROR: SOAK_SUITE_MANIFEST exists but is not a regular file" >&2
        exit 3
    }
fi
[[ ! -e "$artifacts_dir" || -d "$artifacts_dir" ]] || {
    echo "ERROR: SOAK_ARTIFACTS_DIR exists but is not a directory" >&2
    exit 3
}
if [[ -d "$artifacts_dir" && -n "$(ls -A "$artifacts_dir")" ]]; then
    echo "ERROR: SOAK_ARTIFACTS_DIR already contains files: $artifacts_dir_relative" >&2
    exit 3
fi
if $dry_run; then
    printf 'PASS: profile=%s project=%s authMode=Local inventorySkew=%s volatilitySpread=%s feedLossPolicy=%s maxReferenceAge=%ss strictRefreshInterval=%ss strictRefreshMargin=%ss artifacts=%s manifest=%s\n' \
        "$profile" "$project_name" "$MM_SOAK_INVENTORY_SKEW_ENABLED" \
        "$MM_SOAK_VOLATILITY_SPREAD_ENABLED" "$MM_SOAK_FEED_LOSS_POLICY" \
        "$max_reference_age_seconds" "$strict_refresh_interval_seconds" "$strict_refresh_margin_seconds" \
        "$artifacts_dir_relative" "${suite_manifest_relative:-none}"
    exit 0
fi

[[ -n "$trading_password" ]] || {
    echo "ERROR: set SOAK_TRADING_PASSWORD to the plaintext matching the configured seed hash/salt." >&2
    exit 2
}
readonly base_url="http://127.0.0.1:${TRADING_HOST_PORT}"
readonly prometheus_url="http://127.0.0.1:${PROMETHEUS_PORT}"
mkdir -p "$artifacts_dir"
if [[ -n "$suite_manifest" ]]; then
    mkdir -p "$(dirname "$suite_manifest")"
fi
readonly samples_jsonl="${artifacts_dir}/samples.jsonl"
readonly samples_csv="${artifacts_dir}/samples.csv"
readonly metric_presence_jsonl="${artifacts_dir}/metric-presence.jsonl"
readonly metric_presence_csv="${artifacts_dir}/metric-presence.csv"
readonly workload_csv="${artifacts_dir}/workload.csv"
readonly strict_refresh_jsonl="${artifacts_dir}/strict-refreshes.jsonl"
readonly strict_refresh_csv="${artifacts_dir}/strict-refreshes.csv"
readonly runtime_jsonl="${artifacts_dir}/runtime.jsonl"
readonly runtime_csv="${artifacts_dir}/runtime.csv"
readonly runtime_events_jsonl="${artifacts_dir}/runtime-events.jsonl"
readonly checks_tsv="${artifacts_dir}/checks.tsv"
readonly cleanup_errors_file="${artifacts_dir}/cleanup-errors.json"
readonly submit_failures_jsonl="${artifacts_dir}/submit-failures.jsonl"
printf 'timestamp_utc,phase,accountingPeriodPresent,accountingPeriodStartedAtUtc,metric,symbol,side,reason,available,source,present,value\n' >"$samples_csv"
printf 'timestamp_utc,phase,metric,symbol,required,present,seriesCount,value\n' >"$metric_presence_csv"
printf 'timestamp_utc,phase,symbol,user,side,clOrdId,fillLatencySeconds\n' >"$workload_csv"
printf 'timestamp_utc,segment,phase,symbol,direction,seller,buyer,priceSource,quantity,price,sellClOrdId,buyClOrdId,fillLatencySeconds\n' \
    >"$strict_refresh_csv"
printf 'timestamp_utc,phase,service,containerId,imageId,startedAtUtc,restartCount,status\n' >"$runtime_csv"
: >"$samples_jsonl"
: >"$metric_presence_jsonl"
: >"$strict_refresh_jsonl"
: >"$runtime_jsonl"
: >"$runtime_events_jsonl"
: >"$checks_tsv"
: >"$submit_failures_jsonl"
printf '%s\n' "$sanitized_rendered_config" >"${artifacts_dir}/rendered-config.json"
jq -n \
    --argjson exportedNamesUnset true \
    --argjson initialChildEnvironmentClean true \
    '{
      exportedNamesUnset: $exportedNamesUnset,
      initialChildEnvironmentClean: $initialChildEnvironmentClean,
      curlEnvironmentCheckedPerRequest: true,
      dockerEventsEnvironmentClean: false,
      childEnvironmentCheckCount: 1
    }' >"${artifacts_dir}/credential-environment.json"

log() {
    printf '%s [mm-soak] %s\n' "$(date -u +%Y-%m-%dT%H:%M:%SZ)" "$*" >&2
}

capture_logs() {
    $stack_started || return 0
    "${compose[@]}" logs --no-color --timestamps \
        matching-platform marketdata trading-host market-maker-bot otel-collector prometheus \
        >"${artifacts_dir}/compose.log" 2>&1
}

stop_runtime_event_monitor_for_cleanup() {
    [[ -n "${runtime_event_monitor_pid:-}" ]] || return 0
    local monitor_pid="$runtime_event_monitor_pid"
    runtime_event_monitor_pid=""
    if soak_stop_event_monitor "$monitor_pid"; then
        return 0
    fi
    runtime_event_monitor_stable=false
    runtime_stable=false
    log "ERROR: Docker runtime event monitor exited before intentional shutdown"
    return 1
}

teardown_stack_for_cleanup() {
    $stack_started || return 0
    $keep_stack && return 0
    "${compose[@]}" down -v --remove-orphans >"${artifacts_dir}/cleanup-compose-down.log" 2>&1
}

register_accepted_suite_run() {
    local suite_manifest_next
    [[ -n "${suite_manifest:-}" ]] || return 0
    [[ -f "${artifacts_dir}/summary.json" ]] || return 0
    jq -e '.passed and .acceptanceEligible' >/dev/null "${artifacts_dir}/summary.json" || return 0
    suite_manifest_next="${suite_manifest}.next"
    jq \
        --arg profile "$profile" \
        --arg artifactDirectory "$artifacts_dir_relative" \
        --slurpfile summary "${artifacts_dir}/summary.json" '
          .runs[$profile] = {
            runId: $summary[0].runId,
            artifactDirectory: $artifactDirectory,
            gitSha: $summary[0].gitSha,
            runtimeImageIds: $summary[0].images,
            settings: $summary[0].settings,
            workload: $summary[0].workload,
            profileConfiguration: $summary[0].profileConfiguration,
            execution: $summary[0].execution,
            accountingPeriodStartedAtUtc: $summary[0].accountingPeriodStartedAtUtc,
            acceptanceEligible: true,
            passed: true,
            finishedAtUtc: $summary[0].finishedAtUtc
          } |
          .updatedAtUtc = (now | todateiso8601) |
          .suiteAcceptanceEligible = (
            [.expectedProfiles[] as $expectedProfile |
              .runs[$expectedProfile].acceptanceEligible == true] | all
          )
        ' "$suite_manifest" >"$suite_manifest_next" &&
        mv "$suite_manifest_next" "$suite_manifest"
}

write_cleanup_evidence() {
    local cleanup_json
    cleanup_json="$(jq -cn \
        --argjson originalExitCode "$original_status" \
        --argjson cleanupExitCode "$cleanup_status" \
        --slurpfile errors "$cleanup_errors_file" \
        '{
          passed: ($cleanupExitCode == 0),
          originalExitCode: $originalExitCode,
          cleanupExitCode: $cleanupExitCode,
          errors: $errors[0]
        }')" || return 1
    printf '%s\n' "$cleanup_json" >"${artifacts_dir}/cleanup.json" || return 1
    if [[ -f "${artifacts_dir}/summary.json" ]]; then
        soak_apply_cleanup_to_summary "${artifacts_dir}/summary.json" "$cleanup_json" ||
            return 1
    fi
}

cleanup() {
    original_status=$?
    cleanup_status=0
    local final_status evidence_status registration_status
    trap - EXIT
    if ! soak_run_cleanup_steps "$cleanup_errors_file" \
        stop-runtime-event-monitor stop_runtime_event_monitor_for_cleanup \
        capture-compose-logs capture_logs \
        compose-down teardown_stack_for_cleanup; then
        cleanup_status=1
    fi

    if write_cleanup_evidence; then
        :
    else
        evidence_status=$?
        cleanup_status=1
        if ! soak_append_cleanup_error \
            "$cleanup_errors_file" record-cleanup-evidence "$evidence_status"; then
            log "ERROR: failed to append cleanup-evidence recording error"
        fi
        if ! write_cleanup_evidence; then
            log "ERROR: failed to record failed cleanup status in evidence"
        fi
    fi

    if (( original_status == 0 && cleanup_status == 0 )); then
        if register_accepted_suite_run; then
            :
        else
            registration_status=$?
            cleanup_status=1
            if ! soak_append_cleanup_error \
                "$cleanup_errors_file" register-suite-manifest "$registration_status"; then
                log "ERROR: failed to append suite-registration cleanup error"
            fi
            if ! write_cleanup_evidence; then
                log "ERROR: failed to record suite-registration failure in evidence"
            fi
        fi
    fi

    final_status="$(soak_cleanup_exit_status "$original_status" "$cleanup_status")"
    if (( original_status != 0 )); then
        log "FAILED (exit $original_status); evidence retained in $artifacts_dir_relative"
    elif (( cleanup_status != 0 )); then
        log "FAILED during cleanup; evidence retained in $artifacts_dir_relative"
    elif $keep_stack; then
        log "PASS; stack retained for project $project_name and evidence is in $artifacts_dir_relative"
    else
        log "PASS; isolated stack removed and evidence is in $artifacts_dir_relative"
    fi
    if (( cleanup_status != 0 )); then
        log "cleanup errors: $(jq -c . "$cleanup_errors_file")"
    fi
    exit "$final_status"
}
trap cleanup EXIT

git_sha="$(git rev-parse HEAD)"
git_status_porcelain="$(git status --porcelain)"
git_clean=true
git_dirty=false
if [[ -n "$git_status_porcelain" ]]; then
    git_clean=false
    git_dirty=true
fi
timing_eligible=false
if (( warmup_seconds >= 600 && duration_seconds >= 7200 && sample_interval_seconds <= 30 )); then
    timing_eligible=true
fi
if [[ -n "$suite_manifest" ]]; then
    $timing_eligible || {
        log "ERROR: SOAK_SUITE_MANIFEST is only valid with acceptance timing (warmup>=600, duration>=7200, sample<=30)"
        exit 3
    }
    $git_clean || {
        log "ERROR: acceptance evidence requires a clean checkout before image build"
        exit 3
    }
    suite_preflight_run_count=0
    if [[ -f "$suite_manifest" ]]; then
        suite_preflight_run_count="$(jq -er '.runs | length' "$suite_manifest")" || {
            log "ERROR: existing suite manifest has no valid runs object"
            exit 3
        }
    fi
    if (( suite_preflight_run_count == 0 )) && ! $build_images; then
        log "ERROR: the first acceptance-eligible suite run must build images from the clean checkout; --no-build is not permitted"
        exit 3
    fi
fi
build_mode="preexisting-images-smoke"
built_from_git_sha=""
if $build_images; then
    build_mode="compose-build-smoke"
    if $git_clean; then
        built_from_git_sha="$git_sha"
    fi
fi
if [[ -n "$suite_manifest" ]]; then
    if $build_images; then
        build_mode="clean-checkout-compose-build"
    else
        build_mode="pinned-runtime-images"
    fi
fi
configured_symbols_json="$(printf '%s\n' "${configured_symbols[@]}" | jq -R . | jq -s .)"
jq -n \
    --arg schemaVersion "8" \
    --arg runId "$run_id" \
    --arg profile "$profile" \
    --arg projectName "$project_name" \
    --arg gitSha "$git_sha" \
    --argjson gitClean "$git_clean" \
    --argjson gitDirty "$git_dirty" \
    --arg symbol "$symbol" \
    --arg inventorySkew "$MM_SOAK_INVENTORY_SKEW_ENABLED" \
    --arg volatilitySpread "$MM_SOAK_VOLATILITY_SPREAD_ENABLED" \
    --arg feedLossPolicy "$MM_SOAK_FEED_LOSS_POLICY" \
    --argjson warmupSeconds "$warmup_seconds" \
    --argjson durationSeconds "$duration_seconds" \
    --argjson sampleIntervalSeconds "$sample_interval_seconds" \
    --argjson workloadIntervalSeconds "$workload_interval_seconds" \
    --argjson outageSeconds "$outage_seconds" \
    --argjson recoveryTimeoutSeconds "$recovery_timeout_seconds" \
    --argjson recoveryCrossAttempts "$recovery_cross_attempts" \
    --argjson recoveryCrossIntervalSeconds "$recovery_cross_interval_seconds" \
    --argjson metricExportIntervalMilliseconds "$metric_export_interval_ms" \
    --argjson prometheusScrapeIntervalSeconds "$prometheus_scrape_interval_seconds" \
    --argjson fullTelemetryCycleSeconds "$full_telemetry_cycle_seconds" \
    --arg maxReferenceAge "$max_reference_age" \
    --argjson maxReferenceAgeSeconds "$max_reference_age_seconds" \
    --argjson strictRefreshIntervalSeconds "$strict_refresh_interval_seconds" \
    --argjson strictRefreshMarginSeconds "$strict_refresh_margin_seconds" \
    --argjson strictRefreshCycleBudgetSeconds "$strict_refresh_cycle_budget_seconds" \
    --argjson preOutageStabilizationCycles "$pre_outage_stabilization_cycles" \
    --argjson preOutageStabilizationIntervalSeconds "$pre_outage_stabilization_interval_seconds" \
    --argjson preOutageStabilizationTimeoutSeconds "$pre_outage_stabilization_timeout_seconds" \
    --argjson reconnectStaleHoldSeconds "$reconnect_stale_hold_seconds" \
    --argjson inventoryBiasLots "$inventory_bias_lots" \
    --argjson fillTimeoutSeconds "$fill_timeout_seconds" \
    --argjson quantity "$quantity" \
    --argjson marketableBuyPrice "$marketable_buy_price" \
    --argjson marketableSellPrice "$marketable_sell_price" \
    --argjson referenceCrossPrice "$reference_cross_price" \
    --argjson depositAmount "$deposit_amount" \
    --argjson counterpartyDepositAmount "$counterparty_deposit_amount" \
    --argjson configuredSymbols "$configured_symbols_json" \
    --argjson configuredInstruments "$configured_instruments_json" \
    --argjson identities "$identity_mappings_json" \
    --argjson timingEligible "$timing_eligible" \
    --argjson buildImages "$build_images" \
    --arg buildMode "$build_mode" \
    --arg builtFromGitSha "$built_from_git_sha" \
    --argjson keepStack "$keep_stack" \
    --argjson withGrafana "$with_grafana" \
    --arg suiteManifest "$suite_manifest_relative" \
    '{
      schemaVersion: $schemaVersion,
      runId: $runId,
      profile: $profile,
      projectName: $projectName,
      gitSha: $gitSha,
      gitClean: $gitClean,
      gitDirty: $gitDirty,
      symbol: $symbol,
      configuredSymbols: $configuredSymbols,
      startedAtUtc: (now | todateiso8601),
      settings: {
        warmupSeconds: $warmupSeconds,
        durationSeconds: $durationSeconds,
        sampleIntervalSeconds: $sampleIntervalSeconds,
        workloadIntervalSeconds: $workloadIntervalSeconds,
        fillTimeoutSeconds: $fillTimeoutSeconds,
        outageSeconds: $outageSeconds,
        recoveryTimeoutSeconds: $recoveryTimeoutSeconds,
        recoveryCrossAttempts: $recoveryCrossAttempts,
        recoveryCrossIntervalSeconds: $recoveryCrossIntervalSeconds,
        metricExportIntervalMilliseconds: $metricExportIntervalMilliseconds,
        prometheusScrapeIntervalSeconds: $prometheusScrapeIntervalSeconds,
        fullTelemetryCycleSeconds: $fullTelemetryCycleSeconds,
        maxReferenceAge: $maxReferenceAge,
        maxReferenceAgeSeconds: $maxReferenceAgeSeconds,
        strictRefreshIntervalSeconds: $strictRefreshIntervalSeconds,
        strictRefreshMarginSeconds: $strictRefreshMarginSeconds,
        strictRefreshCycleBudgetSeconds: $strictRefreshCycleBudgetSeconds,
        preOutageStabilizationCycles: $preOutageStabilizationCycles,
        preOutageStabilizationIntervalSeconds: $preOutageStabilizationIntervalSeconds,
        preOutageStabilizationTimeoutSeconds: $preOutageStabilizationTimeoutSeconds,
        reconnectStaleHoldSeconds: $reconnectStaleHoldSeconds,
        withGrafana: $withGrafana
      },
      workload: {
        identities: $identities,
        primaryTarget: {
          symbol: $symbol,
          quantity: $quantity,
          marketableBuyPrice: $marketableBuyPrice,
          marketableSellPrice: $marketableSellPrice,
          intervalSeconds: $workloadIntervalSeconds,
          evidenceArtifact: "workload.csv"
        },
        strictRefresh: {
          configuredInstruments: $configuredInstruments,
          referenceCrossPrice: $referenceCrossPrice,
          intervalSeconds: $strictRefreshIntervalSeconds,
          marginSeconds: $strictRefreshMarginSeconds,
          cycleBudgetSeconds: $strictRefreshCycleBudgetSeconds,
          maxReferenceAge: $maxReferenceAge,
          maxReferenceAgeSeconds: $maxReferenceAgeSeconds,
          evidenceArtifacts: ["strict-refreshes.csv","strict-refreshes.jsonl","strict-freshness.json"]
        },
        inventoryBiasLots: $inventoryBiasLots,
        deposits: {
          tradingUser: $depositAmount,
          counterpartyUser: $counterpartyDepositAmount
        },
        accountingBootstrapRoundTrip: true,
        recoveryCrosses: $configuredInstruments
      },
      profileConfiguration: {
        inventorySkewEnabled: ($inventorySkew == "true"),
        volatilitySpreadEnabled: ($volatilitySpread == "true"),
        feedLossPolicy: $feedLossPolicy,
        initialReferenceCrosses: ($feedLossPolicy == "PauseAndCancel")
      },
      renderedConfigArtifact: "rendered-config.json",
      execution: {
        buildImages: $buildImages,
        buildMode: $buildMode,
        builtFromGitSha: (
          if $builtFromGitSha == "" then null else $builtFromGitSha end
        ),
        keepStack: $keepStack
      },
      suiteManifest: (if $suiteManifest == "" then null else $suiteManifest end),
      timingEligible: $timingEligible,
      acceptanceEligible: false,
      evidenceClass: "smoke",
      eligibilityReasons: ["runtime images and suite compatibility have not been verified"]
    }' >"${artifacts_dir}/run.json"

if ! "${compose[@]}" down -v --remove-orphans >"${artifacts_dir}/pre-run-compose-down.log" 2>&1; then
    log "ERROR: pre-run isolation teardown failed; refusing to start project '$project_name'"
    exit 1
fi
if $build_images; then
    log "building trading-host, market-maker-bot, and local alert receiver"
    "${compose[@]}" build trading-host market-maker-bot alert-receiver
fi

readonly critical_services=(
    trading-host
    matching-platform
    marketdata
    market-maker-bot
    otel-collector
    prometheus
)
critical_services_json="$(printf '%s\n' "${critical_services[@]}" | jq -R . | jq -s .)"
services=(trading-host market-maker-bot prometheus)
if $with_grafana; then
    services+=(grafana)
fi
log "starting isolated real stack"
stack_started=true
"${compose[@]}" up -d --no-build --wait "${services[@]}"

capture_runtime_images() {
    local runtime_images='[]' container_id inspect_json service configured_image image_id started_at
    local restart_count status repo_digests row
    local container_ids=()
    mapfile -t container_ids < <("${compose[@]}" ps --all -q)
    ((${#container_ids[@]} > 0)) || {
        log "ERROR: Compose reported no runtime containers"
        return 1
    }
    for container_id in "${container_ids[@]}"; do
        inspect_json="$(docker inspect "$container_id" | jq '.[0]')"
        service="$(jq -er '.Config.Labels["com.docker.compose.service"]' <<<"$inspect_json")"
        configured_image="$(jq -er '.Config.Image' <<<"$inspect_json")"
        image_id="$(jq -er '.Image' <<<"$inspect_json")"
        started_at="$(jq -er '.State.StartedAt' <<<"$inspect_json")"
        restart_count="$(jq -er '.RestartCount' <<<"$inspect_json")"
        status="$(jq -er '.State.Status' <<<"$inspect_json")"
        repo_digests="$(docker image inspect "$image_id" | jq '.[0].RepoDigests // []')"
        row="$(jq -cn \
            --arg service "$service" \
            --arg containerId "$container_id" \
            --arg configuredImage "$configured_image" \
            --arg imageId "$image_id" \
            --arg startedAtUtc "$started_at" \
            --argjson restartCount "$restart_count" \
            --arg status "$status" \
            --argjson repoDigests "$repo_digests" \
            '{
              service: $service,
              containerId: $containerId,
              configuredImage: $configuredImage,
              imageId: $imageId,
              startedAtUtc: $startedAtUtc,
              restartCount: $restartCount,
              status: $status,
              repoDigests: $repoDigests
            }')"
        runtime_images="$(jq -cn --argjson rows "$runtime_images" --argjson row "$row" '$rows + [$row]')"
    done
    jq -e --argjson critical "$critical_services_json" '
      length > 0 and
      (map(.service) | unique | length) == length and
      all(.imageId | test("^sha256:[0-9a-f]{64}$")) and
      (($critical - map(.service)) | length == 0) and
      all(.[] | select(.service as $service | $critical | index($service));
        .restartCount == 0 and .status == "running")
    ' >/dev/null <<<"$runtime_images" || {
        log "ERROR: critical runtime evidence is incomplete, unhealthy, restarted, or lacks immutable image IDs"
        return 1
    }
    jq 'sort_by(.service)' <<<"$runtime_images" >"${artifacts_dir}/runtime-images.json"
}

capture_runtime_images
runtime_image_ids="$(jq 'map({key: .service, value: .imageId}) | from_entries' \
    "${artifacts_dir}/runtime-images.json")"

jq \
    --argjson images "$runtime_image_ids" \
    '.images = $images | .runtimeImagesArtifact = "runtime-images.json"' \
    "${artifacts_dir}/run.json" >"${artifacts_dir}/run.json.next"
mv "${artifacts_dir}/run.json.next" "${artifacts_dir}/run.json"

runtime_stable=true
runtime_violation_logged=false
runtime_event_monitor_stable=true
marketdata_transition_phase=steady
marketdata_after_started_at=""
capture_runtime_state() {
    local phase="$1" timestamp service expected_id expected_image expected_started current_id inspect_json
    local restart_count status started_at actual_image service_stable row runtime_baseline
    timestamp="$(date -u +%Y-%m-%dT%H:%M:%SZ)" || return 1
    runtime_baseline="$(jq -r --argjson critical "$critical_services_json" '
        .[] |
        select(.service as $service | $critical | index($service)) |
        [.service,.containerId,.imageId,.startedAtUtc] | @tsv' \
        "${artifacts_dir}/runtime-images.json")" || return 1
    [[ -n "$runtime_baseline" ]] || return 1
    while IFS=$'\t' read -r service expected_id expected_image expected_started; do
        current_id="$("${compose[@]}" ps --all -q "$service")" || return 1
        restart_count=-1
        status=missing
        started_at=missing
        actual_image=missing
        service_stable=true
        if [[ "$current_id" == "$expected_id" ]] &&
            inspect_json="$(docker inspect "$current_id" 2>/dev/null | jq '.[0]')"; then
            restart_count="$(jq -er '.RestartCount' <<<"$inspect_json")" || return 1
            status="$(jq -er '.State.Status' <<<"$inspect_json")" || return 1
            started_at="$(jq -er '.State.StartedAt' <<<"$inspect_json")" || return 1
            actual_image="$(jq -er '.Image' <<<"$inspect_json")" || return 1
            [[ "$actual_image" == "$expected_image" ]] || service_stable=false
            (( restart_count == 0 )) || service_stable=false
            if [[ "$profile" == "pause-and-cancel" && "$service" == "marketdata" ]]; then
                case "$marketdata_transition_phase" in
                    steady)
                        [[ "$status" == "running" && "$started_at" == "$expected_started" ]] ||
                            service_stable=false
                        ;;
                    stopped)
                        [[ "$status" == "exited" && "$started_at" == "$expected_started" ]] ||
                            service_stable=false
                        ;;
                    restarted)
                        [[ "$status" == "running" &&
                            -n "$marketdata_after_started_at" &&
                            "$started_at" == "$marketdata_after_started_at" &&
                            "$started_at" != "$expected_started" ]] || service_stable=false
                        ;;
                    *)
                        service_stable=false
                        ;;
                esac
            else
                [[ "$status" == "running" && "$started_at" == "$expected_started" ]] ||
                    service_stable=false
            fi
        else
            service_stable=false
            status=replaced-or-missing
        fi
        if ! $service_stable; then
            runtime_stable=false
        fi
        row="$(jq -cn \
            --arg timestamp "$timestamp" \
            --arg phase "$phase" \
            --arg service "$service" \
            --arg containerId "${current_id:-missing}" \
            --arg imageId "$actual_image" \
            --arg startedAtUtc "$started_at" \
            --argjson restartCount "$restart_count" \
            --arg status "$status" \
            '{
              timestampUtc: $timestamp,
              phase: $phase,
              service: $service,
              containerId: $containerId,
              imageId: $imageId,
              startedAtUtc: $startedAtUtc,
              restartCount: $restartCount,
              status: $status
            }')" || return 1
        printf '%s\n' "$row" >>"$runtime_jsonl" || return 1
        jq -r '[.timestampUtc,.phase,.service,.containerId,.imageId,.startedAtUtc,.restartCount,.status] | @csv' \
            <<<"$row" >>"$runtime_csv" || return 1
    done <<<"$runtime_baseline"
    if [[ -n "${runtime_event_monitor_pid:-}" ]] &&
        ! kill -0 "$runtime_event_monitor_pid" 2>/dev/null; then
        runtime_event_monitor_stable=false
        runtime_stable=false
    fi
    if ! $runtime_stable && ! $runtime_violation_logged; then
        log "ERROR: runtime container identity, image ID, or restart count changed; this run cannot pass"
        runtime_violation_logged=true
    fi
}

capture_service_runtime_snapshot() {
    local service="$1" phase="$2" container_id inspect_json timestamp image_id started_at
    local restart_count status
    container_id="$("${compose[@]}" ps --all -q "$service")" || return 1
    [[ -n "$container_id" ]] || {
        log "ERROR: critical service '$service' has no container while capturing '$phase'"
        return 1
    }
    inspect_json="$(docker inspect "$container_id" | jq -e '.[0]')" || return 1
    timestamp="$(date -u +%Y-%m-%dT%H:%M:%SZ)" || return 1
    image_id="$(jq -er '.Image' <<<"$inspect_json")" || return 1
    started_at="$(jq -er '.State.StartedAt' <<<"$inspect_json")" || return 1
    restart_count="$(jq -er '.RestartCount' <<<"$inspect_json")" || return 1
    status="$(jq -er '.State.Status' <<<"$inspect_json")" || return 1
    jq -cn \
        --arg timestampUtc "$timestamp" \
        --arg phase "$phase" \
        --arg service "$service" \
        --arg containerId "$container_id" \
        --arg imageId "$image_id" \
        --arg startedAtUtc "$started_at" \
        --argjson restartCount "$restart_count" \
        --arg status "$status" \
        '{
          timestampUtc: $timestampUtc,
          phase: $phase,
          service: $service,
          containerId: $containerId,
          imageId: $imageId,
          startedAtUtc: $startedAtUtc,
          restartCount: $restartCount,
          status: $status
        }' || return 1
}

capture_runtime_state initial
if ! soak_child_environment_is_secret_free \
    trading_password counterparty_password trading_token counterparty_token admin_token; then
    log "ERROR: refusing to launch docker events with credentials visible in its child environment"
    exit 1
fi
credential_environment_check_count=$((credential_environment_check_count + 1))
docker events \
    --filter type=container \
    --filter "label=com.docker.compose.project=$project_name" \
    --format '{{json .}}' >>"$runtime_events_jsonl" &
runtime_event_monitor_pid=$!
sleep 1
kill -0 "$runtime_event_monitor_pid" 2>/dev/null || {
    log "ERROR: Docker runtime event monitor failed to start"
    exit 1
}
runtime_event_environment=""
while IFS= read -r -d '' environment_entry; do
    runtime_event_environment+="${environment_entry}"$'\n'
done <"/proc/${runtime_event_monitor_pid}/environ"
if ! soak_environment_snapshot_is_secret_free runtime_event_environment \
    trading_password counterparty_password trading_token counterparty_token admin_token; then
    log "ERROR: docker events process environment contains credential material"
    exit 1
fi
unset runtime_event_environment environment_entry
jq \
    --argjson checkCount "$credential_environment_check_count" '
      .dockerEventsEnvironmentClean = true |
      .childEnvironmentCheckCount = $checkCount
    ' "${artifacts_dir}/credential-environment.json" \
    >"${artifacts_dir}/credential-environment.json.next"
mv "${artifacts_dir}/credential-environment.json.next" \
    "${artifacts_dir}/credential-environment.json"

runtime_image_binding="$(jq '
  [.[] | {service, configuredImage, imageId, repoDigests}] |
  sort_by(.service)
' "${artifacts_dir}/runtime-images.json")"
compatibility_workload="$(
    soak_suite_compatibility_workload "${artifacts_dir}/run.json"
)"
compatibility_json="$(jq -n \
    --slurpfile run "${artifacts_dir}/run.json" \
    --slurpfile rendered "${artifacts_dir}/rendered-config.json" \
    --slurpfile runtimeImages "${artifacts_dir}/runtime-images.json" \
    --argjson workload "$compatibility_workload" '
    {
      gitSha: $run[0].gitSha,
      settings: $run[0].settings,
      workload: $workload,
      configuredSymbols: $run[0].configuredSymbols,
      commonRenderedConfiguration: {
        auth: $rendered[0].services.tradingHost.auth,
        workloadRisk: $rendered[0].services.tradingHost.workloadRisk,
        sandboxCash: $rendered[0].services.tradingHost.sandboxCash,
        networkIpam: $rendered[0].network.ipam,
        marketMakerBot: (
          $rendered[0].services.marketMakerBot.configuration
          | with_entries(select(
              (.key | test("__InventorySkew__Enabled$|__VolatilitySpread__Enabled$|MarketData__FeedLossPolicy$")) | not
            ))
        )
      },
      runtimeImageIds: (
        $runtimeImages[0] | map({key: .service, value: .imageId}) | from_entries
      )
    }')"
printf '%s\n' "$compatibility_json" >"${artifacts_dir}/compatibility.json"

suite_compatible=false
if [[ -n "$suite_manifest" ]]; then
    suite_id="$(basename "$(dirname "$suite_manifest")")"
    suite_manifest_exists=false
    accepted_run_count=0
    manifest_built_from_git_sha=null
    pinned_runtime_images=null
    if [[ -e "$suite_manifest" ]]; then
        suite_manifest_exists=true
        accepted_run_count="$(jq -er '.runs | length' "$suite_manifest")"
        manifest_built_from_git_sha="$(jq -c '.sourceBinding.builtFromGitSha // null' \
            "$suite_manifest")"
        pinned_runtime_images="$(jq -c '.sourceBinding.runtimeImages // null' "$suite_manifest")"
    fi
    suite_source_binding_report="$(jq -cn \
        --argjson manifestExists "$suite_manifest_exists" \
        --argjson acceptedRunCount "$accepted_run_count" \
        --argjson buildImages "$build_images" \
        --argjson gitClean "$git_clean" \
        --arg gitSha "$git_sha" \
        --argjson manifestBuiltFromGitSha "$manifest_built_from_git_sha" \
        --argjson pinnedRuntimeImages "$pinned_runtime_images" \
        --argjson actualRuntimeImages "$runtime_image_binding" \
        '{
          manifestExists: $manifestExists,
          acceptedRunCount: $acceptedRunCount,
          buildImages: $buildImages,
          gitClean: $gitClean,
          gitSha: $gitSha,
          manifestBuiltFromGitSha: $manifestBuiltFromGitSha,
          pinnedRuntimeImages: $pinnedRuntimeImages,
          actualRuntimeImages: $actualRuntimeImages
        }' | soak_evaluate_suite_source_binding)"
    printf '%s\n' "$suite_source_binding_report" \
        >"${artifacts_dir}/suite-source-binding.json"
    if [[ "$(jq -r '.passed' <<<"$suite_source_binding_report")" != "true" ]]; then
        log "ERROR: acceptance suite source/image binding failed: $(jq -c . <<<"$suite_source_binding_report")"
        exit 1
    fi
    if $suite_manifest_exists && (( accepted_run_count > 0 )); then
        if ! jq -e \
            --argjson compatibility "$compatibility_json" '
              .schemaVersion == "2" and
              .expectedProfiles == ["baseline","inventory-skew","volatility-spread","pause-and-cancel"] and
              .compatibility == $compatibility
            ' >/dev/null "$suite_manifest"; then
            printf '%s\n' "$compatibility_json" >"${artifacts_dir}/compatibility-observed.json"
            log "ERROR: suite compatibility mismatch; compare $suite_manifest_relative with ${artifacts_dir_relative}/compatibility-observed.json"
            exit 1
        fi
        if jq -e --arg profile "$profile" '.runs[$profile] != null' >/dev/null "$suite_manifest"; then
            log "ERROR: suite manifest already contains profile '$profile'; use a new suite"
            exit 3
        fi
    else
        # A manifest can exist with zero accepted runs when a prior attempt
        # for this suite created it but failed/crashed before any profile
        # was accepted (e.g. the run's build produced fresh runtime image
        # IDs, or the run itself failed mid-flight). Nothing is pinned by an
        # accepted run yet, so it is safe to regenerate the compatibility/
        # runtimeImages binding for a fresh attempt instead of treating a
        # differing image ID as a fatal "suite compatibility mismatch" —
        # that used to reject every retry after a failed first build even
        # though the suite had zero accepted evidence to protect.
        if $suite_manifest_exists; then
            log "WARN: suite manifest exists with zero accepted runs; regenerating compatibility/runtimeImages binding for this attempt"
        fi
        jq -n \
            --arg suiteId "$suite_id" \
            --arg builtFromGitSha "$git_sha" \
            --arg buildMode "$build_mode" \
            --arg firstRunProfile "$profile" \
            --argjson runtimeImages "$runtime_image_binding" \
            --argjson compatibility "$compatibility_json" '{
              schemaVersion: "2",
              suiteId: $suiteId,
              createdAtUtc: (now | todateiso8601),
              expectedProfiles: ["baseline","inventory-skew","volatility-spread","pause-and-cancel"],
              sourceBinding: {
                builtFromGitSha: $builtFromGitSha,
                buildMode: $buildMode,
                buildImages: true,
                firstRunProfile: $firstRunProfile,
                builtServices: ["trading-host","market-maker-bot","alert-receiver"],
                runtimeImages: $runtimeImages
              },
              compatibility: $compatibility,
              runs: {},
              suiteAcceptanceEligible: false
            }' >"$suite_manifest"
    fi
    suite_compatible=true
fi

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
    local user="$1" password="$2" no_auth_token="" login_body
    login_body="$(printf '%s\0%s' "$user" "$password" |
        jq -Rs 'split("\u0000") | {username:.[0],password:.[1]}')"
    soak_curl_json_request         POST "${base_url}/api/auth/login" no_auth_token login_body |
        jq -c '{token: .token, expiresAt: .expiresAt}'
}

set_session_token() {
    local token_variable="$1" expiry_variable="$2" user="$3" password="$4"
    local session_json expires_at
    session_json="$(login "$user" "$password")" || return 1
    printf -v "$token_variable" '%s' "$(jq -er '.token' <<<"$session_json")" || return 1
    expires_at="$(jq -er '.expiresAt' <<<"$session_json")" || return 1
    printf -v "$expiry_variable" '%s' "$(date -u -d "$expires_at" +%s)" || return 1
    export -n "$token_variable" "$expiry_variable"
}

refresh_session_token_if_due() {
    local token_variable="$1" expiry_variable="$2" user="$3" password="$4"
    local now expires_at_epoch
    now="$(date -u +%s)" || return 1
    expires_at_epoch="${!expiry_variable:-0}"
    if (( expires_at_epoch > 0 && now + auth_refresh_margin_seconds < expires_at_epoch )); then
        return 0
    fi
    set_session_token "$token_variable" "$expiry_variable" "$user" "$password"
}

authed_json_request_into() {
    local response_variable="$1" method="$2" url="$3" token_variable="$4" expiry_variable="$5" user="$6" password="$7" body_variable="$8"
    local token helper_response
    refresh_session_token_if_due "$token_variable" "$expiry_variable" "$user" "$password" || return 1
    token="${!token_variable}"
    helper_response="$(soak_curl_json_request "$method" "$url" token "$body_variable")" || return 1
    printf -v "$response_variable" '%s' "$helper_response"
}

authed_json_request() {
    local response
    authed_json_request_into response "$@" || return 1
    printf '%s' "$response"
}

authed_json_request_with_status_into() {
    local response_variable="$1" status_variable="$2"
    local method="$3" url="$4" token_variable="$5" expiry_variable="$6"
    local user="$7" password="$8" body_variable="$9"
    local token envelope
    refresh_session_token_if_due \
        "$token_variable" "$expiry_variable" "$user" "$password" || return 1
    token="${!token_variable}"
    envelope="$(soak_curl_json_request_with_status \
        "$method" "$url" token "$body_variable")" || return 1
    soak_curl_status_envelope_into \
        "$response_variable" "$status_variable" "$envelope"
}

set_session_token trading_token trading_token_expires_at "$trading_user" "$trading_password"
set_session_token counterparty_token counterparty_token_expires_at "$counterparty_user" "$counterparty_password"
set_session_token admin_token admin_token_expires_at "$admin_user" "$trading_password"

deposit() {
    local token_variable="$1" expiry_variable="$2" user="$3" password="$4" amount="$5" request_body
    awk -v amount="$amount" 'BEGIN { exit !(amount > 0) }' || return 0
    local response
    request_body="$(jq -cn --argjson amount "$amount" '{amount:$amount}')"
    authed_json_request_into response \
        POST "${base_url}/api/balance/deposit" \
        "$token_variable" "$expiry_variable" "$user" "$password" request_body || return 1
}

deposit trading_token trading_token_expires_at "$trading_user" "$trading_password" "$deposit_amount"
deposit counterparty_token counterparty_token_expires_at "$counterparty_user" "$counterparty_password" "$counterparty_deposit_amount"

primary_price_collar_percent=""
primary_price_collar_absolute=""
empty_request_body=""
primary_firm="$(jq -er '.trading.firm' <<<"$identity_mappings_json")" || exit 1
authed_json_request_into primary_risk_limits \
    GET \
    "${base_url}/api/admin/risk/limits?endClient=${trading_user}&firmId=${primary_firm}&symbol=${symbol}" \
    admin_token admin_token_expires_at "$admin_user" "$trading_password" \
    empty_request_body || exit 1
primary_price_collar_percent="$(jq -r '.limits.priceCollarPercent // empty' \
    <<<"$primary_risk_limits")" || exit 1
primary_price_collar_absolute="$(jq -r '.limits.priceCollarAbsolute // empty' \
    <<<"$primary_risk_limits")" || exit 1

strict_primary_reference_pending=false
strict_primary_reference_baseline=
strict_primary_reference_direction=
last_resolved_primary_reference=
last_resolved_primary_limit=
last_resolved_primary_source=
soak_primary_reference_bootstrap_reset

read_live_refresh_price_into() {
    local output_variable="$1" refresh_symbol="$2" status_variable="${3:-}"
    local empty_request_body="" response live_row updated_epoch_ms now_epoch_ms live_price
    local last_status="missing" last_rejection="missing"
    local started=$SECONDS
    while ((SECONDS - started < max_reference_age_seconds)); do
        authed_json_request_into response \
            GET \
            "${base_url}/api/admin/marketdata/reference-prices?symbols=${refresh_symbol}" \
            admin_token admin_token_expires_at "$admin_user" "$trading_password" \
            empty_request_body || return 1
        live_row="$(jq -ce \
            --arg symbol "$refresh_symbol" '
              .symbols[] |
              select(.symbol == $symbol) |
              .live |
              select(.price != null and .updatedUtc != null)
            ' <<<"$response" 2>/dev/null || true)"
        if [[ -n "$live_row" ]]; then
            updated_epoch_ms="$(date -u -d "$(jq -r '.updatedUtc' <<<"$live_row")" +%s%3N)" || {
                [[ -z "$status_variable" ]] || printf -v "$status_variable" '%s' "invalid"
                log "ERROR: live reference for '$refresh_symbol' has an invalid updatedUtc"
                return 1
            }
            now_epoch_ms="$(date -u +%s%3N)" || return 1
            if ! soak_reference_timestamp_is_fresh \
                "$updated_epoch_ms" "$now_epoch_ms" "$max_reference_age_seconds"; then
                last_status="stale"
                last_rejection="stale updatedUtc=$(jq -r '.updatedUtc' <<<"$live_row") ageMs=$((now_epoch_ms - updated_epoch_ms))"
                sleep 0.25
                continue
            fi
            live_price="$(jq -er '.price' <<<"$live_row")" || return 1
            printf -v "$output_variable" '%s' "$live_price"
            [[ -z "$status_variable" ]] || printf -v "$status_variable" '%s' "fresh"
            if [[ "$refresh_symbol" == "$symbol" ]]; then
                soak_primary_reference_mark_live_observed
            fi
            return
        fi
        sleep 0.25
    done
    [[ -z "$status_variable" ]] || printf -v "$status_variable" '%s' "$last_status"
    log "ERROR: no fresh live reference is available for '$refresh_symbol' within ${max_reference_age_seconds}s (${last_rejection})"
    return 1
}

resolve_primary_order_price_into() {
    local output_variable="$1" side="$2" configured_price="$3" phase="$4"
    local live_reference live_reference_status tick_size spread_ticks max_skew_ticks
    local max_volatility_additional_spread_ticks resolved_price

    if ! read_live_refresh_price_into \
        live_reference "$symbol" live_reference_status; then
        if soak_primary_reference_fallback_allowed "$live_reference_status"; then
            log "WARN: no raw live reference has arrived for ${phase}; using configured bootstrap ${side,,} price ${configured_price}"
            last_resolved_primary_reference=
            last_resolved_primary_limit="$configured_price"
            last_resolved_primary_source=configured-bootstrap
            printf -v "$output_variable" '%s' "$configured_price"
            return 0
        fi
        log "ERROR: refusing to derive the ${side,,} price for ${phase} from a ${live_reference_status} raw live reference"
        return 1
    fi

    tick_size="$(jq -er \
        --arg symbol "$symbol" \
        '.[] | select(.symbol == $symbol) | .tickSize' \
        <<<"$configured_instruments_json")" || return 1
    spread_ticks="$(jq -er \
        --arg symbol "$symbol" \
        '.[] | select(.symbol == $symbol) | .spreadTicks' \
        <<<"$configured_instruments_json")" || return 1
    max_skew_ticks="$(jq -er \
        --arg symbol "$symbol" \
        '.[] | select(.symbol == $symbol) | .maxSkewTicks' \
        <<<"$configured_instruments_json")" || return 1
    max_volatility_additional_spread_ticks="$(jq -er \
        --arg symbol "$symbol" \
        '.[] | select(.symbol == $symbol) | .maxVolatilityAdditionalSpreadTicks' \
        <<<"$configured_instruments_json")" || return 1

    resolved_price="$(soak_resolve_marketable_limit \
        "$side" \
        "$live_reference" \
        "$tick_size" \
        "$spread_ticks" \
        "$max_skew_ticks" \
        "$max_volatility_additional_spread_ticks" \
        "$marketable_price_extra_ticks" \
        "$primary_price_collar_percent" \
        "$primary_price_collar_absolute")" || {
        log "ERROR: the fresh ${side,,} marketable limit is outside the configured price collar for ${phase} (reference=${live_reference}, configuredHalfSpreadTicks=${spread_ticks}, maxSkewTicks=${max_skew_ticks}, maxVolatilityAdditionalHalfSpreadTicks=${max_volatility_additional_spread_ticks}, crossingExtraTicks=${marketable_price_extra_ticks}, collarPercent=${primary_price_collar_percent:-unset}, collarAbsolute=${primary_price_collar_absolute:-unset})"
        return 1
    }

    log "derived fresh ${side,,} limit ${resolved_price} for ${phase} from liveReference=${live_reference}, configuredHalfSpreadTicks=${spread_ticks}, maxSkewTicks=${max_skew_ticks}, maxVolatilityAdditionalHalfSpreadTicks=${max_volatility_additional_spread_ticks}, crossingExtraTicks=${marketable_price_extra_ticks}, collarPercent=${primary_price_collar_percent:-unset}, collarAbsolute=${primary_price_collar_absolute:-unset}, unusedFallback=${configured_price}"

    last_resolved_primary_reference="$live_reference"
    last_resolved_primary_limit="$resolved_price"
    last_resolved_primary_source=raw-live
    printf -v "$output_variable" '%s' "$resolved_price"
}

resolve_live_refresh_price_into() {
    local output_variable="$1" refresh_symbol="$2" started=$SECONDS price tick_size wait_budget
    wait_budget="$max_reference_age_seconds"
    if [[ "$refresh_symbol" == "$symbol" ]] &&
        $strict_primary_reference_pending; then
        wait_budget="$full_telemetry_cycle_seconds"
    fi
    while ((SECONDS - started < wait_budget)); do
        read_live_refresh_price_into price "$refresh_symbol" || return 1
        if [[ "$refresh_symbol" != "$symbol" ]] ||
            ! $strict_primary_reference_pending ||
            awk -v actual="$price" \
                -v baseline="$strict_primary_reference_baseline" \
                -v direction="$strict_primary_reference_direction" '
                  BEGIN {
                    if (direction == "Buy")
                      exit !(actual > baseline)
                    if (direction == "Sell")
                      exit !(actual < baseline)
                    exit 1
                  }
                '; then
            if [[ "$refresh_symbol" == "$symbol" ]] &&
                $strict_primary_reference_pending; then
                tick_size="$(jq -er \
                    --arg symbol "$refresh_symbol" \
                    '.[] | select(.symbol == $symbol) | .tickSize' \
                    <<<"$configured_instruments_json")" || return 1
                price="$(soak_reference_transition_interior_price \
                    "$price" "$strict_primary_reference_baseline" "$tick_size")" ||
                    return 1
            fi
            printf -v "$output_variable" '%s' "$price"
            return 0
        fi
        sleep 0.25
    done
    if [[ "$refresh_symbol" == "$symbol" ]] &&
        $strict_primary_reference_pending; then
        log "WARN: $refresh_symbol primary $strict_primary_reference_direction fill did not advance the live reference; using an interior-tick maintenance price after the full drain window"
        read_live_refresh_price_into price "$refresh_symbol" || return 1
        tick_size="$(jq -er \
            --arg symbol "$refresh_symbol" \
            '.[] | select(.symbol == $symbol) | .tickSize' \
            <<<"$configured_instruments_json")" || return 1
        price="$(soak_reference_transition_interior_price \
            "$price" "$strict_primary_reference_baseline" "$tick_size")" ||
            return 1
        printf -v "$output_variable" '%s' "$price"
        return 0
    fi
    log "ERROR: no fresh live reference is available for strict refresh symbol '$refresh_symbol'"
    return 1
}

wait_order_filled_into() {
    local output_variable="$1" token_variable="$2" expiry_variable="$3" user="$4" password="$5" clordid="$6" expected_quantity="${7:-$quantity}"
    local started orders status cum empty_request_body=""
    started=$SECONDS
    while (( SECONDS - started < fill_timeout_seconds )); do
        authed_json_request_into orders \
            GET "${base_url}/api/orders" \
            "$token_variable" "$expiry_variable" "$user" "$password" empty_request_body || return 1
        status="$(jq -r --arg id "$clordid"             '.[] | select((.clOrdId|tostring)==$id) | .status'             <<<"$orders" | tail -n1)" || return 1
        cum="$(jq -r --arg id "$clordid"             '.[] | select((.clOrdId|tostring)==$id) | .cumulativeQuantity'             <<<"$orders" | tail -n1)" || return 1
        if [[ "$status" == "Filled" && "$cum" == "$expected_quantity" ]]; then
            printf -v "$output_variable" '%s' "$((SECONDS - started))"
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

expected_workload_sequence=1

record_submit_order_failure() {
    local stage="$1" command_status="$2" http_status="$3" response_body="$4"
    local phase="$5" side="$6" price="$7" clordid="${8:-}"
    local context_json timestamp
    timestamp="$(date -u +%Y-%m-%dT%H:%M:%SZ)" || return 0
    context_json="$(jq -cn \
        --arg profile "$profile" \
        --arg phase "$phase" \
        --arg side "$side" \
        --argjson quantity "$quantity" \
        --arg resolvedReference "$last_resolved_primary_reference" \
        --arg resolvedLimit "$price" \
        --arg resolvedSource "$last_resolved_primary_source" \
        --arg collarPercent "$primary_price_collar_percent" \
        --arg collarAbsolute "$primary_price_collar_absolute" \
        --argjson expectedSequence "$expected_workload_sequence" \
        --arg clOrdId "$clordid" \
        '{
          profile:$profile,
          phase:$phase,
          side:$side,
          quantity:$quantity,
          resolvedReference:(
            if $resolvedReference == "" then null else ($resolvedReference | tonumber) end
          ),
          resolvedLimit:($resolvedLimit | tonumber),
          resolvedSource:$resolvedSource,
          collarPercent:(
            if $collarPercent == "" then null else ($collarPercent | tonumber) end
          ),
          collarAbsolute:(
            if $collarAbsolute == "" then null else ($collarAbsolute | tonumber) end
          ),
          expectedSequence:$expectedSequence,
          clOrdId:(if $clOrdId == "" then null else $clOrdId end)
        }')" || return 0
    if ! soak_append_submit_failure \
        "$submit_failures_jsonl" "$timestamp" "$stage" "$command_status" \
        "$http_status" "$response_body" "$context_json"; then
        log "ERROR: failed to persist submit-order diagnostics"
    fi
}

submit_order() {
    local token_variable="$1" expiry_variable="$2" user="$3" password="$4" side="$5" price="$6" phase="$7"
    local response="" http_status="" clordid="" latency request_body reference_baseline
    local submit_status=0
    if [[ "$profile" == "pause-and-cancel" ]]; then
        read_live_refresh_price_into reference_baseline "$symbol" || return 1
    fi
    request_body="$(jq -cn         --arg symbol "$symbol"         --arg side "$side"         --argjson quantity "$quantity"         --argjson price "$price"         '{symbol:$symbol,side:$side,type:"Limit",quantity:$quantity,price:$price}')" ||
        return 1
    authed_json_request_with_status_into response http_status \
        POST "${base_url}/api/orders" \
        "$token_variable" "$expiry_variable" "$user" "$password" request_body ||
        submit_status=$?
    if ((submit_status != 0)); then
        record_submit_order_failure \
            http-post "$submit_status" "$http_status" "$response" \
            "$phase" "$side" "$price"
        return "$submit_status"
    fi
    [[ "$(jq -r '.status // ""' <<<"$response")" != "Rejected" ]] || {
        record_submit_order_failure \
            application-rejected 1 "$http_status" "$response" \
            "$phase" "$side" "$price" "$(jq -r '.clOrdId // empty' <<<"$response")"
        log "ERROR: $side order was rejected: $response"
        return 1
    }
    clordid="$(jq -er '.clOrdId | tostring' <<<"$response")" || {
        record_submit_order_failure \
            malformed-response 1 "$http_status" "$response" \
            "$phase" "$side" "$price"
        return 1
    }
    if ! wait_order_filled_into \
        latency "$token_variable" "$expiry_variable" "$user" "$password" "$clordid"; then
        record_submit_order_failure \
            fill-wait 1 "$http_status" "$response" \
            "$phase" "$side" "$price" "$clordid"
        return 1
    fi
    printf '%s,%s,%s,%s,%s,%s,%s
'         "$(date -u +%Y-%m-%dT%H:%M:%SZ)" "$phase" "$symbol" "$user" "$side" "$clordid" "$latency"         >>"$workload_csv" || return 1
    expected_workload_sequence=$((10#$clordid + 1))
    if [[ "$profile" == "pause-and-cancel" ]]; then
        if [[ "$phase" == "accounting-bootstrap" ]]; then
            if [[ "$side" == "Sell" ]]; then
                strict_primary_reference_baseline="$reference_baseline"
                strict_primary_reference_direction="$side"
                strict_primary_reference_pending=true
            else
                strict_primary_reference_pending=false
            fi
            strict_refresh_next_due=$((SECONDS + strict_refresh_interval_seconds))
        else
            strict_primary_reference_baseline="$reference_baseline"
            strict_primary_reference_direction="$side"
            strict_primary_reference_pending=true
            strict_refresh_next_due=$SECONDS
        fi
    fi
}

submit_recovery_cross() {
    local cross_symbol="$1" cross_quantity="$2" cross_price="$3"
    local phase="${4:-feed-recovery-cross}" segment="${5:-post-recovery}"
    local price_source="${6:-configured-reference}" direction="${7:-trading-buys}"
    local sell_response buy_response sell_id buy_id latency sell_latency request_body timestamp refresh_row
    local seller buyer sell_token_variable sell_expiry_variable sell_password
    local buy_token_variable buy_expiry_variable buy_password
    if [[ "$direction" == "trading-buys" ]]; then
        seller="$counterparty_user"
        buyer="$trading_user"
        sell_token_variable=counterparty_token
        sell_expiry_variable=counterparty_token_expires_at
        sell_password="$counterparty_password"
        buy_token_variable=trading_token
        buy_expiry_variable=trading_token_expires_at
        buy_password="$trading_password"
    elif [[ "$direction" == "counterparty-buys" ]]; then
        seller="$trading_user"
        buyer="$counterparty_user"
        sell_token_variable=trading_token
        sell_expiry_variable=trading_token_expires_at
        sell_password="$trading_password"
        buy_token_variable=counterparty_token
        buy_expiry_variable=counterparty_token_expires_at
        buy_password="$counterparty_password"
    else
        log "ERROR: unsupported strict refresh direction '$direction'"
        return 1
    fi
    request_body="$(jq -cn         --arg symbol "$cross_symbol"         --argjson quantity "$cross_quantity"         --argjson price "$cross_price"         '{symbol:$symbol,side:"Sell",type:"Limit",quantity:$quantity,price:$price}')" ||
        return 1
    authed_json_request_into sell_response \
        POST "${base_url}/api/orders" \
        "$sell_token_variable" "$sell_expiry_variable" "$seller" "$sell_password" request_body || return 1
    [[ "$(jq -r '.status // ""' <<<"$sell_response")" != "Rejected" ]] || {
        log "ERROR: recovery-cross sell for $cross_symbol was rejected: $sell_response"
        return 1
    }
    sell_id="$(jq -er '.clOrdId | tostring' <<<"$sell_response")" || return 1

    request_body="$(jq -cn         --arg symbol "$cross_symbol"         --argjson quantity "$cross_quantity"         --argjson price "$cross_price"         '{symbol:$symbol,side:"Buy",type:"Limit",quantity:$quantity,price:$price}')" ||
        return 1
    authed_json_request_into buy_response \
        POST "${base_url}/api/orders" \
        "$buy_token_variable" "$buy_expiry_variable" "$buyer" "$buy_password" request_body || return 1
    [[ "$(jq -r '.status // ""' <<<"$buy_response")" != "Rejected" ]] || {
        log "ERROR: recovery-cross buy for $cross_symbol was rejected: $buy_response"
        return 1
    }
    buy_id="$(jq -er '.clOrdId | tostring' <<<"$buy_response")" || return 1
    wait_order_filled_into latency "$buy_token_variable" "$buy_expiry_variable" "$buyer" "$buy_password" "$buy_id" "$cross_quantity" || return 1
    wait_order_filled_into sell_latency "$sell_token_variable" "$sell_expiry_variable" "$seller" "$sell_password" "$sell_id" "$cross_quantity" ||
        return 1
    timestamp="$(date -u +%Y-%m-%dT%H:%M:%SZ)" || return 1
    refresh_row="$(jq -cn         --arg timestampUtc "$timestamp"         --arg segment "$segment"         --arg phase "$phase"         --arg symbol "$cross_symbol"         --arg direction "$direction"         --arg seller "$seller"         --arg buyer "$buyer"         --arg priceSource "$price_source"         --argjson quantity "$cross_quantity"         --argjson price "$cross_price"         --arg sellClOrdId "$sell_id"         --arg buyClOrdId "$buy_id"         --argjson fillLatencySeconds "$latency" '
          {
            timestampUtc: $timestampUtc,
            segment: $segment,
            phase: $phase,
            symbol: $symbol,
            direction: $direction,
            seller: $seller,
            buyer: $buyer,
            priceSource: $priceSource,
            quantity: $quantity,
            price: $price,
            sellClOrdId: $sellClOrdId,
            buyClOrdId: $buyClOrdId,
            fillLatencySeconds: $fillLatencySeconds
          }
        ')" || return 1
    printf '%s
' "$refresh_row" >>"$strict_refresh_jsonl" || return 1
    jq -r '[
      .timestampUtc,.segment,.phase,.symbol,.direction,.seller,.buyer,
      .priceSource,.quantity,.price,
      .sellClOrdId,.buyClOrdId,.fillLatencySeconds
    ] | @csv' <<<"$refresh_row" >>"$strict_refresh_csv" || return 1
}

prom_query() {
    local body
    body="$(curl -sS --max-time 10 --get \
        --data-urlencode "query=$1" \
        "${prometheus_url}/api/v1/query")" || {
        log "ERROR: Prometheus request failed: $1"
        return 1
    }
    if ! jq -e '.status == "success"' >/dev/null <<<"$body"; then
        log "ERROR: Prometheus query failed: $(jq -r '.error // .' <<<"$body" 2>/dev/null || printf 'invalid response')"
        return 1
    fi
    printf '%s\n' "$body" || return 1
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
    bot_orders_ttl_refresh_total
    bot_orders_ttl_refresh_cancel_rejected_total
    bot_orders_ttl_refresh_cancel_submit_failed_total
    bot_orders_quote_restore_rejected_total
    bot_orders_safety_cap_hit_total
    bot_orders_book_driven_requote_total
    bot_orders_book_driven_requote_submit_failed_total
    bot_orders_book_driven_requote_cancel_rejected_total
    bot_market_data_availability_transition_total
    bot_market_data_quote_suppressed_total
    bot_market_data_reference_age_seconds
    bot_market_data_reference_eligible
    bot_market_data_reference_eligible_current
    bot_orders_feed_unavailable_cancel_total
    bot_orders_feed_unavailable_cancel_rejected_total
    bot_orders_feed_unavailable_cancel_submit_failed_total
    bot_orders_feed_unavailable_cancel_retry_total
    bot_orders_cancel_ack_expired_total
)
metric_observation() {
    local query="$1" body
    body="$(prom_query "$query")" || return 1
    soak_metric_sample <<<"$body" || return 1
}

metric_value() {
    local query="$1" sample
    local present value
    sample="$(metric_observation "$query")" || return 1
    present="$(jq -r '.present' <<<"$sample")" || return 1
    value="$(jq -r '.value' <<<"$sample")" || return 1
    if [[ "$present" != "true" || "$value" == "null" ]]; then
        log "ERROR: mandatory Prometheus series is absent or non-numeric: $query"
        return 1
    fi
    printf '%s\n' "$value" || return 1
}

readonly bot_container_id="$(jq -er '.[] | select(.service == "market-maker-bot") | .containerId' \
    "${artifacts_dir}/runtime-images.json")"
accounting_stable=true
accounting_period_started_at_utc=""

current_accounting_period() {
    docker logs --tail 500 "$bot_container_id" 2>&1 |
        sed -n 's/.*accountingPeriodStartedAtUtc=\(.*\) symbol=.*/\1/p' |
        tail -n1
}

wait_accounting_period() {
    local started current
    started=$SECONDS
    while (( SECONDS - started < 60 )); do
        refresh_strict_symbols_if_due accounting-wait-refresh || return 1
        current="$(current_accounting_period)"
        if [[ -n "$current" ]]; then
            accounting_period_started_at_utc="$current"
            return 0
        fi
        sleep 1
    done
    log "ERROR: no [mm-pnl] accountingPeriodStartedAtUtc was observed within 60 seconds"
    return 1
}

wait_service_healthy() {
    local service="$1" timeout="$2" started container_id health
    started=$SECONDS
    container_id="$("${compose[@]}" ps --all -q "$service")" || return 1
    [[ -n "$container_id" ]] || {
        log "ERROR: service '$service' has no Compose container"
        return 1
    }
    while (( SECONDS - started < timeout )); do
        health="$(docker inspect -f '{{if .State.Health}}{{.State.Health.Status}}{{else}}{{.State.Status}}{{end}}' \
            "$container_id" 2>/dev/null || true)"
        [[ "$health" == "healthy" || "$health" == "running" ]] && return 0
        sleep 1
    done
    log "ERROR: service '$service' did not become healthy within ${timeout}s (last=${health:-missing})"
    return 1
}

wait_bot_log_since() {
    local since="$1" pattern="$2" timeout="$3" started
    started=$SECONDS
    while (( SECONDS - started < timeout )); do
        if docker logs --since "$since" "$bot_container_id" 2>&1 | grep -Fq "$pattern"; then
            return 0
        fi
        sleep 1
    done
    log "ERROR: market-maker bot did not log '$pattern' within ${timeout}s"
    return 1
}

collect_all_metrics() {
    local results='[]' response more metric_name
    for metric_name in "${metric_names[@]}"; do
        response="$(prom_query "$metric_name")" || return 1
        more="$(jq -ce '.data.result' <<<"$response")" || return 1
        results="$(jq -cn --argjson current "$results" --argjson more "$more" '$current + $more')" ||
            return 1
    done
    jq -cn --argjson result "$results" '{data:{result:$result}}' || return 1
}

mandatory_metric_requirements() {
    local phase="$1"
    jq -cn \
        --arg profile "$profile" \
        --arg phase "$phase" '
      def symbol($metric): {metric:$metric,scope:"symbol"};
      def target($metric): {metric:$metric,scope:"target"};
      def global($metric): {metric:$metric,scope:"global"};
      [
        symbol("bot_position_net_quantity"),
        symbol("bot_position_average_entry_price"),
        symbol("bot_orders_open"),
        symbol("bot_strategy_configured_half_spread_ticks"),
        symbol("bot_strategy_effective_half_spread_ticks"),
        symbol("bot_pnl_realized"),
        symbol("bot_orders_submitted_total"),
        symbol("bot_orders_submit_failed_total"),
        symbol("bot_fills_received_total"),
        symbol("bot_pnl_fills_applied_total"),
        symbol("bot_pnl_fills_duplicate_total"),
        symbol("bot_pnl_fills_invalid_total"),
        symbol("bot_pnl_fills_inconsistent_total"),
        symbol("bot_pnl_fill_delta_mismatch_total"),
        symbol("bot_orders_rejected_total"),
        symbol("bot_orders_ttl_refresh_total"),
        symbol("bot_orders_ttl_refresh_cancel_rejected_total"),
        symbol("bot_orders_ttl_refresh_cancel_submit_failed_total"),
        symbol("bot_orders_quote_restore_rejected_total"),
        symbol("bot_orders_safety_cap_hit_total"),
        symbol("bot_orders_book_driven_requote_submit_failed_total"),
        symbol("bot_orders_book_driven_requote_cancel_rejected_total"),
        symbol("bot_orders_cancel_ack_expired_total"),
        global("bot_pnl_fills_unknown_order_total"),
        global("bot_orders_cancelled_total")
      ]
      + (if ($phase | IN("outage-settled","outage-hold","reconnected-no-reference")) then []
        elif $profile == "pause-and-cancel" then [
          symbol("bot_pnl_unrealized"),
          symbol("bot_pnl_total")
        ]
        else [
          target("bot_pnl_unrealized"),
          target("bot_pnl_total")
        ] end)
      + (if ($phase | IN("outage-settled","outage-hold")) then []
        elif $profile == "pause-and-cancel" then [
          symbol("bot_market_data_reference_age_seconds")
        ]
        else [
          target("bot_market_data_reference_age_seconds")
        ] end)
      + (if $profile == "inventory-skew" then [
          symbol("bot_strategy_inventory_skew_ticks")
        ] else [] end)
      + (if $profile == "volatility-spread" then
          [symbol("bot_strategy_volatility_additional_half_spread_ticks")]
          + (if ($phase | IN("warmup-complete","duration","final")) then
              [target("bot_strategy_volatility_move_estimate_ticks")]
            else [] end)
        else [] end)
      + (if $profile == "pause-and-cancel" then [
          symbol("bot_market_data_reference_eligible"),
          symbol("bot_market_data_reference_eligible_current"),
          symbol("bot_market_data_availability_transition_total"),
          symbol("bot_market_data_quote_suppressed_total"),
          symbol("bot_orders_feed_unavailable_cancel_total"),
          symbol("bot_orders_feed_unavailable_cancel_rejected_total"),
          symbol("bot_orders_feed_unavailable_cancel_submit_failed_total"),
          symbol("bot_orders_feed_unavailable_cancel_retry_total")
        ] else [] end)
    '
}

record_metric_presence() {
    local phase="$1" timestamp="$2" body="$3" persist_failure="${4:-true}"
    local requirements report rows_csv
    requirements="$(mandatory_metric_requirements "$phase")" || return 1
    report="$(jq -c \
        --arg timestampUtc "$timestamp" \
        --arg phase "$phase" \
        --arg serviceName "b3-market-maker-bot" \
        --arg targetSymbol "$symbol" \
        --argjson symbols "$configured_symbols_json" \
        --argjson requirements "$requirements" '
      .data.result as $results |
      [
        $requirements[] as $requirement |
        (if $requirement.scope == "symbol" then $symbols[]
         elif $requirement.scope == "target" then $targetSymbol
         else null end) as $symbol |
        [
          $results[] |
          select(.metric.__name__ == $requirement.metric) |
          select((.metric.service_name // "") == $serviceName) |
          select($symbol == null or (.metric.symbol // "") == $symbol)
        ] as $rows |
        ([
          $rows[].value[1] |
          if . == "NaN" or . == "+Inf" or . == "-Inf" then null else tonumber end
        ]) as $values |
        {
          timestampUtc: $timestampUtc,
          phase: $phase,
          metric: $requirement.metric,
          symbol: $symbol,
          required: true,
          present: (($rows | length) > 0 and all($values[]; . != null)),
          seriesCount: ($rows | length),
          value: (
            if ($rows | length) == 0 or any($values[]; . == null)
            then null
            else ($values | add)
            end
          )
        }
      ]
    ' <<<"$body")" || return 1
    if ! jq -e 'all(.[]; .present)' >/dev/null <<<"$report"; then
        [[ "$persist_failure" == "true" ]] || return 2
        jq -c '.[]' <<<"$report" >>"$metric_presence_jsonl" || return 1
        rows_csv="$(jq -r '.[] |
          [.timestampUtc,.phase,.metric,(.symbol // ""),.required,.present,.seriesCount,(.value // "")] |
          @csv' <<<"$report")" || return 1
        printf '%s\n' "$rows_csv" >>"$metric_presence_csv" || return 1
        printf '%s\n' "$report" >"${artifacts_dir}/metric-presence-error.json" || return 1
        log "ERROR: mandatory metric series missing in phase '$phase': $(jq -c '[.[] | select(.present | not) | {metric,symbol}]' <<<"$report")"
        return 1
    fi
    jq -c '.[]' <<<"$report" >>"$metric_presence_jsonl" || return 1
    rows_csv="$(jq -r '.[] |
      [.timestampUtc,.phase,.metric,(.symbol // ""),.required,.present,.seriesCount,(.value // "")] |
      @csv' <<<"$report")" || return 1
    printf '%s\n' "$rows_csv" >>"$metric_presence_csv" || return 1
}

capture_metrics() {
    local phase="$1" timestamp body started presence_status normalized csv_rows current_accounting
    capture_runtime_state "$phase" || return 1
    current_accounting="$(current_accounting_period)" || return 1
    if [[ -z "$current_accounting" ]]; then
        log "ERROR: accountingPeriodStartedAtUtc is absent while sampling phase '$phase'"
        return 1
    fi
    if [[ "$current_accounting" != "$accounting_period_started_at_utc" ]]; then
        accounting_stable=false
        log "ERROR: accountingPeriodStartedAtUtc changed from '$accounting_period_started_at_utc' to '$current_accounting'"
    fi
    started=$SECONDS
    while true; do
        timestamp="$(date -u +%Y-%m-%dT%H:%M:%SZ)" || return 1
        body="$(collect_all_metrics)" || {
            log "ERROR: failed to collect mandatory market-maker telemetry for phase '$phase'"
            return 1
        }
        if jq -e '(.data.result | length) > 0' >/dev/null <<<"$body"; then
            presence_status=0
            record_metric_presence "$phase" "$timestamp" "$body" false ||
                presence_status=$?
            if ((presence_status == 0)); then
                break
            fi
            if ((presence_status != 2)); then
                return "$presence_status"
            fi
        fi
        if ((SECONDS - started >= full_telemetry_cycle_seconds)); then
            record_metric_presence "$phase" "$timestamp" "$body" true || return 1
            break
        fi
        sleep 1
    done
    if ! normalized="$(jq -c \
      --arg timestamp "$timestamp" \
      --arg phase "$phase" \
      --arg accountingPeriodStartedAtUtc "$current_accounting" '
      .data.result[] |
      {
        timestamp_utc: $timestamp,
        phase: $phase,
        accountingPeriodPresent: true,
        accountingPeriodStartedAtUtc: $accountingPeriodStartedAtUtc,
        metric: .metric.__name__,
        labels: (.metric | del(.__name__)),
        present: true,
        value: (.value[1] as $value | if $value == "NaN" then null else ($value | tonumber) end)
      }' <<<"$body")"; then
        printf '%s\n' "$body" >"${artifacts_dir}/metric-normalization-error.json" || return 1
        log "ERROR: failed to normalize market-maker metrics for phase '$phase'"
        return 1
    fi
    printf '%s\n' "$normalized" >>"$samples_jsonl" || return 1
    if ! csv_rows="$(jq -r \
      --arg timestamp "$timestamp" \
      --arg phase "$phase" \
      --arg accountingPeriodStartedAtUtc "$current_accounting" '
      .data.result[] |
      [
        $timestamp,
        $phase,
        true,
        $accountingPeriodStartedAtUtc,
        .metric.__name__,
        (.metric.symbol // ""),
        (.metric.side // ""),
        (.metric.reason // ""),
        (.metric.available // ""),
        (.metric.exported_source // .metric.source // ""),
        true,
        .value[1]
      ] | @csv' <<<"$body")"; then
        printf '%s\n' "$body" >"${artifacts_dir}/metric-csv-error.json" || return 1
        log "ERROR: failed to write market-maker metric CSV for phase '$phase'"
        return 1
    fi
    printf '%s\n' "$csv_rows" >>"$samples_csv" || return 1
}

wait_metric_equals() {
    local query="$1" expected="$2" timeout="$3" started value next_runtime_check sample present
    started=$SECONDS
    next_runtime_check=$SECONDS
    while (( SECONDS - started < timeout )); do
        refresh_strict_symbols_if_due metric-wait-refresh || return 1
        if (( SECONDS >= next_runtime_check )); then
            capture_runtime_state metric-wait || return 1
            next_runtime_check=$((SECONDS + 10))
        fi
        sample="$(metric_observation "$query")" || return 1
        present="$(jq -r '.present and (.value != null)' <<<"$sample")" || return 1
        value="$(jq -r '.value // "missing"' <<<"$sample")" || return 1
        if [[ "$present" == "true" ]] &&
            awk -v actual="$value" -v expected="$expected" 'BEGIN { exit !(actual == expected) }'; then
            return 0
        fi
        sleep 1
    done
    log "ERROR: metric did not reach $expected with a present series: $query (last=${value:-missing}, present=${present:-false})"
    return 1
}

capture_outage_telemetry_snapshot() {
    local phase="$1" timestamp transitions suppressions feed_cancels submissions open_orders eligible
    timestamp="$(date -u +%Y-%m-%dT%H:%M:%SZ)" || return 1
    transitions="$(metric_observation \
        'sum(bot_market_data_availability_transition_total{service_name="b3-market-maker-bot",available="false"})')" ||
        return 1
    suppressions="$(metric_observation \
        'sum(bot_market_data_quote_suppressed_total{service_name="b3-market-maker-bot",reason="disconnected"})')" ||
        return 1
    feed_cancels="$(metric_observation \
        'sum(bot_orders_feed_unavailable_cancel_total{service_name="b3-market-maker-bot"})')" ||
        return 1
    submissions="$(metric_observation \
        'sum(bot_orders_submitted_total{service_name="b3-market-maker-bot"})')" ||
        return 1
    open_orders="$(metric_observation \
        'sum(bot_orders_open{service_name="b3-market-maker-bot"})')" ||
        return 1
    eligible="$(metric_observation \
        'sum(bot_market_data_reference_eligible_current{service_name="b3-market-maker-bot"})')" ||
        return 1
    jq -cn \
        --arg timestampUtc "$timestamp" \
        --arg phase "$phase" \
        --argjson transitions "$transitions" \
        --argjson suppressions "$suppressions" \
        --argjson feedCancels "$feed_cancels" \
        --argjson submissions "$submissions" \
        --argjson openOrders "$open_orders" \
        --argjson eligible "$eligible" '
      {
        timestampUtc: $timestampUtc,
        phase: $phase,
        present: all(
          $transitions,
          $suppressions,
          $feedCancels,
          $submissions,
          $openOrders,
          $eligible;
          .present and .value != null
        ),
        feedIneligibleTransitions: $transitions.value,
        quoteSuppressions: $suppressions.value,
        feedUnavailableCancels: $feedCancels.value,
        ordersSubmitted: $submissions.value,
        openOrders: $openOrders.value,
        eligibleSymbols: $eligible.value,
        observations: {
          feedIneligibleTransitions: $transitions,
          quoteSuppressions: $suppressions,
          feedUnavailableCancels: $feedCancels,
          ordersSubmitted: $submissions,
          openOrders: $openOrders,
          eligibleSymbols: $eligible
        }
      }
    ' || return 1
}

strict_refresh_next_due=0
strict_refresh_segment=pre-outage
strict_refresh_paused=false
strict_refresh_cycle_count=0

run_strict_refresh_cycle() {
    local phase="$1" segment="$2" price_mode="${3:-live}"
    local refresh_symbol refresh_quantity refresh_price configured_price price_source refresh_rows direction
    local primary_reference_pending
    [[ "$profile" == "pause-and-cancel" ]] || return 0
    if ((strict_refresh_cycle_count % 2 == 0)); then
        direction=trading-buys
    else
        direction=counterparty-buys
    fi
    refresh_rows="$(jq -r '.[] | [.symbol,.quantity,.referencePrice] | @tsv' \
        <<<"$configured_instruments_json")" || return 1
    while IFS=$'\t' read -r refresh_symbol refresh_quantity configured_price; do
        if [[ "$price_mode" == "live" ]]; then
            primary_reference_pending="$strict_primary_reference_pending"
            resolve_live_refresh_price_into refresh_price "$refresh_symbol" || return 1
            if [[ "$refresh_symbol" == "$symbol" ]]; then
                strict_primary_reference_pending=false
            fi
            if [[ "$refresh_symbol" == "$symbol" && "$primary_reference_pending" == "true" ]]; then
                price_source=trading-host-live-reference-interior-tick
            else
                price_source=trading-host-live-reference
            fi
        else
            refresh_price="$configured_price"
            price_source=configured-reference
        fi
        submit_recovery_cross \
            "$refresh_symbol" \
            "$refresh_quantity" \
            "$refresh_price" \
            "$phase" \
            "$segment" \
            "$price_source" \
            "$direction" || return 1
    done <<<"$refresh_rows"
    strict_refresh_cycle_count=$((strict_refresh_cycle_count + 1))
    strict_refresh_next_due=$((SECONDS + strict_refresh_interval_seconds))
}

refresh_strict_symbols_if_due() {
    local phase="$1"
    [[ "$profile" == "pause-and-cancel" ]] || return 0
    $strict_refresh_paused && return 0
    if ((SECONDS >= strict_refresh_next_due)); then
        run_strict_refresh_cycle "$phase" "$strict_refresh_segment" live || return 1
    fi
}

stabilize_pre_outage_submissions() {
    local started samples='[]' sample report report_passed elapsed refresh_event_count
    started=$SECONDS
    while true; do
        refresh_strict_symbols_if_due pre-outage-stabilization-refresh || return 1
        soak_run_required_phase_step pre-outage-stabilization capture-metrics \
            capture_metrics || return 1
        sample="$(soak_run_required_phase_step pre-outage-stabilization \
            capture-outage-snapshot capture_outage_telemetry_snapshot)" || {
            log "ERROR: failed to capture pre-outage stabilization telemetry snapshot"
            return 1
        }
        refresh_event_count="$(wc -l <"$strict_refresh_jsonl")" || return 1
        samples="$(jq -cn \
            --argjson samples "$samples" \
            --argjson sample "$sample" \
            --argjson refreshEventCount "$refresh_event_count" \
            '$samples + [($sample + {strictRefreshEventCount:$refreshEventCount})]')" ||
            return 1
        report="$(jq -cn \
            --argjson requiredStableCycles "$pre_outage_stabilization_cycles" \
            --argjson intervalSeconds "$pre_outage_stabilization_interval_seconds" \
            --argjson timeoutSeconds "$pre_outage_stabilization_timeout_seconds" \
            --argjson samples "$samples" \
            '{
              requiredStableCycles: $requiredStableCycles,
              intervalSeconds: $intervalSeconds,
              timeoutSeconds: $timeoutSeconds,
              samples: $samples
            }' | soak_evaluate_counter_stabilization)" || return 1
        report_passed="$(jq -r '.passed' <<<"$report")" || return 1
        if [[ "$report_passed" == "true" ]]; then
            printf '%s\n' "$report" >"${artifacts_dir}/pre-outage-stabilization.json" ||
                return 1
            pre_outage_stabilization_report="$report"
            return 0
        fi
        elapsed=$((SECONDS - started))
        if (( elapsed + pre_outage_stabilization_interval_seconds >
            pre_outage_stabilization_timeout_seconds )); then
            printf '%s\n' "$report" >"${artifacts_dir}/pre-outage-stabilization.json" ||
                return 1
            pre_outage_stabilization_report="$report"
            log "ERROR: submitted-order counter did not remain stable for ${pre_outage_stabilization_cycles} consecutive full telemetry cycles within ${pre_outage_stabilization_timeout_seconds}s"
            return 1
        fi
        sleep "$pre_outage_stabilization_interval_seconds"
    done
}

capture_reconnected_no_reference_sample() {
    local snapshot
    soak_run_required_phase_step reconnected-no-reference capture-metrics \
        capture_metrics || return 1
    snapshot="$(soak_run_required_phase_step reconnected-no-reference \
        capture-outage-snapshot capture_outage_telemetry_snapshot)" || {
        log "ERROR: failed to capture reconnected-without-reference telemetry snapshot"
        return 1
    }
    reconnected_samples="$(jq -cn \
        --argjson rows "$reconnected_samples" \
        --argjson row "$snapshot" \
        '$rows + [$row]')" || return 1
    if ! jq -e \
        --argjson submitted "$pre_outage_submissions" '
          .present and
          .eligibleSymbols == 0 and
          .openOrders == 0 and
          .ordersSubmitted == $submitted
        ' >/dev/null <<<"$snapshot"; then
        log "ERROR: reconnect reused stale epoch data or submitted orders before a fresh reference"
        return 1
    fi
}

next_side=Buy
run_window() {
    local seconds="$1" phase="$2" sample="$3" started next_sample now price side
    started=$SECONDS
    next_sample=$SECONDS
    while (( SECONDS - started < seconds )); do
        refresh_strict_symbols_if_due "${phase}-strict-refresh" || return 1
        now=$SECONDS
        if [[ "$sample" == "true" ]] && (( now >= next_sample )); then
            capture_metrics "$phase"
            next_sample=$((now + sample_interval_seconds))
        fi
        side="$next_side"
        if [[ "$side" == "Buy" ]]; then
            resolve_primary_order_price_into price "$side" "$marketable_buy_price" "$phase" || return 1
            next_side=Sell
        else
            resolve_primary_order_price_into price "$side" "$marketable_sell_price" "$phase" || return 1
            next_side=Buy
        fi
        submit_order trading_token trading_token_expires_at "$trading_user" "$trading_password" "$side" "$price" "$phase"
        sleep "$workload_interval_seconds"
    done
    if [[ "$sample" == "true" ]]; then
        refresh_strict_symbols_if_due "${phase}-strict-refresh" || return 1
        capture_metrics "$phase"
    fi
}

if [[ "$profile" == "pause-and-cancel" ]]; then
    log "printing initial current-epoch trades for every strict-policy symbol"
    run_strict_refresh_cycle initial-reference-cross pre-outage configured
fi

log "waiting for initial two-sided PETR4 quotes"
wait_metric_equals \
    "bot_orders_open{service_name=\"b3-market-maker-bot\",symbol=\"$symbol\"}" \
    2 60
log "executing a two-fill bootstrap round trip so the P&L ledger emits snapshots"
resolve_primary_order_price_into price Buy "$marketable_buy_price" "accounting-bootstrap"
submit_order trading_token trading_token_expires_at "$trading_user" "$trading_password" Buy "$price" "accounting-bootstrap"
refresh_strict_symbols_if_due accounting-bootstrap-between-fills
resolve_primary_order_price_into price Sell "$marketable_sell_price" "accounting-bootstrap"
submit_order trading_token trading_token_expires_at "$trading_user" "$trading_password" Sell "$price" "accounting-bootstrap"
if [[ "$profile" == "pause-and-cancel" ]]; then
    sleep "$workload_interval_seconds"
fi
wait_accounting_period
capture_metrics initial

inventory_long_pass=true
inventory_short_pass=true
if [[ "$profile" == "inventory-skew" ]]; then
    log "applying ${inventory_bias_lots}-lot sell bias so the bot becomes long and reaches skew saturation"
    for ((i = 0; i < inventory_bias_lots; i++)); do
        resolve_primary_order_price_into price Sell "$marketable_sell_price" "inventory-bias"
        submit_order trading_token trading_token_expires_at "$trading_user" "$trading_password" Sell "$price" "inventory-bias"
    done
    if ! wait_metric_equals \
        "bot_strategy_inventory_skew_ticks{service_name=\"b3-market-maker-bot\",symbol=\"$symbol\"}" \
        5 30; then
        inventory_long_pass=false
    fi
    capture_metrics inventory-long-saturation

    log "reversing through flat to ${inventory_bias_lots} lots short to prove skew direction"
    for ((i = 0; i < inventory_bias_lots * 2; i++)); do
        resolve_primary_order_price_into price Buy "$marketable_buy_price" "inventory-reversal"
        submit_order trading_token trading_token_expires_at "$trading_user" "$trading_password" Buy "$price" "inventory-reversal"
    done
    if ! wait_metric_equals \
        "bot_strategy_inventory_skew_ticks{service_name=\"b3-market-maker-bot\",symbol=\"$symbol\"}" \
        -5 30; then
        inventory_short_pass=false
    fi
    capture_metrics inventory-short-saturation
fi

log "warmup for ${warmup_seconds}s"
run_window "$warmup_seconds" warmup true
capture_metrics warmup-complete

pause_outage_pass=true
pause_recovery_pass=true
reconnect_stale_epoch_pass=true
outage_telemetry_pass=true
outage_telemetry_evidence=null
marketdata_transition_pass=true
marketdata_transition_evidence=null
pre_outage_stabilization_pass=true
pre_outage_stabilization_report=null
if [[ "$profile" == "pause-and-cancel" ]]; then
    log "stabilizing submitted-order telemetry before the pre-outage boundary"
    if ! stabilize_pre_outage_submissions; then
        pre_outage_stabilization_pass=false
        exit 1
    fi
    refresh_strict_symbols_if_due pre-outage-refresh
    capture_metrics pre-outage
    outage_pre_snapshot="$(capture_outage_telemetry_snapshot pre-outage)"
    stabilized_submissions="$(jq -r '.stableWindow[-1].ordersSubmitted // "missing"' \
        <<<"$pre_outage_stabilization_report")"
    pre_outage_submissions="$(jq -r '.ordersSubmitted // "missing"' <<<"$outage_pre_snapshot")"
    if [[ "$pre_outage_stabilization_report" == "null" ]] ||
        [[ "$stabilized_submissions" != "$pre_outage_submissions" ]]; then
        pre_outage_stabilization_pass=false
        log "ERROR: submitted-order counter changed between stabilization and the pre-outage boundary"
        exit 1
    fi
    log "stopping marketdata to exercise PauseAndCancel"
    strict_refresh_paused=true
    marketdata_before_snapshot="$(capture_service_runtime_snapshot marketdata before-intentional-stop)"
    outage_check_started_seconds=$SECONDS
    outage_deadline=$((SECONDS + recovery_timeout_seconds))
    "${compose[@]}" stop marketdata
    marketdata_transition_phase=stopped
    marketdata_stopped_snapshot="$(capture_service_runtime_snapshot marketdata intentionally-stopped)"
    capture_runtime_state outage-stopped
    outage_remaining=$((outage_deadline - SECONDS))
    if (( outage_remaining <= 0 )); then
        pause_outage_pass=false
        outage_remaining=1
    fi
    if ! wait_metric_equals \
        'sum(bot_market_data_reference_eligible_current{service_name="b3-market-maker-bot"})' \
        0 "$outage_remaining"; then
        pause_outage_pass=false
    fi
    outage_remaining=$((outage_deadline - SECONDS))
    if (( outage_remaining <= 0 )); then
        pause_outage_pass=false
        outage_remaining=1
    fi
    if ! wait_metric_equals \
        'sum(bot_orders_open{service_name="b3-market-maker-bot"})' \
        0 "$outage_remaining"; then
        pause_outage_pass=false
    fi
    outage_cancellation_elapsed_seconds=$((SECONDS - outage_check_started_seconds))
    if (( outage_cancellation_elapsed_seconds > recovery_timeout_seconds )); then
        pause_outage_pass=false
    fi
    capture_metrics outage-settled
    outage_settled_snapshot="$(capture_outage_telemetry_snapshot outage-settled)"
    outage_hold_samples='[]'
    outage_started=$SECONDS
    capture_metrics outage-hold
    outage_hold_snapshot="$(capture_outage_telemetry_snapshot outage-hold)"
    outage_hold_samples="$(jq -cn \
        --argjson rows "$outage_hold_samples" \
        --argjson row "$outage_hold_snapshot" \
        '$rows + [$row]')"
    while (( SECONDS - outage_started < outage_seconds )); do
        outage_hold_remaining=$((outage_seconds - (SECONDS - outage_started)))
        outage_hold_sleep=$((outage_hold_remaining < sample_interval_seconds
            ? outage_hold_remaining
            : sample_interval_seconds))
        (( outage_hold_sleep > 0 )) || break
        sleep "$outage_hold_sleep"
        capture_metrics outage-hold
        outage_hold_snapshot="$(capture_outage_telemetry_snapshot outage-hold)"
        outage_hold_samples="$(jq -cn \
            --argjson rows "$outage_hold_samples" \
            --argjson row "$outage_hold_snapshot" \
            '$rows + [$row]')"
    done

    log "starting marketdata and waiting for the bot to reconnect"
    recovery_started_at="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
    recovery_started_seconds=$SECONDS
    recovery_deadline=$((SECONDS + recovery_timeout_seconds))
    "${compose[@]}" start marketdata
    recovery_remaining=$((recovery_deadline - SECONDS))
    if (( recovery_remaining <= 0 )) ||
        ! wait_service_healthy marketdata "$recovery_remaining"; then
        pause_recovery_pass=false
    fi
    marketdata_after_snapshot="$(capture_service_runtime_snapshot marketdata after-intentional-start)"
    marketdata_after_started_at="$(jq -er '.startedAtUtc' <<<"$marketdata_after_snapshot")"
    marketdata_transition_phase=restarted
    marketdata_transition_evidence="$(jq -cn \
        --argjson before "$marketdata_before_snapshot" \
        --argjson stopped "$marketdata_stopped_snapshot" \
        --argjson after "$marketdata_after_snapshot" \
        '{
          expectedTransition: "running -> exited -> running",
          allowedTransitionCount: 1,
          before: $before,
          stopped: $stopped,
          after: $after
        }')"
    if ! jq -e '
        ([.before,.stopped,.after] | map(.service) | unique) == ["marketdata"] and
        ([.before,.stopped,.after] | map(.containerId) | unique | length) == 1 and
        ([.before,.stopped,.after] | map(.imageId) | unique | length) == 1 and
        all(.before,.stopped,.after; .restartCount == 0) and
        .before.status == "running" and
        .stopped.status == "exited" and
        .after.status == "running" and
        .stopped.startedAtUtc == .before.startedAtUtc and
        .after.startedAtUtc != .before.startedAtUtc
    ' >/dev/null <<<"$marketdata_transition_evidence"; then
        marketdata_transition_pass=false
        runtime_stable=false
        log "ERROR: marketdata did not exhibit exactly the permitted stop/start identity transition"
    fi
    printf '%s\n' "$marketdata_transition_evidence" >"${artifacts_dir}/marketdata-transition.json"
    capture_runtime_state recovery-started
    recovery_remaining=$((recovery_deadline - SECONDS))
    if (( recovery_remaining <= 0 )) ||
        ! wait_bot_log_since "$recovery_started_at" "MarketData connection state: Connected" "$recovery_remaining"; then
        pause_recovery_pass=false
    fi
    reconnected_samples='[]'
    if $pause_recovery_pass; then
        log "holding the reconnected feed without generating a fresh reference for ${reconnect_stale_hold_seconds}s"
        if ! capture_reconnected_no_reference_sample; then
            pause_recovery_pass=false
            reconnect_stale_epoch_pass=false
        fi
        reconnect_hold_started=$SECONDS
        while $pause_recovery_pass &&
            (( SECONDS - reconnect_hold_started < reconnect_stale_hold_seconds )); do
            reconnect_hold_remaining=$((reconnect_stale_hold_seconds -
                (SECONDS - reconnect_hold_started)))
            reconnect_hold_sleep=$((reconnect_hold_remaining < sample_interval_seconds
                ? reconnect_hold_remaining
                : sample_interval_seconds))
            (( reconnect_hold_sleep > 0 )) || reconnect_hold_sleep=1
            sleep "$reconnect_hold_sleep"
            if ! capture_reconnected_no_reference_sample; then
                pause_recovery_pass=false
                reconnect_stale_epoch_pass=false
            fi
        done
    else
        reconnect_stale_epoch_pass=false
    fi
    if $pause_recovery_pass; then
        log "printing ${recovery_cross_attempts} post-gap cross rounds for every configured symbol"
        strict_refresh_segment=post-recovery
        strict_refresh_paused=false
        for ((attempt = 1; attempt <= recovery_cross_attempts; attempt++)); do
            run_strict_refresh_cycle "feed-recovery-cross-${attempt}" post-recovery configured
            if (( attempt < recovery_cross_attempts )); then
                sleep "$recovery_cross_interval_seconds"
            fi
        done

        for recovery_symbol in "${configured_symbols[@]}"; do
            recovery_remaining=$((recovery_deadline - SECONDS))
            if (( recovery_remaining <= 0 )) ||
                ! wait_metric_equals \
                    "count(bot_market_data_reference_age_seconds{service_name=\"b3-market-maker-bot\",symbol=\"$recovery_symbol\",exported_source=\"last_trade_price\"} < $max_reference_age_seconds)" \
                    1 "$recovery_remaining"; then
                pause_recovery_pass=false
                continue
            fi
            recovery_remaining=$((recovery_deadline - SECONDS))
            if (( recovery_remaining <= 0 )) ||
                ! wait_metric_equals \
                    "bot_orders_open{service_name=\"b3-market-maker-bot\",symbol=\"$recovery_symbol\"}" \
                    2 "$recovery_remaining"; then
                pause_recovery_pass=false
            fi
        done
    fi
    recovery_elapsed_seconds=$((SECONDS - recovery_started_seconds))
    if (( recovery_elapsed_seconds > recovery_timeout_seconds )); then
        pause_recovery_pass=false
    fi
    if ! $pause_recovery_pass; then
        log "ERROR: fresh three-symbol recovery did not complete within ${recovery_timeout_seconds}s"
    fi
    capture_metrics recovered
    outage_post_snapshot="$(capture_outage_telemetry_snapshot recovered)"
    outage_telemetry_evidence="$(jq -cn \
        --argjson expectedSymbolCount "${#configured_symbols[@]}" \
        --argjson reconnectHoldSeconds "$reconnect_stale_hold_seconds" \
        --argjson pre "$outage_pre_snapshot" \
        --argjson settled "$outage_settled_snapshot" \
        --argjson holdSamples "$outage_hold_samples" \
        --argjson reconnectedSamples "$reconnected_samples" \
        --argjson stabilization "$pre_outage_stabilization_report" \
        --argjson post "$outage_post_snapshot" \
        '{
          expectedSymbolCount: $expectedSymbolCount,
          reconnectHoldSeconds: $reconnectHoldSeconds,
          phaseBoundaries: {
            pre: "captured after the submitted-order counter was stable across full telemetry cycles and immediately before the intentional marketdata stop",
            settled: "captured after eligibility=0 and all asynchronous quote cancellations reached open=0",
            hold: "samples collected after the settled boundary and before marketdata restart",
            reconnected: "samples collected after Connected and for a full telemetry cycle before any new trade/reference was generated",
            post: "captured only after fresh references and exact two-sided quotes recovered"
          },
          preOutageStabilization: $stabilization,
          pre: $pre,
          settled: $settled,
          holdSamples: $holdSamples,
          reconnectedSamples: $reconnectedSamples,
          post: $post
        }')"
    outage_telemetry_report="$(soak_evaluate_outage_telemetry <<<"$outage_telemetry_evidence")"
    outage_telemetry_pass="$(jq -r '.passed' <<<"$outage_telemetry_report")"
    printf '%s\n' "$outage_telemetry_report" >"${artifacts_dir}/outage-telemetry.json"
    if [[ "$outage_telemetry_pass" != "true" ]]; then
        pause_outage_pass=false
        log "ERROR: PauseAndCancel telemetry contract failed: $(jq -c '.observed' <<<"$outage_telemetry_report")"
    fi
fi

log "evidence window for ${duration_seconds}s"
run_window "$duration_seconds" duration true
log "waiting for exact final quote state"
for configured_symbol in "${configured_symbols[@]}"; do
    if [[ "$profile" == "pause-and-cancel" ]]; then
        wait_metric_equals \
            "bot_market_data_reference_eligible_current{service_name=\"b3-market-maker-bot\",symbol=\"$configured_symbol\"}" \
            1 "$recovery_timeout_seconds" || true
    fi
    wait_metric_equals \
        "bot_orders_open{service_name=\"b3-market-maker-bot\",symbol=\"$configured_symbol\"}" \
        2 "$recovery_timeout_seconds" || true
done
wait_metric_equals 'sum(bot_orders_open{service_name="b3-market-maker-bot"})' \
    6 "$recovery_timeout_seconds" || true
capture_metrics final
if ! stop_runtime_event_monitor_for_cleanup; then
    runtime_event_monitor_stable=false
    runtime_stable=false
fi
runtime_event_monitor_pid=""
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
final_quotes_exact=true
final_quote_state='[]'
for configured_symbol in "${configured_symbols[@]}"; do
    symbol_open="$(metric_value "bot_orders_open{service_name=\"b3-market-maker-bot\",symbol=\"$configured_symbol\"}")"
    symbol_eligible=null
    if [[ "$profile" == "pause-and-cancel" ]]; then
        symbol_eligible="$(metric_value "bot_market_data_reference_eligible_current{service_name=\"b3-market-maker-bot\",symbol=\"$configured_symbol\"}")"
    fi
    final_quote_state="$(jq -cn \
        --argjson rows "$final_quote_state" \
        --arg symbol "$configured_symbol" \
        --argjson openOrders "$symbol_open" \
        --argjson eligible "$symbol_eligible" \
        '$rows + [{symbol:$symbol,openOrders:$openOrders,eligible:$eligible}]')"
    if ! numeric_test "$symbol_open == 2" ||
        [[ "$profile" == "pause-and-cancel" && "$symbol_eligible" != "1" ]]; then
        final_quotes_exact=false
    fi
done
corruption_total="$(soak_sum_metric_values \
    bot_pnl_fills_unknown_order_total \
    bot_pnl_fills_duplicate_total \
    bot_pnl_fills_invalid_total \
    bot_pnl_fills_inconsistent_total \
    bot_pnl_fill_delta_mismatch_total)"
safety_total="$(metric_value 'sum(bot_orders_safety_cap_hit_total{service_name="b3-market-maker-bot"})')"
fill_received="$(metric_value 'sum(bot_fills_received_total{service_name="b3-market-maker-bot"})')"
fills_applied="$(metric_value 'sum(bot_pnl_fills_applied_total{service_name="b3-market-maker-bot"})')"
operational_errors="$(soak_operational_error_total)"
continuity="$(jq -s --arg symbol "$symbol" '
    [.[] | select(.phase == "duration" and .metric == "bot_orders_open" and .labels.symbol == $symbol)] as $rows |
    if ($rows | length) == 0 then 0
    else (([$rows[] | select(.value == 2)] | length) / ($rows | length))
    end' "$samples_jsonl")"
open_order_bounds_report="$(jq -s '
  [.[] | select(.metric == "bot_orders_open")] as $rows |
  {
    perSymbol: (
      $rows
      | group_by(.labels.symbol)
      | map({symbol: .[0].labels.symbol, maxOpenOrders: (map(.value) | max)})
    ),
    maxTotal: (
      $rows
      | group_by(.timestamp_utc + "|" + .phase)
      | map(map(.value) | add)
      | max
    )
  }
' "$samples_jsonl")"
open_orders_bounded="$(jq -r '
  (.perSymbol | length) == 3 and
  all(.perSymbol[]; .maxOpenOrders <= 2) and
  .maxTotal <= 6
' <<<"$open_order_bounds_report")"
printf '%s\n' "$open_order_bounds_report" >"${artifacts_dir}/open-order-bounds.json"
accounting_period_count="$(jq -s '[.[].accountingPeriodStartedAtUtc] | unique | length' "$samples_jsonl")"
counter_monotonic_report="$(jq -s '
  reduce (.[] | select((.metric | endswith("_total")) and .metric != "bot_pnl_total")) as $row (
    {last: {}, violations: []};
    ($row.metric + "|" + ($row.labels | tojson)) as $key |
    (if (.last | has($key)) and $row.value < .last[$key] then
       .violations += [{
         metric: $row.metric,
         labels: $row.labels,
         previous: .last[$key],
         current: $row.value,
         phase: $row.phase,
         timestampUtc: $row.timestamp_utc
       }]
     else . end) |
    .last[$key] = $row.value
  )
' "$samples_jsonl")"
counter_monotonic="$(jq -r '.violations | length == 0' <<<"$counter_monotonic_report")"
printf '%s\n' "$counter_monotonic_report" >"${artifacts_dir}/counter-monotonicity.json"
runtime_lifecycle_report="$(jq -s \
  --arg profile "$profile" \
  --argjson monitorStable "$runtime_event_monitor_stable" \
  --argjson critical "$critical_services_json" '
  [
    .[] |
    (.Actor.Attributes["com.docker.compose.service"] // "") as $service |
    select($critical | index($service)) |
    select(.Action == "start" or .Action == "die" or .Action == "restart") |
    {timeNano: .timeNano, service: $service, action: .Action, containerId: (.Actor.ID // .id)}
  ] as $events |
  [
    $critical[] as $service |
    [$events[] | select(.service == $service)] as $rows |
    {
      service: $service,
      startCount: ([$rows[] | select(.action == "start")] | length),
      dieCount: ([$rows[] | select(.action == "die")] | length),
      restartCount: ([$rows[] | select(.action == "restart")] | length),
      events: $rows,
      passed: (
        if $profile == "pause-and-cancel" and $service == "marketdata" then
          ([$rows[] | select(.action == "start")] | length) == 1 and
          ([$rows[] | select(.action == "die")] | length) == 1 and
          ([$rows[] | select(.action == "restart")] | length) == 0
        else
          ($rows | length) == 0
        end
      )
    }
  ] as $services |
  {
    monitoredAfterInitialSnapshot: true,
    monitorStable: $monitorStable,
    services: $services,
    passed: ($monitorStable and all($services[]; .passed))
  }
' "$runtime_events_jsonl")"
runtime_lifecycle_pass="$(jq -r '.passed' <<<"$runtime_lifecycle_report")"
printf '%s\n' "$runtime_lifecycle_report" >"${artifacts_dir}/runtime-lifecycle.json"
runtime_continuity_report="$(jq -s \
  --arg profile "$profile" \
  --argjson critical "$critical_services_json" \
  --argjson lifecycle "$runtime_lifecycle_report" \
  --argjson transition "$marketdata_transition_evidence" '
  . as $all |
  [
    $critical[] as $service |
    [$all[] | select(.service == $service)] as $rows |
    {
      service: $service,
      sampleCount: ($rows | length),
      containerIds: ($rows | map(.containerId) | unique),
      imageIds: ($rows | map(.imageId) | unique),
      startedAtUtcValues: ($rows | map(.startedAtUtc) | unique),
      restartCounts: ($rows | map(.restartCount) | unique),
      statuses: ($rows | map(.status) | unique),
      passed: (
        ($rows | length) > 0 and
        ($rows | map(.containerId) | unique | length) == 1 and
        ($rows | map(.imageId) | unique | length) == 1 and
        all($rows[]; .restartCount == 0) and
        (if $profile == "pause-and-cancel" and $service == "marketdata" then
           ($rows | map(.startedAtUtc) | unique | length) == 2 and
           ($rows | map(.status) | unique) == ["exited","running"] and
           $transition != null and
           $transition.allowedTransitionCount == 1
         else
           ($rows | map(.startedAtUtc) | unique | length) == 1 and
           all($rows[]; .status == "running")
         end)
      )
    }
  ] as $services |
  {
    criticalServices: $critical,
    lifecycleEvents: $lifecycle,
    permittedMarketdataTransition: $transition,
    services: $services,
    passed: (
      ($services | length) == ($critical | length) and
      all($services[]; .passed) and
      $lifecycle.passed
    )
  }
' "$runtime_jsonl")"
runtime_continuity_pass="$(jq -r '.passed' <<<"$runtime_continuity_report")"
printf '%s\n' "$runtime_continuity_report" >"${artifacts_dir}/runtime-continuity.json"
if [[ "$runtime_continuity_pass" != "true" || "$runtime_lifecycle_pass" != "true" ]]; then
    runtime_stable=false
fi
max_restart_count="$(jq -s '[.[].restartCount] | max' "$runtime_jsonl")"
strict_freshness_pass=true
strict_freshness_report=null
if [[ "$profile" == "pause-and-cancel" ]]; then
    strict_freshness_report="$(jq -n \
        --argjson configuredSymbols "$configured_symbols_json" \
        --argjson maxReferenceAgeSeconds "$max_reference_age_seconds" \
        --argjson refreshIntervalSeconds "$strict_refresh_interval_seconds" \
        --argjson refreshMarginSeconds "$strict_refresh_margin_seconds" \
        --slurpfile refreshes "$strict_refresh_jsonl" \
        --slurpfile metricSamples "$samples_jsonl" '
          {
            configuredSymbols: $configuredSymbols,
            maxReferenceAgeSeconds: $maxReferenceAgeSeconds,
            refreshIntervalSeconds: $refreshIntervalSeconds,
            refreshMarginSeconds: $refreshMarginSeconds,
            refreshes: $refreshes,
            metricSamples: $metricSamples
          }
        ' | soak_evaluate_strict_freshness)" || {
        log "ERROR: failed to evaluate strict-profile freshness evidence"
        exit 1
    }
    strict_freshness_pass="$(jq -r '.passed' <<<"$strict_freshness_report")" || exit 1
    printf '%s\n' "$strict_freshness_report" >"${artifacts_dir}/strict-freshness.json"
fi

record_check "final-quotes-per-eligible-symbol" "$final_quotes_exact" \
    "exactly 2 open quotes per configured symbol; strict-profile eligibility=1" \
    "$(jq -c . <<<"$final_quote_state")"
record_check "open-orders-total" \
    "$(numeric_test "$final_open_total == 6" && echo true || echo false)" \
    "exactly 6 at successful completion" "$final_open_total"
record_check "quote-continuity" \
    "$(numeric_test "$continuity >= 0.90" && echo true || echo false)" \
    ">=90% duration samples have two PETR4 quotes" "$continuity"
record_check "open-orders-bounded-during-run" "$open_orders_bounded" \
    "every sampled symbol<=2 and sampled total<=6" \
    "$(jq -c . <<<"$open_order_bounds_report")"
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
    "0 submit/reject/cancel/ACK-timeout error events" "$operational_errors"
record_check "credential-child-environments" true \
    "exported password names/values absent from child environments; curl checks every request and docker events is checked before launch" \
    "artifact=credential-environment.json"
if [[ -n "$suite_manifest" ]]; then
    record_check "suite-source-binding" "$suite_compatible" \
        "first accepted run clean-builds the recorded git SHA; later no-build runs exactly match pinned runtime image IDs/digests" \
        "buildMode=$build_mode builtFromGitSha=${built_from_git_sha:-manifest-pinned} artifact=suite-source-binding.json"
fi
record_check "runtime-containers-stable" \
    "$($runtime_stable && [[ "$runtime_continuity_pass" == "true" ]] && echo true || echo false)" \
    "all critical services keep container/image identity and restartCount=0 with no lifecycle event; PauseAndCancel permits exactly one marketdata die/start pair" \
    "stable=$runtime_stable continuity=$runtime_continuity_pass lifecycle=$runtime_lifecycle_pass maxRestartCount=$max_restart_count report=runtime-continuity.json"
if [[ "$profile" == "pause-and-cancel" ]]; then
    record_check "marketdata-intentional-transition" "$marketdata_transition_pass" \
        "exactly one recorded running -> exited -> running transition with stable container/image and restartCount=0" \
        "artifact=marketdata-transition.json"
fi
record_check "accounting-period-stable" \
    "$($accounting_stable && [[ "$accounting_period_count" == "1" ]] && echo true || echo false)" \
    "one non-empty accountingPeriodStartedAtUtc across every sample" \
    "stable=$accounting_stable distinct=$accounting_period_count value=$accounting_period_started_at_utc"
record_check "tracked-counters-monotonic" "$counter_monotonic" \
    "every tracked counter series is monotonically non-decreasing" \
    "violations=$(jq -c '.violations' <<<"$counter_monotonic_report")"

configured_spread="$(metric_value "bot_strategy_configured_half_spread_ticks{service_name=\"b3-market-maker-bot\",symbol=\"$symbol\"}")"
effective_spread="$(metric_value "bot_strategy_effective_half_spread_ticks{service_name=\"b3-market-maker-bot\",symbol=\"$symbol\"}")"

case "$profile" in
    baseline)
        record_check "default-static-spread" \
            "$(numeric_test "$effective_spread == $configured_spread" && echo true || echo false)" \
            "effective=configured; disabled volatility series is not required" \
            "configured=$configured_spread effective=$effective_spread"
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
        additional_spread="$(metric_value "bot_strategy_volatility_additional_half_spread_ticks{service_name=\"b3-market-maker-bot\",symbol=\"$symbol\"}")"
        record_check "adaptive-spread-floor-cap-response" \
            "$(numeric_test "$additional_spread > 0 && $additional_spread <= 20 && $effective_spread == $configured_spread + $additional_spread" && echo true || echo false)" \
            "0<additional<=20 and effective=configured+additional" \
            "configured=$configured_spread effective=$effective_spread additional=$additional_spread"
        ;;
    pause-and-cancel)
        record_check "strict-symbol-refresh-continuity" "$strict_freshness_pass" \
            "every configured symbol has deterministic refresh trades with observed gaps <= interval+margin < MaxReferenceAge, and remains eligible/fresh outside intentional outage/reconnect phases" \
            "intervalSeconds=$strict_refresh_interval_seconds marginSeconds=$strict_refresh_margin_seconds maxReferenceAgeSeconds=$max_reference_age_seconds artifact=strict-freshness.json"
        record_check "pre-outage-submission-stability" "$pre_outage_stabilization_pass" \
            "mandatory series present and market-maker submitted-order counter unchanged across at least two full telemetry cycles while user refresh crosses keep references fresh" \
            "cycles=$pre_outage_stabilization_cycles intervalSeconds=$pre_outage_stabilization_interval_seconds refreshIntervalSeconds=$strict_refresh_interval_seconds artifact=pre-outage-stabilization.json"
        record_check "pause-and-cancel-outage" "$pause_outage_pass" \
            "settled eligibility/openOrders=0, required outage counters increase, submissions remain unchanged from pre-outage and quotes stay zero throughout hold" \
            "passed=$pause_outage_pass telemetry=$outage_telemetry_pass elapsedSeconds=$outage_cancellation_elapsed_seconds artifact=outage-telemetry.json"
        record_check "pause-and-cancel-reconnected-stale-epoch-hold" "$reconnect_stale_epoch_pass" \
            "after Connected and before a new market event, eligibility/openOrders remain zero and submissions unchanged for a full telemetry cycle" \
            "holdSeconds=$reconnect_stale_hold_seconds artifact=outage-telemetry.json"
        record_check "pause-and-cancel-outage-telemetry" "$outage_telemetry_pass" \
            "outage counters increase; submissions stay unchanged and open quotes remain zero through disconnected and reconnected-without-reference holds" \
            "artifact=outage-telemetry.json"
        record_check "pause-and-cancel-recovery" "$pause_recovery_pass" \
            "fresh current-epoch trade, eligibility=1, openOrders=2 for every configured symbol within timeout" \
            "passed=$pause_recovery_pass elapsedSeconds=$recovery_elapsed_seconds"
        ;;
esac

acceptance_candidate=false
eligibility_reasons='[]'
if ! $timing_eligible; then
    eligibility_reasons="$(jq -cn --argjson rows "$eligibility_reasons" \
        '$rows + ["timing below acceptance minimum"]')"
fi
if $git_dirty; then
    eligibility_reasons="$(jq -cn --argjson rows "$eligibility_reasons" \
        '$rows + ["checkout contains tracked modifications or untracked files"]')"
fi
if [[ -z "$suite_manifest" ]]; then
    eligibility_reasons="$(jq -cn --argjson rows "$eligibility_reasons" \
        '$rows + ["SOAK_SUITE_MANIFEST was not provided"]')"
elif ! $suite_compatible; then
    eligibility_reasons="$(jq -cn --argjson rows "$eligibility_reasons" \
        '$rows + ["suite compatibility was not verified"]')"
fi
if $timing_eligible && ! $git_dirty && $suite_compatible; then
    acceptance_candidate=true
fi

jq \
    --arg accountingPeriodStartedAtUtc "$accounting_period_started_at_utc" \
    --argjson acceptanceCandidate "$acceptance_candidate" \
    --argjson eligibilityReasons "$eligibility_reasons" '
      .accountingPeriodStartedAtUtc = $accountingPeriodStartedAtUtc |
      .acceptanceCandidate = $acceptanceCandidate |
      .eligibilityReasons = $eligibilityReasons
    ' "${artifacts_dir}/run.json" >"${artifacts_dir}/run.json.next"
mv "${artifacts_dir}/run.json.next" "${artifacts_dir}/run.json"

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
      passed: ($checks | all(.passed)),
      acceptanceEligible: ($run[0].acceptanceCandidate and ($checks | all(.passed))),
      evidenceClass: (
        if $run[0].acceptanceCandidate and ($checks | all(.passed))
        then "acceptance-profile"
        else "smoke"
        end
      )
    }' <"$checks_tsv" >"${artifacts_dir}/summary.json"

jq \
    --slurpfile summary "${artifacts_dir}/summary.json" '
      .acceptanceEligible = $summary[0].acceptanceEligible |
      .evidenceClass = $summary[0].evidenceClass |
      .finishedAtUtc = $summary[0].finishedAtUtc
    ' "${artifacts_dir}/run.json" >"${artifacts_dir}/run.json.next"
mv "${artifacts_dir}/run.json.next" "${artifacts_dir}/run.json"

if ! jq -e '.passed' "${artifacts_dir}/summary.json" >/dev/null; then
    jq '.checks[] | select(.passed == false)' "${artifacts_dir}/summary.json" >&2
    exit 1
fi

log "all profile checks passed"
