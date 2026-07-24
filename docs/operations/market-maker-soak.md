# Market-maker strategy soak

This runbook is the reproducible evidence procedure for
[#719](https://github.com/pedrosakuma/B3TradingPlatform/issues/719) and the
strategy RFC [#713](https://github.com/pedrosakuma/B3TradingPlatform/issues/713).
It compares the same real-stack workload across:

1. static/default baseline;
2. inventory skew enabled;
3. volatility-adaptive spread enabled;
4. `PauseAndCancel` feed outage and recovery.

It does **not** make the sandbox bot suitable for economic trading, prove net
profitability, add a multi-hour GitHub Actions job, or configure Azure. The
operator runs the multi-hour profiles outside normal CI and attaches genuine
evidence; this repository contains only the cloud-neutral OTLP contract.

## Acceptance run versus smoke

The helper defaults are deliberately short. They validate wiring and artifact
shape, and their `run.json` is classified as `smoke`.

Evidence is closure-eligible only when **each** profile has:

- warmup at least 600 seconds;
- evidence duration at least 7,200 seconds (two hours);
- sample interval at most 30 seconds;
- the same commit, images, symbol, quantity, prices, and workload interval.

Do not label a short run or CI conformance run as the required multi-hour
baseline. Capture the baseline after #716 and before making any P&L-improvement
claim about #717/#718.

## Prerequisites and isolation

- Linux Docker Engine with Compose v2, `curl`, and `jq`;
- enough resources for matching, market data, trading host, bot, Collector, and
  Prometheus;
- `docker/.env` populated as described in [DOCKER.md](../DOCKER.md), or the same
  required Compose values exported by the operator;
- `SOAK_TRADING_PASSWORD` exported in the invoking shell. The helper never
  writes it to evidence;
- the exact commit/image digests recorded before comparing profiles.

The soak overlay replaces fixed container, network, and volume names with
`MM_SOAK_PROJECT_NAME`-prefixed names. It also uses dedicated default host
ports. Give every run a unique project name:

```bash
export SOAK_PROJECT_NAME=b3tp-719-baseline-01
export SOAK_TRADING_PASSWORD
```

Change `SOAK_*_PORT` variables if the defaults conflict. Never point this
sandbox at production credentials or a production venue.

## Canonical Compose model

Every profile uses exactly these overlays, in this order:

```bash
compose=(
  docker compose
  --project-name "$SOAK_PROJECT_NAME"
  -f docker/docker-compose.yml
  -f docker/docker-compose.market-maker.yml
  -f docker/docker-compose.observability.yml
  -f docker/docker-compose.market-maker-soak.yml
)
```

`docker-compose.real.yml` is now a compatibility no-op and is not required.
Do not add `docker-compose.demo.yml`: it switches the host to the mock gateway.
The soak overlay:

- routes `b3-market-maker-bot` by OTLP/gRPC to the bundled Collector;
- leaves Azure exporters and credentials out of this repository;
- parameterizes the strategy/feed profile;
- isolates containers, network, trading/matching/bot state, and observability
  volumes.

Render without starting anything:

```bash
scripts/soak/run-market-maker-soak.sh --profile baseline --dry-run
scripts/soak/run-market-maker-soak.sh --profile inventory-skew --dry-run
scripts/soak/run-market-maker-soak.sh --profile volatility-spread --dry-run
scripts/soak/run-market-maker-soak.sh --profile pause-and-cancel --dry-run
```

## Workload

The helper reuses the same real-stack sandbox seam as
`MarketMakerLiquiditySpecTests`:

1. log in as the configured end client;
2. use sandbox-only `POST /api/balance/deposit`;
3. submit one-lot PETR4 marketable limits through `POST /api/orders`;
4. wait for each order to fill against the bot before sending the next.

Alternating `Buy @ 32.80` and `Sell @ 29.30` consumes the bot's own resting
quotes. Each fill prints a real matching-engine trade into UMDF, moves the live
market-data reference, and gives the volatility estimator valid trade-to-trade
samples. No external price generator or synthetic ER injector is introduced.

The inventory profile first makes the bot long by 12 lots, records positive
skew saturation, then reverses it through flat to 12 lots short and records
negative saturation. The feed profile stops only the isolated `marketdata`
service. After restart it prints an alice/bob end-client cross at `30.00`, the
existing real-stack technique for supplying a fresh current-epoch trade before
asserting quote recovery.

## Run the four profiles

Short functional smoke:

```bash
export SOAK_TRADING_PASSWORD
SOAK_WARMUP_SECONDS=30 \
SOAK_DURATION_SECONDS=60 \
SOAK_SAMPLE_INTERVAL_SECONDS=5 \
SOAK_PROJECT_NAME=b3tp-719-smoke-baseline \
  scripts/soak/run-market-maker-soak.sh --profile baseline
```

Closure-eligible example, repeated with a fresh project name for every profile:

```bash
export SOAK_TRADING_PASSWORD
export SOAK_WARMUP_SECONDS=900
export SOAK_DURATION_SECONDS=7200
export SOAK_SAMPLE_INTERVAL_SECONDS=15
export SOAK_WORKLOAD_INTERVAL_SECONDS=1
image_tag="$(git rev-parse --short=12 HEAD)"
export SOAK_TRADING_IMAGE="b3tp-719-trading-host:${image_tag}"
export SOAK_MARKET_MAKER_BOT_IMAGE="b3tp-719-market-maker-bot:${image_tag}"
export SOAK_ALERT_RECEIVER_IMAGE="b3tp-719-alert-receiver:${image_tag}"

# Build the exact checkout once.
SOAK_PROJECT_NAME=b3tp-719-baseline-01 \
  scripts/soak/run-market-maker-soak.sh --profile baseline

# Reuse the same image IDs for every comparison.
SOAK_PROJECT_NAME=b3tp-719-inventory-01 \
  scripts/soak/run-market-maker-soak.sh --profile inventory-skew --no-build

SOAK_PROJECT_NAME=b3tp-719-volatility-01 \
  scripts/soak/run-market-maker-soak.sh --profile volatility-spread --no-build

SOAK_PROJECT_NAME=b3tp-719-feed-01 \
SOAK_OUTAGE_SECONDS=60 \
  scripts/soak/run-market-maker-soak.sh --profile pause-and-cancel --no-build
```

Useful controls:

| Variable | Default | Purpose |
|---|---:|---|
| `SOAK_WARMUP_SECONDS` | `60` | Workload before evidence sampling |
| `SOAK_DURATION_SECONDS` | `300` | Sampled evidence window |
| `SOAK_SAMPLE_INTERVAL_SECONDS` | `15` | Prometheus snapshot interval |
| `SOAK_WORKLOAD_INTERVAL_SECONDS` | `1` | Delay after each completed order |
| `SOAK_OUTAGE_SECONDS` | `20` | Feed hold-down after cancellation proof |
| `SOAK_RECOVERY_TIMEOUT_SECONDS` | `90` | Cancellation/fresh-recovery deadline |
| `SOAK_INVENTORY_BIAS_LOTS` | `12` | Long/short reversal magnitude |
| `SOAK_ARTIFACTS_DIR` | `soak-artifacts/<run-id>` | Ignored evidence directory |
| `SOAK_KEEP_STACK` | `false` | Keep isolated containers/volumes for inspection |
| `SOAK_BUILD_IMAGES` | `true` | Build the host, bot, and local alert receiver from the checked-out commit |
| `SOAK_TRADING_IMAGE` | project-local tag | Host image/tag to build or reuse |
| `SOAK_MARKET_MAKER_BOT_IMAGE` | project-local tag | Bot image/tag to build or reuse |
| `SOAK_ALERT_RECEIVER_IMAGE` | project-local tag | Local receiver image/tag to build or reuse |

The helper exits nonzero on a failed check, captures logs, and tears down its
isolated project and volumes unless `--keep-stack`/`SOAK_KEEP_STACK=true` is
set. Manual cleanup is:

```bash
MM_SOAK_PROJECT_NAME="$SOAK_PROJECT_NAME" docker compose \
  --project-name "$SOAK_PROJECT_NAME" \
  -f docker/docker-compose.yml \
  -f docker/docker-compose.market-maker.yml \
  -f docker/docker-compose.observability.yml \
  -f docker/docker-compose.market-maker-soak.yml \
  down -v --remove-orphans
```

## Evidence fields

The authoritative instrument catalog is
[METRICS.md § Market-maker meter](../METRICS.md#market-maker-meter-b3tradingmarketmakerbot).
The helper records the OTel-to-Prometheus translated names (`.` becomes `_`;
counters add `_total`) in `samples.csv` and `samples.jsonl`.

Required comparison fields:

| Evidence | Source |
|---|---|
| Net quantity, average entry | `bot.position.net_quantity`, `bot.position.average_entry_price` |
| Accounting start | `[mm-pnl] accountingPeriodStartedAtUtc` |
| Realized/unrealized/total gross P&L | `bot.pnl.realized`, `bot.pnl.unrealized`, `bot.pnl.total`; `[mm-pnl]` snapshot |
| Configured/effective spread | `bot.strategy.configured_half_spread_ticks`, `bot.strategy.effective_half_spread_ticks` |
| Volatility estimate/addition | `bot.strategy.volatility_move_estimate_ticks`, `bot.strategy.volatility_additional_half_spread_ticks`; `[mm-volatility]` |
| Inventory skew | `bot.strategy.inventory_skew_ticks` |
| Feed eligibility/age/source | `bot.market_data.reference_eligible`, `bot.market_data.reference_age_seconds`; `[mm-feed]` |
| Fills/rejects/cancels | `bot.fills.received`, `bot.pnl.fills_applied`, `bot.orders.rejected`, `bot.orders.cancelled` |
| Accounting corruption | unknown, duplicate, invalid, inconsistent, and delta-mismatch P&L counters |
| Open orders/safety | `bot.orders.open`, `bot.orders.safety_cap_hit`, cancel/requote error counters |

`unrealized` and `total` are intentionally absent, not zero, when no fresh live
mark exists. Full quoted spread in ticks is twice the reported half-spread.

## Pass/fail contract

The helper writes every result to `summary.json`. For issue closure, also
inspect the complete time series against these unambiguous thresholds:

### Common

- `bot.orders.open <= 2` for every configured symbol and total `<= 6`; PETR4
  has exactly two open quotes in at least 90% of sampled eligible time and at
  the end of the profile.
- Every submitted workload order fills within 20 seconds.
- `bot.fills.received == bot.pnl.fills_applied > 0`.
- Unknown, duplicate, invalid, inconsistent, and fill-delta-mismatch counters
  all remain zero.
- Safety-cap hits, quote submit failures, quote rejects, and cancel
  reject/submit-failure counters remain zero.
- Containers have no unexpected restart loop; `compose-ps.json` and
  `compose.log` identify any restart/fault.
- P&L snapshots share one `accountingPeriodStartedAtUtc` per process and never
  report stale/missing unrealized P&L as numeric zero.

### Baseline

- Both feature flags are false and `StaticRefPrice` is selected.
- Inventory-skew and volatility-addition series are absent or zero.
- Effective half-spread equals configured half-spread (`5` ticks here).
- Quote-continuity/open-order thresholds above pass. This is the default
  compatibility checkpoint, not a profitability target.

### Inventory skew

- Volatility spread remains disabled.
- At `net_quantity >= +1000`, skew is `+5` ticks; at
  `net_quantity <= -1000`, skew is `-5` ticks.
- For intermediate observations, sign(skew) equals sign(net quantity), absolute
  skew is monotonic with absolute inventory, and `abs(skew) <= 5`.
- Both saturation snapshots retain two-sided quoting and no safety/error event.

### Volatility spread

- Inventory skew remains disabled.
- Before readiness, effective half-spread is never below the configured
  5-tick floor.
- After at least ten moving trade samples, additional ticks become positive.
- At every sample:
  `effective = configured + additional`,
  `0 <= additional <= 20`, hence `5 <= effective <= 25`.
- When the estimator reports a changed addition, `[mm-volatility]` and the
  gauge agree on `estimateTicks`, readiness, and `additionalTicks`.

### `PauseAndCancel`

- On feed stop, eligibility becomes `0` and all PETR4 bot quotes reach
  `open=0` within 90 seconds.
- No new quote is submitted while ineligible; suppression/feed-cancel counters
  increase without cancel errors or accounting corruption.
- Restart alone is not accepted as recovery. After the fresh current-epoch
  trade, eligibility becomes `1`, `reference_age_seconds{source="trade"}`
  appears, and PETR4 returns to `open=2` within 90 seconds.
- `reference_age_seconds` resets to a fresh value and `[mm-feed]` records
  source plus the unavailable-to-available transition.

## OTLP, Collector, and Azure Monitor contract

The source path is:

```text
b3-market-maker-bot
  -> OTLP/gRPC (original OTel names and attributes)
  -> operator Collector
  -> Azure Monitor exporter configured by b3deploy
```

This repository owns the first hop and the semantic contract only. `b3deploy`
owns managed identity/connection strings, Azure exporter configuration,
workbooks, dashboards, retention, and alert routing. No Azure credential or
Azure-specific exporter belongs in this repository.

The deployment Collector must preserve:

- original OTel instrument name, type, temporality, unit/value;
- resource attributes `service.name=b3-market-maker-bot`,
  `service.version`, and `deployment.environment`;
- metric dimensions exactly as documented in METRICS.md.

Recommended Azure/Grafana panel names:

- **MM Net Inventory and Average Entry**
- **MM Gross P&L — Realized / Unrealized / Total**
- **MM Configured vs Effective Half-Spread**
- **MM Volatility Estimate and Added Ticks**
- **MM Inventory Skew and Saturation**
- **MM Feed Eligibility and Reference Age**
- **MM Fill / Reject / Cancel Rates**
- **MM Open Orders and Safety Events**
- **MM Accounting Integrity Counters**

Recommended alert names:

- `MarketMakerAccountingCorruption` — any integrity-counter increase;
- `MarketMakerOpenOrdersUnbounded` — symbol above `2` or total above `6`;
- `MarketMakerQuoteContinuityLost` — eligible symbol below `2` for 90 seconds;
- `MarketMakerSafetyCapHit` — any increase;
- `MarketMakerFeedIneligible` — strict profile remains ineligible for 90 seconds;
- `MarketMakerPauseCancelFailed` — ineligible symbol retains open orders for 90 seconds;
- `MarketMakerAdaptiveSpreadOutOfBounds` — effective below configured or above configured + cap.

Group/filter by `deployment.environment` and `symbol`. Add `side` only to the
bounded order-rate panels. Bounded `reason`, `available`, and `source` are
appropriate for drill-down; never add ClOrdID, order ID, trade ID, account, or
exception text as dimensions.

## Artifact and issue template

The helper writes only under ignored `soak-artifacts/`:

- `run.json` — immutable inputs and classification;
- `samples.csv` / `samples.jsonl` — normalized metric samples;
- `workload.csv` — order/fill latency evidence;
- `summary.json` — machine-readable checks;
- `compose-ps.json` and `compose.log` — runtime state and diagnostics.

Use this summary shape (generated values only; never fabricate results):

```json
{
  "schemaVersion": "1",
  "runId": "20260724T180000Z-baseline",
  "profile": "baseline",
  "gitSha": "<40-hex commit>",
  "images": {
    "tradingHost": "<image@sha256:digest>",
    "marketMakerBot": "<image@sha256:digest>",
    "matching": "<image@sha256:digest>",
    "marketData": "<image@sha256:digest>"
  },
  "startedAtUtc": "<ISO-8601>",
  "finishedAtUtc": "<ISO-8601>",
  "settings": {
    "warmupSeconds": 900,
    "durationSeconds": 7200,
    "sampleIntervalSeconds": 15
  },
  "accountingPeriodStartedAtUtc": "<ISO-8601 from mm-pnl>",
  "checks": [
    {
      "id": "accounting-corruption-counters",
      "passed": true,
      "expected": "0",
      "observed": "0"
    }
  ],
  "passed": true
}
```

For every profile, post a Markdown table with run ID, commit, image digests,
duration, artifact location/checksum, and every threshold result. Attach the
non-secret JSON/CSV/log bundle through the GitHub UI or link the retained
operator artifact. Then add the same evidence link to both issues:

```bash
gh issue comment 719 --body-file soak-artifacts/<run-id>/issue-comment.md
gh issue comment 713 --body-file soak-artifacts/<run-id>/issue-comment.md
```

Review logs before attachment. Never upload tokens, `docker/.env`, WAL,
snapshots, dumps, or unrelated user/account data. Do not close #719/#713 until
all four closure-eligible runs pass and the baseline/feature comparison is
attached.
