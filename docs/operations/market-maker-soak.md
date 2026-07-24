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
- a clean checkout at the recorded commit, including no untracked files;
- `SOAK_SUITE_MANIFEST` set to one shared path below `soak-artifacts/`;
- the same timing, workload controls, rendered common configuration, configured
  symbols, and actual runtime image IDs as the suite manifest.

The helper creates the suite manifest on the first qualifying profile and
compares every later profile byte-for-byte against its compatibility section.
It sets a profile's `acceptanceEligible` only after that run passes. It sets
`suiteAcceptanceEligible` only after all four expected profiles have passed.
An isolated run without the manifest is always a smoke, even if it runs for two
hours.

Do not label a short run or CI conformance run as the required multi-hour
baseline. Capture the baseline after #716 and before making any P&L-improvement
claim about #717/#718.

## Prerequisites and isolation

- Linux Docker Engine with Compose v2, `curl`, `jq`, and GNU `realpath`;
- enough resources for matching, market data, trading host, bot, Collector, and
  Prometheus;
- `docker/.env` populated as described in [DOCKER.md](../DOCKER.md), or the same
  required Compose values exported by the operator;
- `SOAK_TRADING_PASSWORD` exported in the invoking shell. The helper never
  writes it to evidence;
- a clean exact checkout; the helper records `gitClean`/`gitDirty` from the full
  `git status --porcelain` result. Ignored `soak-artifacts/` output does not
  dirty the checkout.

The soak overlay replaces fixed container, network, and volume names with
`MM_SOAK_PROJECT_NAME`-prefixed names. It also uses dedicated default host
ports and a dedicated `192.168.64.0/24` subnet to avoid dependence on Docker's
finite automatic address pools. Give every run a unique project name:

```bash
export SOAK_PROJECT_NAME=b3tp-719-baseline-01
export SOAK_TRADING_USER=alice
export SOAK_COUNTERPARTY_USER=bob
export SOAK_TRADING_PASSWORD
```

Change `SOAK_*_PORT` variables if the defaults conflict. Never point this
sandbox at production credentials or a production venue. Set `SOAK_SUBNET` to
another unused CIDR if the default overlaps an operator network; keep it
identical across a comparison suite.

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
- pins `Trading__Auth__Mode=Local` and local login for this sandbox workload,
  even if the operator shell exports `AUTH_MODE=Entra`;
- resolves `SOAK_TRADING_USER` and `SOAK_COUNTERPARTY_USER` into distinct seed
  slots and enables short selling only for that configured isolated
  counterparty, matching the existing real-conformance test policy;
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
3. complete one buy/sell bootstrap round trip so the process-local ledger has a
   position entry and emits its accounting-period timestamp;
4. submit one-lot PETR4 marketable limits through `POST /api/orders`;
5. wait for each order to fill against the bot before sending the next.

Alternating `Buy @ 32.80` and `Sell @ 29.30` consumes the bot's own resting
quotes. Each fill prints a real matching-engine trade into UMDF, moves the live
market-data reference, and gives the volatility estimator valid trade-to-trade
samples. No external price generator or synthetic ER injector is introduced.

The inventory profile first makes the bot long by 12 lots, records positive
skew saturation, then reverses it through flat to 12 lots short and records
negative saturation. The feed profile stops only the isolated `marketdata`
service. Because strict mode cannot quote without a current-epoch reference,
the helper crosses all three symbols once before the initial quote assertion.
After restart it repeats crosses between the configured trading and
counterparty identities at each rendered reference price. The helper fails
before startup if either identity has no unique rendered seed mapping or if the
resolved counterparty risk mapping is absent. This existing real-stack
technique makes every configured symbol prove fresh eligibility and exactly two
recovered quotes. A restarted UMDF consumer may discard the first post-gap
event while healing sequence state, so the helper emits three recorded cross
rounds, ten seconds apart, for all symbols. Only a fresh `<15s` last-trade
reference plus two quotes for all symbols inside the single recovery timeout
passes.

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
export SOAK_OUTAGE_SECONDS=60
image_tag="$(git rev-parse --short=12 HEAD)"
suite_id="b3tp-719-${image_tag}-01"
export SOAK_SUITE_MANIFEST="soak-artifacts/${suite_id}/suite-manifest.json"
export SOAK_TRADING_IMAGE="b3tp-719-trading-host:${image_tag}"
export SOAK_MARKET_MAKER_BOT_IMAGE="b3tp-719-market-maker-bot:${image_tag}"
export SOAK_ALERT_RECEIVER_IMAGE="b3tp-719-alert-receiver:${image_tag}"

# Build the exact checkout once.
SOAK_PROJECT_NAME=b3tp-719-baseline-01 \
SOAK_ARTIFACTS_DIR="soak-artifacts/${suite_id}/baseline" \
  scripts/soak/run-market-maker-soak.sh --profile baseline

# Reuse the same images. The helper compares actual sha256 image IDs, not tags.
SOAK_PROJECT_NAME=b3tp-719-inventory-01 \
SOAK_ARTIFACTS_DIR="soak-artifacts/${suite_id}/inventory-skew" \
  scripts/soak/run-market-maker-soak.sh --profile inventory-skew --no-build

SOAK_PROJECT_NAME=b3tp-719-volatility-01 \
SOAK_ARTIFACTS_DIR="soak-artifacts/${suite_id}/volatility-spread" \
  scripts/soak/run-market-maker-soak.sh --profile volatility-spread --no-build

SOAK_PROJECT_NAME=b3tp-719-feed-01 \
SOAK_ARTIFACTS_DIR="soak-artifacts/${suite_id}/pause-and-cancel" \
  scripts/soak/run-market-maker-soak.sh --profile pause-and-cancel --no-build
```

The configured tags are labels only and may be mutable. Acceptance is pinned to
the `sha256:` IDs obtained from the running containers. If `latest` or any
other tag resolves to a different image between profiles, the helper fails the
suite compatibility check. Do not rebuild, pull, or retag suite images between
profiles.

Useful controls:

| Variable | Default | Purpose |
|---|---:|---|
| `SOAK_WARMUP_SECONDS` | `60` | Workload before evidence sampling |
| `SOAK_DURATION_SECONDS` | `300` | Sampled evidence window |
| `SOAK_SAMPLE_INTERVAL_SECONDS` | `15` | Prometheus snapshot interval |
| `SOAK_WORKLOAD_INTERVAL_SECONDS` | `1` | Delay after each completed order |
| `SOAK_OUTAGE_SECONDS` | `20` | Feed hold-down after cancellation proof |
| `SOAK_RECOVERY_TIMEOUT_SECONDS` | `90` | Cancellation/fresh-recovery deadline |
| `SOAK_RECOVERY_CROSS_ATTEMPTS` | `3` | Recorded post-gap crosses per symbol |
| `SOAK_RECOVERY_CROSS_INTERVAL_SECONDS` | `10` | Delay between post-gap cross rounds |
| `SOAK_INVENTORY_BIAS_LOTS` | `12` | Long/short reversal magnitude |
| `SOAK_QUANTITY` | `100` | PETR4 workload order quantity |
| `SOAK_MARKETABLE_BUY_PRICE` | `32.80` | Marketable workload buy limit |
| `SOAK_MARKETABLE_SELL_PRICE` | `29.30` | Marketable workload sell limit |
| `SOAK_REFERENCE_CROSS_PRICE` | `30.00` | PETR4 feed-recovery cross |
| `SOAK_DEPOSIT_AMOUNT` | `100000.00` | Trading-user sandbox deposit |
| `SOAK_COUNTERPARTY_DEPOSIT_AMOUNT` | `0` | Counterparty sandbox deposit |
| `SOAK_TRADING_USER` | `alice` | Trading workload identity; must resolve to one rendered seed slot |
| `SOAK_COUNTERPARTY_USER` | `bob` | Distinct cross-printing identity; must resolve to one rendered seed slot/risk mapping |
| `SOAK_ARTIFACTS_DIR` | `soak-artifacts/<run-id>` | Ignored evidence directory |
| `SOAK_SUITE_MANIFEST` | unset | Shared closure-suite manifest; required for acceptance |
| `SOAK_KEEP_STACK` | `false` | Keep isolated containers/volumes for inspection |
| `SOAK_BUILD_IMAGES` | `true` | Build the host, bot, and local alert receiver from the checked-out commit |
| `SOAK_WITH_GRAFANA` | `false` | Include Grafana; value is suite-comparable |
| `SOAK_SUBNET` | `192.168.64.0/24` | Isolated Compose subnet; suite-comparable |
| `SOAK_TRADING_IMAGE` | project-local tag | Host image/tag to build or reuse |
| `SOAK_MARKET_MAKER_BOT_IMAGE` | project-local tag | Bot image/tag to build or reuse |
| `SOAK_ALERT_RECEIVER_IMAGE` | project-local tag | Local receiver image/tag to build or reuse |

The helper exits nonzero on a failed check, captures logs, and tears down its
isolated project and volumes unless `--keep-stack`/`SOAK_KEEP_STACK=true` is
set. It refuses a non-empty artifact directory so stale evidence cannot be
mixed into a rerun.

Both evidence path controls are canonicalized before any directory or file is
created. They must resolve under this checkout's ignored `soak-artifacts/`
directory. The helper rejects `..` traversal, external absolute paths, symlink
escapes, symlink overwrite targets, a manifest inside its profile artifact
directory, non-file/non-directory targets, and stale atomic `.next` targets.
Evidence records repository-relative paths, so manifests remain portable.

Manual cleanup is:

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

- `bot.orders.open <= 2` for every configured symbol and total `<= 6` during
  the run; PETR4 has exactly two open quotes in at least 90% of sampled
  eligible time.
- Successful completion requires exactly two open quotes for each of PETR4,
  VALE3, and ITUB4, exactly six total. The strict `PauseAndCancel` profile also
  requires exported eligibility `1` for all three after recovery. Static
  profiles intentionally do not export the strict-policy eligibility gauge.
- Every submitted workload order fills within 20 seconds.
- `bot.fills.received == bot.pnl.fills_applied > 0`.
- Unknown, duplicate, invalid, inconsistent, and fill-delta-mismatch counters
  all remain zero.
- Safety-cap hits, quote submit failures, quote rejects, and cancel
  reject/submit-failure counters remain zero.
- Every sampled critical service (`trading-host`, `matching-platform`,
  `marketdata`, `market-maker-bot`, `otel-collector`, and `prometheus`) retains
  its initial container ID and image ID with `restartCount=0`. `StartedAt`
  remains unchanged for every service except the one intentional marketdata
  stop/start in `PauseAndCancel`. The helper has no persistent workload-helper
  container.
- `PauseAndCancel` records explicit before-stop, stopped, and after-start
  marketdata snapshots. They must show the same container/image,
  `restartCount=0`, statuses `running -> exited -> running`, and exactly two
  distinct `StartedAt` values. A Docker event monitor additionally requires
  exactly one post-baseline marketdata `die`/`start` pair and zero lifecycle
  events for every other critical service.
- Every metric sample contains the same non-empty
  `accountingPeriodStartedAtUtc`. A changed bot process/ledger fails the run.
- Every tracked `*_total` series is monotonically non-decreasing across all
  warmup, profile-event, duration, and final samples. A reset cannot erase an
  earlier reject, cancellation error, or accounting-corruption event.
- P&L never reports stale/missing unrealized P&L as numeric zero.

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

- On feed stop, eligibility and open orders reach `0` for all three configured
  symbols within 90 seconds.
- No new quote is submitted while ineligible; suppression/feed-cancel counters
  increase without cancel errors or accounting corruption.
- Restart alone is not accepted as recovery. After fresh current-epoch trades,
  eligibility becomes `1`, a
  `reference_age_seconds{exported_source="last_trade_price"} < 15` sample
  appears, and every configured symbol returns to `open=2` within 90 seconds.
- `reference_age_seconds` resets to a fresh value and `[mm-feed]` records
  source plus the unavailable-to-available transition.

The bundled Prometheus scrape target already uses a `source` label, so its
translation renames the instrument's bounded OTel `source` attribute to
`exported_source`. The helper normalizes `exported_source` back into the
`source` CSV column while retaining both raw labels in JSONL.

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
- `rendered-config.json` — allow-listed, secret-free rendered auth/strategy configuration;
- `runtime-images.json` — configured references, actual image IDs, and available repo digests;
- `compatibility.json` — exact cross-profile comparison input;
- `runtime.csv` / `runtime.jsonl` — container IDs, image IDs, start time, restart count, and state;
- `runtime-events.jsonl` / `runtime-lifecycle.json` — Docker lifecycle events and exact transition counts;
- `runtime-continuity.json` — per-critical-service identity/start/status proof;
- `marketdata-transition.json` — the sole permitted outage stop/start (strict profile only);
- `samples.csv` / `samples.jsonl` — normalized metric samples with accounting-period identity;
- `workload.csv` — order/fill latency evidence;
- `counter-monotonicity.json` — any decreasing tracked counter series;
- `open-order-bounds.json` — per-symbol and total maxima across all samples;
- `summary.json` — machine-readable checks;
- `compose-ps.json` and `compose.log` — runtime state and diagnostics.

The shared `suite-manifest.json` pins the first profile's compatibility object
and adds one accepted run per profile. Neither the rendered projection nor any
other artifact contains passwords, tokens, signing keys, password hashes,
salts, or the bot access key.

Use this summary shape (generated values only; never fabricate results):

```json
{
  "schemaVersion": "3",
  "runId": "20260724T180000Z-baseline",
  "profile": "baseline",
  "gitSha": "<40-hex commit>",
  "gitClean": true,
  "gitDirty": false,
  "configuredSymbols": ["PETR4", "VALE3", "ITUB4"],
  "images": {
    "trading-host": "sha256:<actual-runtime-image-id>",
    "market-maker-bot": "sha256:<actual-runtime-image-id>",
    "matching-platform": "sha256:<actual-runtime-image-id>",
    "marketdata": "sha256:<actual-runtime-image-id>",
    "otel-collector": "sha256:<actual-runtime-image-id>",
    "prometheus": "sha256:<actual-runtime-image-id>"
  },
  "startedAtUtc": "<ISO-8601>",
  "finishedAtUtc": "<ISO-8601>",
  "settings": {
    "warmupSeconds": 900,
    "durationSeconds": 7200,
    "sampleIntervalSeconds": 15,
    "workloadIntervalSeconds": 1,
    "fillTimeoutSeconds": 20,
    "outageSeconds": 60,
    "recoveryTimeoutSeconds": 90,
    "recoveryCrossAttempts": 3,
    "recoveryCrossIntervalSeconds": 10,
    "withGrafana": false
  },
  "execution": {"buildImages": true, "keepStack": false},
  "workload": {
    "identities": {
      "trading": {"seedIndex": 0, "username": "<trading-user>", "firm": "<firm>", "role": "<role>"},
      "counterparty": {"seedIndex": 5, "username": "<counterparty-user>", "firm": "<firm>", "role": "<role>"},
      "risk": {"counterparty": "<counterparty-user>", "allowShortSell": true}
    },
    "symbol": "PETR4",
    "quantity": 100,
    "marketableBuyPrice": 32.8,
    "marketableSellPrice": 29.3,
    "referenceCrossPrice": 30,
    "inventoryBiasLots": 12,
    "deposits": {"tradingUser": 100000, "counterpartyUser": 0},
    "recoveryCrosses": [
      {"symbol": "PETR4", "quantity": 100, "referencePrice": 30},
      {"symbol": "VALE3", "quantity": 100, "referencePrice": 70},
      {"symbol": "ITUB4", "quantity": 100, "referencePrice": 32}
    ]
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
  "passed": true,
  "acceptanceEligible": true,
  "evidenceClass": "acceptance-profile"
}
```

Suite manifest completion shape:

```json
{
  "schemaVersion": "1",
  "suiteId": "b3tp-719-<commit>-01",
  "expectedProfiles": [
    "baseline",
    "inventory-skew",
    "volatility-spread",
    "pause-and-cancel"
  ],
  "compatibility": {
    "gitSha": "<40-hex commit>",
    "settings": "<identical timing controls>",
    "workload": "<identical workload controls>",
    "configuredSymbols": ["PETR4", "VALE3", "ITUB4"],
    "commonRenderedConfiguration": "<secret-free projection>",
    "runtimeImageIds": {"<service>": "sha256:<actual image ID>"}
  },
  "runs": {"<profile>": "<accepted run record>"},
  "suiteAcceptanceEligible": true
}
```

For every profile, post a Markdown table with run ID, commit, actual image IDs
and repo digests, controls, duration, artifact location/checksum, and every
threshold result. Include the final suite manifest and require
`suiteAcceptanceEligible=true`. Attach the non-secret JSON/CSV/log bundle
through the GitHub UI or link the retained operator artifact. Then add the same
evidence link to both issues:

```bash
gh issue comment 719 --body-file soak-artifacts/<run-id>/issue-comment.md
gh issue comment 713 --body-file soak-artifacts/<run-id>/issue-comment.md
```

Review logs before attachment. Never upload tokens, `docker/.env`, WAL,
snapshots, dumps, or unrelated user/account data. Do not close #719/#713 until
all four closure-eligible runs pass and the baseline/feature comparison is
attached.
