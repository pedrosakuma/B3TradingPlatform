# RFC: Pre-trade risk v2

| Field    | Value                                          |
| -------- | ---------------------------------------------- |
| Status   | Implemented                                    |
| Tracking | [#38](https://github.com/pedrosakuma/B3TradingPlatform/issues/38) |
| Replaces | n/a (extends v1 in `B3.Trading.Application/Risk`) |

## 1. Context

`RiskPipeline` shipped in Phase 4 with four checks (kill-switch, max
quantity, position limit, price collar) and a config-driven
`RiskOptions` that resolves limits in the order `per-end-client → per-symbol → default`.
It works, it is exercised by the conformance suite, and it has not
caused a single production incident. But it is **v1**: every gap we
deferred to "after we have real customers on it" is now blocking
something concrete:

- Multi-firm support landed (#25, `/admin/firms`) but limits are still
  per-symbol/per-end-client. A firm can't be capped as a unit.
- `IMarginProvider` exists as an interface only — no implementation,
  no cache, no metric. Any conversation about onboarding a real firm
  starts with "and how do you check available margin?".
- Fat-finger validation lives in `frontend/js/validation.js`. A
  scripted client bypassing the UI gets none of it.
- Reference prices are static in `appsettings.json`. The price collar
  check therefore drifts from reality the moment the market moves.
- Limits are loaded once at boot via `IOptions<RiskOptions>.Value`.
  Changing a limit means a redeploy.

This RFC proposes the smallest set of changes that closes those gaps
without rewriting the pipeline or the `IRiskCheck` contract.

## 2. Goals

1. Make the pipeline **firm-aware** end-to-end (limits, kill-switch,
   margin, observability).
2. Promote `IMarginProvider` from interface to a real, plugged-in
   check with sensible defaults (cache + stub provider) — leaving room
   for a back-office adapter later.
3. Move every fat-finger guard that exists client-side to also exist
   server-side, with the backend as source of truth.
4. Tie reference prices to the live `MarketData` cache, with an
   appsettings fallback so the system still starts cold.
5. Make limit changes operationally cheap (hot-reload, debug
   endpoint).

## 3. Non-goals

- Full margin engine (intraday liquidation, cross-margining,
  per-instrument haircuts). v2 ships a plausible stub + cache; the
  real adapter is a follow-up RFC.
- Replacing `IRiskCheck` or the pipeline composition model.
- Persisting limits to a database. Limits stay in config (file or
  appsettings) until the persistence spike (#29) picks a store.
- A UI for editing limits. Hot-reload + debug endpoint first; UI
  follows if the operational pressure justifies it.
- Pre-trade controls specific to algo orders (parent/child notional,
  participation rate). That belongs to the algo orders RFC.
- **Derivatives, options, futures.** The domain (`OrderType` =
  `Limit`/`Market`, `Position.NetQuantity` as a linear inventory)
  models cash equities only — there is no underlying / strike /
  expiry / contract-multiplier / margin-by-greeks concept anywhere
  in the codebase. Adding those instruments requires a polymorphic
  `Instrument` model, a calendar, and a margin engine that knows
  about initial vs. maintenance margin. **Out-of-scope; tracked in a
  future RFC.**
- **T+N cash settlement (B3 real-world model).** The current system
  has no calendar, no D+0/D+1/D+2 projected-balance concept, no
  integration with a clearing house (B3-CCP). v2 deliberately
  assumes the simpler model below; T+N settlement is a future RFC.

## 3.1 Margin model assumed by v2

`MarginCheck` (slice 4) is designed for a **synchronous,
reserve-on-submit ledger** — same shape as a crypto spot exchange,
not a brokered T+2 cash market:

- On `OrderSubmittedEvent`: `available -= price · qty` for the
  end-client.
- On ER `Filled` / `Cancelled` / `Rejected`: release the unfilled
  portion of the reservation; partial fills release proportionally.
- Available balance is a single scalar per (end-client, currency)
  with no projection horizon.

**Why this model first:** it is the only one that fits the existing
domain (cash-equities-only, linear positions, no instrument
polymorphism, no calendar). It also requires no integration with
the clearing house, which keeps v2 fully self-contained.

**What it explicitly does not cover** (and the RFC that will revisit
it):

- T+2 settlement projections — future "pre-trade risk v3 / cash
  settlement" RFC, blocked on the persistence spike (#29) picking a
  store.
- Derivatives margin (initial/maintenance/variation, greeks,
  haircut by instrument) — future "derivatives support" RFC, blocked
  on the polymorphic `Instrument` domain change.

The `IMarginProvider` interface is intentionally narrow
(`GetAsync(EndClientKey) → MarginSnapshot`) so a future
T+N or derivatives-aware provider can replace the stub without
touching the pipeline or the synthetic-ER channel.

## 4. Design

### 4.1 Limit resolution (extended)

Today: `PerEndClient → PerSymbol → Default`.

Proposed:

```
PerEndClient → PerFirm → PerSymbol → Default
```

`PerFirm` is **new**. It is a sibling dictionary on `RiskOptions`
keyed by `firmId` (case-insensitive). The resolver still walks each
field independently and picks first-non-null, so a per-symbol cap can
coexist with a per-firm cap on a different field.

Rationale for putting `PerFirm` between `PerEndClient` and
`PerSymbol`: a firm-level cap is an operational tool (the broker's
ceiling) and should override symbol defaults but defer to anything an
ops engineer set explicitly per end-client.

### 4.2 Margin check

```
IMarginProvider
  Task<MarginSnapshot> GetAsync(EndClientKey, CancellationToken);

MarginSnapshot
  decimal AvailableNotional;
  DateTimeOffset AsOf;
```

Pipeline order:

1. `KillSwitchCheck`
2. `MarginCheck` (new — fail fast on insufficient funds before doing
   any per-symbol math)
3. `PositionLimitCheck`
4. `PriceCollarCheck`
5. `MaxQuantityCheck`
6. (new fat-finger checks — see §4.3)

Cache strategy: in-memory `MemoryCache` with a per-end-client TTL
(`Trading:Risk:Margin:CacheSeconds`, default 5). On miss, the cached
provider calls the underlying `IMarginProvider` (initially a stub
implementation `StaticMarginProvider` reading from
`Trading:Risk:Margin:Available`).

Reject reason on the synthetic ER: `MARGIN_INSUFFICIENT` with the
estimated notional in the text. Same channel as exchange ERs — the UI
doesn't care where the "no" came from (invariant from the v1 RFC,
preserved).

### 4.3 Fat-finger server-side

Three new checks, all driven by `RiskOptions`:

- `MinTickSizeCheck` — price must be a multiple of `TickSize`
  (per-symbol → default).
- `MinLotSizeCheck` — quantity must be a multiple of `LotSize`.
- `MaxNotionalPerOrderCheck` — promote the existing schema field
  `RiskLimits.MaxNotional` to a dedicated check (today the field is
  defined but no check enforces it).

`PriceCollarCheck` extended to support an absolute band
(`PriceCollarAbsolute`) in addition to the existing percentage. Both
apply if both are set; first violation wins.

The frontend keeps validating for fast feedback. Backend remains the
source of truth.

### 4.4 Reference prices

Today `RiskOptions.ReferencePrices` is a `Dictionary<string, decimal>`
read once. Proposed:

```
IReferencePrice
  bool TryGet(string symbol, out decimal price, out DateTimeOffset asOf);
```

A new `MarketDataReferencePrice` implementation reads the last trade
from the existing `MarketDataCache`. If the symbol is unknown or the
last trade is older than `MaxReferencePriceAgeSeconds` (default 60),
it falls back to the static appsettings map. If the static map is
also empty, the price collar check is **skipped** for that order
(logged at info, counted in metrics) — fail-open here is intentional
because cold-start with no MD must not block trading; ops gets paged
on the metric instead.

### 4.5 Hot-reload

Switch `RiskLimitsResolver` (and the new fat-finger / margin checks)
from `IOptions<RiskOptions>` to `IOptionsMonitor<RiskOptions>` and
read `.CurrentValue` on each request. ASP.NET Core already wires the
file watcher for appsettings; this is a small, well-trodden change.

Two new admin endpoints (gated by `role=admin`):

- `GET /admin/risk/limits?firmId=&endClient=&symbol=` returns the
  effective `RiskLimits` after resolution. Pure read, no side effects.
  Lets ops verify what the system actually thinks the cap is.
- `POST /admin/risk/reload` triggers a reload of the underlying
  configuration source. No-op for the appsettings provider (which
  reloads automatically) but a useful hook for the file/DB providers
  the persistence spike may pick later.

### 4.6 Observability

New metrics (OTel via the existing pipeline; meter name `B3.Trading`):

| Metric                                      | Type             | Tags              | Status       |
| ------------------------------------------- | ---------------- | ----------------- | ------------ |
| `trading.risk.refprice.lookups`             | counter          | symbol, source    | shipped (slice 5) |
| `trading.risk.refprice.staleness_seconds`   | observable gauge | symbol            | shipped (slice 5) |
| `trading.risk.collar.bypassed_no_reference` | counter          | symbol            | shipped (slice 5) |

`source` is one of `live` (live MD cache hit fresh under
`Trading:MarketData:MaxStaleness`), `fallback` (live missed/stale,
satisfied by the static `Trading:Risk:ReferencePrices` table), or
`missing` (no source had a number). The `bypassed_no_reference`
counter only increments when a configured collar approves an order
purely because no reference was available — i.e. the fail-open
escape hatch was exercised. A sustained non-zero rate is the cue for
ops to either seed the static table or fix the live feed.

Per-check `risk_check_total` / `risk_reject_total` / pipeline
duration histograms remain on the slice 8 list (conformance + Grafana
panel).

### 4.7 Conformance

Add scenarios to `B3.Trading.Conformance` covering each new reason
code (`MARGIN_INSUFFICIENT`, tick/lot violations, absolute collar).
The synthetic-ER invariant — risk rejections flow through the same
channel as exchange rejections — is asserted by reading from the same
ER stream the existing tests use.

### 4.8 Throttle ledgers (rolling notional + order rate)

`SlidingWindowLedger` is the shared in-memory aggregate behind
`RollingNotionalCheck` and `OrderRateLimitCheck`. Two design points
worth fixing:

- **Scope is end-client and firm only — no per-symbol slot.** The
  ledgers are keyed per-end-client (and per-firm) globally, but
  `RiskLimits` resolves caps via the per-symbol slot too. Letting the
  per-symbol cap apply to a global ledger would mean the cap that
  applies to one order varies with the symbol while the state being
  measured is global, which is the inconsistency we want to avoid.
  `RollingNotionalOptions` and `OrderRateOptions` therefore live as
  their own top-level sections with `Default` / `PerEndClient` /
  `PerFirm` only.
- **Check + record is intentionally not atomic.** Risk evaluation
  reads the ledger; the accountant only writes after the synchronous
  pipeline *and* the async margin reservation approve. Wrapping a
  lock around that span would serialise order entry. Trade-off: under
  N concurrent in-flight submits the cap can be overshot by up to N.
  Acceptable for an anti-runaway guard; if a strict throttle is ever
  needed, that's a different check with reserve+release semantics
  (out of scope for v2).

`MaxOpenOrdersCheck` reads `WorkingOrderBook.CountOpenForOwner`,
which uses a secondary owner→ClOrdId index built on TryAdd/Restore so
the hot path doesn't scan all historical orders. The order being
submitted is already in the book by the time the risk pipeline runs
(persistence dispatcher adds it before evaluation), so the check
compares with strict `>` against the cap, not `>=`.

A `ThrottleLedgerSweeper` BackgroundService prunes empty buckets
periodically (default 60s) so distinct end-client/firm churn doesn't
leak unbounded memory.

## 5. Alternatives considered

### A. Persist limits in a database now

Rejected. The persistence spike (#29) is unresolved. Picking a store
here would either pre-empt that decision or force a migration when it
lands. Hot-reload from the file is enough for the volumes we expect
in the next milestone.

### B. Replace `IRiskCheck` with a pipeline-builder DSL

Rejected. The current interface is plain, ergonomic, and well-tested.
A DSL adds a layer with no clear win.

### C. Margin check inside the exchange gateway

Rejected. The gateway is a transport. Putting business policy
("client X can or cannot afford this") behind it makes risk
decisions invisible to the metrics, the conformance suite, and the
synthetic-ER channel — exactly the invariants v1 was built around.

### D. Trust client-side fat-finger only

Rejected. Any scripted client (or a misbehaving internal tool)
bypasses it. v2 specifically promotes those checks to server-side.

### E. Live reference prices that block on missing data

Rejected. Fail-closed on a missing reference would mean cold-start
can't process a single order until an MD feed is up. Operationally
brittle; the metric + ops paging is the compromise.

## 6. Migration

- All new fields on `RiskOptions` are optional with safe defaults
  (null / empty dictionaries). Existing deployments need no config
  change to keep running with v1 behavior.
- Adding `MarginCheck` to the pipeline is gated by
  `Trading:Risk:Margin:Enabled` (default `false` for the first
  release, flipped to `true` once the stub provider is documented).
- `MinTickSize` / `MinLotSize` only enforce when the value is set on
  `RiskOptions`; absent values mean "no check" (matches today's
  schema convention).
- Hot-reload is transparent: switching `IOptions` → `IOptionsMonitor`
  doesn't change the resolved limits when the file hasn't changed.

## 7. Roadmap (PRs after this RFC)

Each PR is sequenced, autocontido, build/format/test green, with
metrics + tests included.

1. RFC (this document) — no code. **shipped (#39)**
2. `PerFirm` limits + resolver update. **shipped (#40)**
3. `IOptionsMonitor` switch + `GET /admin/risk/limits` + reload
   endpoint. **shipped (#41)**
4. `MarginCheck` + reserve-on-submit ledger. **shipped (#43)**
5. `IReferencePrice` indirection + `MarketDataReferencePrice` with
   fallback + staleness/source/bypass metrics. **shipped (#44)**
6. Fat-finger server-side: `MinTickSizeCheck`, `MinLotSizeCheck`,
   absolute collar in `PriceCollarCheck`. **shipped** —
   `MaxNotionalPerOrderCheck` was deduplicated against the existing
   `MaxNotionalCheck` (slice 7's rolling-window variant remains
   distinct).
7. Notional cap by rolling window + order rate limit + max open
   orders. **shipped** — sliding-window queue ledger
   (`SlidingWindowLedger`) shared by `RollingNotionalCheck` and
   `OrderRateLimitCheck`; both have per-end-client and per-firm scopes
   (no per-symbol — see §4.4 below). `MaxOpenOrdersCheck` reads an
   indexed `WorkingOrderBook.CountOpenForOwner` so the hot path is
   O(orders for owner). `IRiskAccountant` is fanned out from the
   submit endpoint after both the synchronous pipeline and margin
   approve. A periodic `ThrottleLedgerSweeper` removes empty buckets
   to bound memory under tenant churn.
8. Conformance scenarios + Grafana panel + docs touch-up. **shipped** —
   `Spec_HTTP_Risk/RiskRejectionShapeSpecTests.cs` discovers the
   resolved `MaxQuantity` via `GET /admin/risk/limits` and asserts the
   wire contract for risk rejections (`202 Accepted` + `{ clOrdId,
   status: "Rejected", reason }`). Grafana dashboard
   `dashboards/risk.json` ("B3 Trading — Risk") surfaces rejection
   rates by reason, ref-price lookup mix and staleness, bypass
   counters, and active throttle buckets per scope. `docs/METRICS.md`
   gained the per-metric inventory for the `trading.risk.*` family.

**RFC status: Implemented.** Future evolutions (PerFirm vs PerSymbol
ordering — OQ-1, margin TTL — OQ-2, per-firm reload — OQ-4) will land
as small follow-up PRs without a new RFC unless a behavioural
contract changes.

## 8. Open questions

- **OQ-1:** Should `PerFirm` resolution defer to `PerSymbol` (as
  proposed) or override it? A symbol with a tight cap may need to win
  over a permissive firm cap. Tentative answer: keep the proposed
  order but allow `PerSymbol` to set fields the firm leaves null
  (which is what first-non-null already gives us).
- **OQ-2:** Margin TTL default — 5s feels right for the stub but a
  real provider may want 30s+ to amortize back-office cost. Leaving
  it configurable; default revisits when the real adapter arrives.
- **OQ-3:** Rate limit window — fixed-window vs sliding-log. **Resolved:**
  shipped sliding-log via `SlidingWindowLedger` (queue + running
  aggregate). Memory is bounded by the periodic sweeper; the running
  aggregate keeps `Sum`/`Count` O(entries-pruned-this-call) rather
  than O(window-size).
- **OQ-4:** Should `/admin/risk/reload` be per-firm or global?
  Starting global; per-firm only if a multi-firm config-source
  provider needs it.
