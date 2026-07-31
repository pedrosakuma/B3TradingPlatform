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
export SOAK_ADMIN_USER=soak-admin
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
scripts/soak/test-market-maker-soak-lib.sh
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

The primary workload derives each actual order limit immediately before
submission from a raw live reference whose `updatedUtc` age is strictly less
than `MM_SOAK_MAX_REFERENCE_AGE`. The offset covers the configured half-spread,
the target symbol's worst-case inventory skew, the configured maximum
volatility additional half-spread when enabled, and
`SOAK_MARKETABLE_PRICE_EXTRA_TICKS` of crossing margin. It validates the result
against the effective percent/absolute collar returned by
`/api/admin/risk/limits` and fails rather than submitting a non-marketable
collar-bound order. It also fails explicitly when no fresh live reference is
available after a raw cache entry has been observed. Before the first market
data print, and only while the raw `.live` entry is missing rather than stale,
`SOAK_MARKETABLE_BUY_PRICE` / `SOAK_MARKETABLE_SELL_PRICE` bootstrap that entry
with an explicit warning. The profile process records the first valid primary
live reference in persistent shell state; after that observation, missing,
null, invalid, or stale raw values fail closed and cannot re-enable fallback
after a reconnect. Buy derivation crosses only the predicted ask side; sell
derivation crosses only the predicted bid side.
Each fill prints a real matching-engine trade into UMDF, moves the live
market-data reference, and gives the volatility estimator valid trade-to-trade
samples. No external price generator or synthetic ER injector is introduced.
This target-symbol workload remains independently recorded in `workload.csv`
for cross-profile fill/P&L comparability.

The inventory profile first makes the bot long by 12 lots, records positive
skew saturation, then reverses it through flat to 12 lots short and records
negative saturation. The feed profile stops only the isolated `marketdata`
service. Because strict mode cannot quote without a current-epoch reference,
the helper crosses all three symbols before the initial quote assertion and
continues deterministic same-price crosses for every configured symbol
throughout warmup, pre-outage stabilization, and duration. These maintenance
prints are recorded separately in `strict-refreshes.csv`/`.jsonl`; they do not
replace or change the alternating PETR4 primary workload.
Before each periodic maintenance cross, the helper uses its isolated local
diagnostics identity to read the existing trading-host live-reference endpoint
and crosses at that live price, inside the bot spread. Initial and post-outage
recovery crosses use the configured reference while strict quotes are absent.
This prevents a stale configured price from consuming a bot quote after the
primary PETR4 workload has moved the market. Refresh-cycle ownership alternates:
the trading identity buys one cycle and the counterparty buys the next. Both
identities receive the configured sandbox deposit, so the refresh stream remains
position- and cash-bounded over a multi-hour run.
After a primary target fill, the helper allows one full telemetry cycle for
the live reference to advance. It then moves the maintenance cross one tick
toward the spread interior, away from the potentially stale quote on the fill
side; if the primary print is not reference-eligible, it applies the same
interior-tick rule to the stable live reference. Strict mode schedules the next
all-symbol refresh before another primary target order, so target movement
cannot starve the other configured symbols. The default margin combines one
full telemetry cycle with the serial three-symbol execution budget; the
refresh interval is derived from the remaining `MaxReferenceAge` budget. The
helper rejects configuration unless
`refreshInterval + refreshMargin < MaxReferenceAge`.
After restart it repeats crosses between the configured trading and
counterparty identities at each rendered reference price. The helper fails
before startup if either identity has no unique rendered seed mapping or if the
resolved counterparty risk mapping is absent. This existing real-stack
technique makes every configured symbol prove fresh eligibility and exactly two
recovered quotes. A restarted UMDF consumer may discard the first post-gap
event while healing sequence state, so the helper emits three recorded cross
rounds, ten seconds apart, for all symbols. Only a last-trade reference younger
than the configured `MaxReferenceAge` plus two quotes for all symbols inside the
single recovery timeout passes.

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

Accelerated strict-freshness smoke (non-acceptance evidence) shortens the
freshness bound to 20 seconds and proves the three-symbol 5-second refresh loop:

```bash
export SOAK_TRADING_PASSWORD
MM_SOAK_MAX_REFERENCE_AGE=00:00:20 \
MM_SOAK_METRIC_EXPORT_INTERVAL_MS=1000 \
SOAK_STRICT_REFRESH_INTERVAL_SECONDS=5 \
SOAK_STRICT_REFRESH_MARGIN_SECONDS=12 \
SOAK_WARMUP_SECONDS=15 \
SOAK_DURATION_SECONDS=15 \
SOAK_SAMPLE_INTERVAL_SECONDS=2 \
SOAK_PROJECT_NAME=b3tp-719-smoke-strict-refresh \
  scripts/soak/run-market-maker-soak.sh --profile pause-and-cancel
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

# The suite runner clean-builds baseline, reuses the pinned images for later
# profiles, and exits immediately on the first non-zero profile result.
SOAK_SUITE_ID="$suite_id" scripts/soak/run-market-maker-soak-suite.sh
```

The configured tags are labels only and may be mutable. The first
acceptance-eligible run requires a clean checkout and Compose build, and records
`builtFromGitSha`, build mode, image IDs, and available repo digests in the
manifest's immutable `sourceBinding`. If that run fails before registration,
the manifest still has zero accepted runs and the retry must build again.
Subsequent `--no-build` profiles are accepted only when their full runtime image
identity list exactly matches the manifest. Do not rebuild, pull, or retag suite
images between profiles.

Useful controls:

| Variable | Default | Purpose |
|---|---:|---|
| `SOAK_WARMUP_SECONDS` | `60` | Workload before evidence sampling |
| `SOAK_DURATION_SECONDS` | `300` | Sampled evidence window |
| `SOAK_SAMPLE_INTERVAL_SECONDS` | `15` | Prometheus snapshot interval |
| `SOAK_WORKLOAD_INTERVAL_SECONDS` | `1` | Delay after each completed order |
| `MM_SOAK_MAX_REFERENCE_AGE` | `00:00:30` | Strict feed and P&L mark freshness bound (`HH:MM:SS`) |
| `SOAK_STRICT_REFRESH_INTERVAL_SECONDS` | derived (`7`) | Start-to-next-cycle delay for all-symbol strict refresh trades |
| `SOAK_STRICT_REFRESH_MARGIN_SECONDS` | telemetry cycle + cycle budget (`15`) | Required execution/sampling margin below `MaxReferenceAge` |
| `SOAK_STRICT_REFRESH_CYCLE_BUDGET_SECONDS` | `5` | Budget for the serial three-symbol paired-cross cycle |
| `SOAK_OUTAGE_SECONDS` | `20` | Feed hold-down after cancellation proof |
| `SOAK_RECOVERY_TIMEOUT_SECONDS` | `120` | Cancellation, reconnect hold, and fresh-recovery deadline |
| `SOAK_RECOVERY_CROSS_ATTEMPTS` | `3` | Recorded post-gap crosses per symbol |
| `SOAK_RECOVERY_CROSS_INTERVAL_SECONDS` | `10` | Delay between post-gap cross rounds |
| `SOAK_RECONNECT_STALE_HOLD_SECONDS` | OTLP export + scrape cycle (`10`) | Connected hold before any fresh recovery event |
| `SOAK_PRE_OUTAGE_STABILIZATION_CYCLES` | `2` | Consecutive full telemetry cycles with unchanged submissions |
| `SOAK_PRE_OUTAGE_STABILIZATION_INTERVAL_SECONDS` | OTLP export + scrape cycle (`10`) | Delay between stabilization samples |
| `SOAK_PRE_OUTAGE_STABILIZATION_TIMEOUT_SECONDS` | derived (`60`) | Deadline to obtain stable pre-outage submissions |
| `SOAK_INVENTORY_BIAS_LOTS` | `12` | Long/short reversal magnitude |
| `SOAK_QUANTITY` | `100` | PETR4 workload order quantity |
| `SOAK_MARKETABLE_BUY_PRICE` | `32.80` | Initial bootstrap buy used only while raw `.live` is missing, never stale |
| `SOAK_MARKETABLE_SELL_PRICE` | `29.30` | Initial bootstrap sell used only while raw `.live` is missing, never stale |
| `SOAK_MARKETABLE_PRICE_EXTRA_TICKS` | `1` | Crossing margin beyond configured/adaptive half-spread + worst-case skew |
| `SOAK_REFERENCE_CROSS_PRICE` | `30.00` | PETR4 feed-recovery cross |
| `SOAK_DEPOSIT_AMOUNT` | `100000.00` | Trading-user sandbox deposit |
| `SOAK_COUNTERPARTY_DEPOSIT_AMOUNT` | same as trading user (`100000.00`) | Counterparty sandbox deposit for alternating refresh ownership |
| `SOAK_TRADING_USER` | `alice` | Trading workload identity; must resolve to one rendered seed slot |
| `SOAK_COUNTERPARTY_USER` | `bob` | Distinct cross-printing identity; must resolve to one rendered seed slot/risk mapping |
| `SOAK_ADMIN_USER` | `soak-admin` | Isolated local diagnostics identity used only to resolve live refresh prices |
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

At process start the helper copies `SOAK_TRADING_PASSWORD` and
`SOAK_COUNTERPARTY_PASSWORD` into private, non-exported shell variables and
immediately unsets both exported names before its first child process. Login JSON
and bearer headers are delivered to `curl` through anonymous, process-local file
descriptors. Passwords and tokens are never placed in a child environment,
`curl` argument list, or temporary file. The helper disables shell xtrace,
checks every sensitive curl launch, and verifies the live `docker events`
process environment through `/proc`. Errors and evidence contain identities but
never credentials.

The pre-run `docker compose down -v --remove-orphans` is a required isolation
barrier: failure aborts before build/start. Final cleanup attempts event-monitor
shutdown, log capture, and Compose teardown even if an earlier cleanup step
fails. `cleanup.json`/`cleanup-errors.json` retain every failure. A teardown
failure changes an otherwise passing `summary.json` to `passed=false`,
`acceptanceEligible=false`, exits nonzero, and prevents suite-manifest
registration. When the workload already failed, its original exit status is
preserved while cleanup failures remain in evidence.

Both evidence path controls are canonicalized before any directory or file is
created. They must resolve under this checkout's ignored `soak-artifacts/`
directory. The helper rejects `..` traversal, external absolute paths, symlink
escapes, symlink overwrite targets, a manifest inside its profile artifact
directory, non-file/non-directory targets, and stale atomic `.next` targets.
Evidence records repository-relative paths, so manifests remain portable.

Manual cleanup is:

```bash
# REQUIRED: replace this with the exact project name printed by the failed run.
failed_project_name='<exact-project-name-from-failed-run>'
[[ "$failed_project_name" != '<exact-project-name-from-failed-run>' ]] || {
  echo 'Set failed_project_name to the failed run project before cleanup.' >&2
  exit 2
}
MM_SOAK_COUNTERPARTY_USER="${SOAK_COUNTERPARTY_USER:-bob}" \
MM_SOAK_ADMIN_USER="${SOAK_ADMIN_USER:-soak-admin}" \
MM_SOAK_PROJECT_NAME="$failed_project_name" \
docker compose \
  --project-name "$failed_project_name" \
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

Prometheus absence is never interpreted as numeric zero. Every sample also
writes `metric-presence.csv`/`.jsonl`, with separate `present`, `seriesCount`,
and `value` fields for each mandatory metric/symbol/phase. A present series with
value `0` is valid; an absent or non-numeric series fails immediately. The bot
publishes bounded zero baselines for configured-symbol counters and zero
position/realized-P&L gauges so integrity checks can distinguish healthy zero
from missing telemetry. Unrealized/total P&L still remain absent when no fresh
mark exists. The only phase-specific exception is that
`bot.pnl.unrealized`/`bot.pnl.total` are not mandatory during
`outage-settled`, `outage-hold`, or `reconnected-no-reference`, where the
fresh-mark contract intentionally suppresses them. Position, realized P&L,
open-order, spread, accounting, feed, corruption, and safety series remain
mandatory; `reference_age_seconds` becomes mandatory again during
`reconnected-no-reference`.

Required comparison fields:

| Evidence | Source |
|---|---|
| Net quantity, average entry | `bot.position.net_quantity`, `bot.position.average_entry_price` |
| Accounting start | `[mm-pnl] accountingPeriodStartedAtUtc` |
| Realized/unrealized/total gross P&L | `bot.pnl.realized`, `bot.pnl.unrealized`, `bot.pnl.total`; `[mm-pnl]` snapshot |
| Configured/effective spread | `bot.strategy.configured_half_spread_ticks`, `bot.strategy.effective_half_spread_ticks` |
| Volatility estimate/addition | `bot.strategy.volatility_move_estimate_ticks`, `bot.strategy.volatility_additional_half_spread_ticks`; `[mm-volatility]` |
| Inventory skew | `bot.strategy.inventory_skew_ticks` |
| Feed eligibility/age/source | `bot.market_data.reference_eligible_current` for stable state checks, `bot.market_data.reference_eligible` for bounded reason diagnostics, `bot.market_data.reference_age_seconds`; `[mm-feed]` |
| Fills/rejects/cancels | `bot.fills.received`, `bot.pnl.fills_applied`, `bot.orders.rejected`, `bot.orders.cancelled` |
| Accounting corruption | unknown, duplicate, invalid, inconsistent, and delta-mismatch P&L counters |
| Open orders/safety | `bot.orders.open`, `bot.orders.safety_cap_hit`, cancel/requote/restore error counters, and `bot.orders.cancel_ack_expired`; every non-zero error or ACK timeout fails the soak gate |

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
  are present for every required scope and all remain zero.
- Safety-cap hits, quote submit failures, quote rejects, and cancel
  reject/submit-failure counters are present and remain zero.
- Every phase contains all mandatory open-order, spread, position,
  P&L/accounting, corruption/safety, and profile-specific feed/strategy series
  for every configured symbol. Missing telemetry fails even when zero would
  otherwise satisfy a numeric threshold.
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
  events for every other critical service. The monitor process itself must
  remain alive until the helper requests shutdown; an earlier exit fails the
  lifecycle and cleanup evidence.
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

- Before `pre-outage`, the helper requires all mandatory series and waits until
  the submitted-order counter is unchanged across at least two consecutive
  full OTLP export plus Prometheus scrape cycles. The derived cycle is 10
  seconds for the checked-in 5-second exporter and 5-second scrape. A configured
  interval cannot be shorter than that cycle. Strict refresh crosses continue
  during this boundary: they are end-client orders, while the stabilized
  counter is the market-maker bot's own quote-submission counter. Same-price
  refreshes must not cause bot re-quotes; any observed bot submission change
  restarts/fails stabilization naturally.
- The helper captures explicit `pre-outage`, `outage-settled`, `outage-hold`,
  `reconnected-no-reference`, and `recovered` telemetry boundaries.
  `outage-settled` begins only after
  bounded asynchronous cancellation has reached eligibility `0` and open
  orders `0` for all three symbols within the configured 120-second default
  deadline.
- Unavailable availability-transition, disconnected quote-suppression, and
  `FeedUnavailable` cancel counters must be present and increase from their
  pre-outage values. Cancel error/corruption counters remain zero.
- The order-submission counter is unchanged from the pre-outage boundary through
  the settled boundary and every hold sample. Every hold sample has eligibility
  `0` and exactly zero open quotes. No fresh recovery print is sent until the
  hold completes.
- After marketdata and the bot report connected, no recovery cross is sent for
  at least one complete export-plus-scrape cycle. At least two
  `reconnected-no-reference` samples must retain eligibility `0`, open orders
  `0`, and the pre-outage submitted-order count. This proves reconnect did not
  reuse a stale prior-epoch reference. Fresh-mark-dependent unrealized/total
  P&L may be absent in this phase; all other phase-required telemetry must be
  present. Any required Prometheus request, normalization, presence check, or
  phase snapshot failure aborts the phase rather than relying on Bash
  `errexit`.
- Restart alone is not accepted as recovery. Only after the reconnect hold does
  the helper generate fresh current-epoch trades; then
  eligibility becomes `1`, a
  `reference_age_seconds{exported_source="last_trade_price"} <
  MaxReferenceAge` sample
  appears, and every configured symbol returns to `open=2` within the configured
  recovery deadline.
- `reference_age_seconds` resets to a fresh value and `[mm-feed]` records
  source plus the unavailable-to-available transition.
- Outside `outage-settled`, `outage-hold`, and
  `reconnected-no-reference`, every configured symbol must have
  `reference_eligible_current=1` and `reference_age_seconds <
  MaxReferenceAge` at every sample. Per-symbol refresh timestamps must cover
  both pre-outage and post-recovery continuity windows with no observed gap
  above `refreshInterval + refreshMargin`, which itself must remain strictly
  below `MaxReferenceAge`. A stopped refresh stream therefore fails even if a
  later event happens to restore final quotes.

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
- `suite-source-binding.json` — first-build or pinned-image policy evaluation;
- `credential-environment.json` — secret-name/value scrubbing and child-environment checks;
- `runtime.csv` / `runtime.jsonl` — container IDs, image IDs, start time, restart count, and state;
- `runtime-events.jsonl` / `runtime-lifecycle.json` — Docker lifecycle events and exact transition counts;
- `runtime-continuity.json` — per-critical-service identity/start/status proof;
- `marketdata-transition.json` — the sole permitted outage stop/start (strict profile only);
- `pre-outage-stabilization.json` — mandatory counter presence and the consecutive stable-cycle window;
- `samples.csv` / `samples.jsonl` — normalized present metric samples with accounting-period identity;
- `metric-presence.csv` / `metric-presence.jsonl` — mandatory-series presence/value evidence;
- `outage-telemetry.json` — strict-profile phase boundaries, counters, submissions, eligibility, and open orders;
- `workload.csv` — order/fill latency evidence;
- `submit-failures.jsonl` — secret-redacted failed order POST status/body,
  resolved reference/limit/collar context, phase, side, quantity, and expected
  workload sequence;
- `strict-refreshes.csv` / `strict-refreshes.jsonl` — per-symbol maintenance
  trade timestamps, counts, alternating direction/identities, price source,
  prices, quantities, and both end-client ClOrdIDs;
- `strict-freshness.json` — per-symbol refresh counts/timestamps/gaps and every
  non-outage eligibility/reference-age observation;
- `counter-monotonicity.json` — any decreasing tracked counter series;
- `open-order-bounds.json` — per-symbol and total maxima across all samples;
- `summary.json` — machine-readable checks;
- `cleanup.json` / `cleanup-errors.json` — original status and aggregated cleanup outcome;
- `pre-run-compose-down.log` / `cleanup-compose-down.log` — isolation teardown evidence;
- `compose-ps.json` and `compose.log` — runtime state and diagnostics.

The shared `suite-manifest.json` pins the first profile's compatibility object
and adds one accepted run per profile. Neither the rendered projection nor any
other artifact contains passwords, tokens, signing keys, password hashes,
salts, or the bot access key.
The suite runner additionally writes `suite-run.log` beside the shared
manifest, preserving profile stdout/stderr and the original profile exit code
even when `set -e` stops orchestration.

Use this summary shape (generated values only; never fabricate results):

```json
{
  "schemaVersion": "8",
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
    "recoveryTimeoutSeconds": 120,
    "recoveryCrossAttempts": 3,
    "recoveryCrossIntervalSeconds": 10,
    "metricExportIntervalMilliseconds": 5000,
    "prometheusScrapeIntervalSeconds": 5,
    "fullTelemetryCycleSeconds": 10,
    "maxReferenceAge": "00:00:30",
    "maxReferenceAgeSeconds": 30,
    "strictRefreshIntervalSeconds": 7,
    "strictRefreshMarginSeconds": 15,
    "strictRefreshCycleBudgetSeconds": 5,
    "preOutageStabilizationCycles": 2,
    "preOutageStabilizationIntervalSeconds": 10,
    "preOutageStabilizationTimeoutSeconds": 60,
    "reconnectStaleHoldSeconds": 10,
    "withGrafana": false
  },
  "execution": {
    "buildImages": true,
    "buildMode": "clean-checkout-compose-build",
    "builtFromGitSha": "<same 40-hex commit>",
    "keepStack": false
  },
  "workload": {
    "identities": {
      "trading": {"seedIndex": 0, "username": "<trading-user>", "firm": "<firm>", "role": "<role>"},
      "counterparty": {"seedIndex": 5, "username": "<counterparty-user>", "firm": "<firm>", "role": "<role>"},
      "diagnosticsAdmin": {"seedIndex": 8, "username": "<admin-user>", "firm": "FIRM01", "role": "admin"},
      "risk": {"counterparty": "<counterparty-user>", "allowShortSell": true}
    },
    "primaryTarget": {
      "symbol": "PETR4",
      "quantity": 100,
      "marketableBuyPrice": 32.8,
      "marketableSellPrice": 29.3,
      "intervalSeconds": 1,
      "evidenceArtifact": "workload.csv"
    },
    "strictRefresh": {
      "intervalSeconds": 7,
      "marginSeconds": 15,
      "cycleBudgetSeconds": 5,
      "maxReferenceAge": "00:00:30",
      "maxReferenceAgeSeconds": 30,
      "evidenceArtifacts": [
        "strict-refreshes.csv",
        "strict-refreshes.jsonl",
        "strict-freshness.json"
      ]
    },
    "inventoryBiasLots": 12,
    "deposits": {"tradingUser": 100000, "counterpartyUser": 100000},
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
  "schemaVersion": "2",
  "suiteId": "b3tp-719-<commit>-01",
  "expectedProfiles": [
    "baseline",
    "inventory-skew",
    "volatility-spread",
    "pause-and-cancel"
  ],
  "sourceBinding": {
    "builtFromGitSha": "<40-hex clean checkout commit>",
    "buildMode": "clean-checkout-compose-build",
    "buildImages": true,
    "firstRunProfile": "baseline",
    "builtServices": ["trading-host", "market-maker-bot", "alert-receiver"],
    "runtimeImages": [
      {
        "service": "trading-host",
        "configuredImage": "<suite tag>",
        "imageId": "sha256:<actual image ID>",
        "repoDigests": ["<digest when available>"]
      }
    ]
  },
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
