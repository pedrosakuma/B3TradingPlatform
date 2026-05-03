# RFC: Pre-trade risk v2

| Field    | Value                                          |
| -------- | ---------------------------------------------- |
| Status   | Draft                                          |
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

New metrics (Prometheus, via the existing OTel pipeline):

| Metric                                | Type      | Labels                  |
| ------------------------------------- | --------- | ----------------------- |
| `risk_check_total`                    | counter   | check, outcome          |
| `risk_reject_total`                   | counter   | reason, firmId          |
| `risk_pipeline_duration_ms`           | histogram | (none)                  |
| `risk_margin_check_duration_ms`       | histogram | (none)                  |
| `risk_reference_price_stale_total`    | counter   | symbol                  |
| `risk_reference_price_missing_total`  | counter   | symbol                  |

Grafana panel mirrors the FIXP panel's layout (rate / p95 / errors).

### 4.7 Conformance

Add scenarios to `B3.Trading.Conformance` covering each new reason
code (`MARGIN_INSUFFICIENT`, tick/lot violations, absolute collar).
The synthetic-ER invariant — risk rejections flow through the same
channel as exchange rejections — is asserted by reading from the same
ER stream the existing tests use.

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

1. RFC (this document) — no code.
2. `PerFirm` limits + resolver update.
3. `IOptionsMonitor` switch + `GET /admin/risk/limits` + reload
   endpoint.
4. `MarginCheck` + `StaticMarginProvider` + cache + reject reason
   wiring.
5. `IReferencePrice` indirection + `MarketDataReferencePrice` with
   fallback + stale/missing metrics.
6. Fat-finger server-side: `MinTickSizeCheck`, `MinLotSizeCheck`,
   `MaxNotionalPerOrderCheck`, absolute collar in
   `PriceCollarCheck`.
7. Notional cap by rolling window + order rate limit + max open
   orders.
8. Conformance scenarios + Grafana panel + docs touch-up.

## 8. Open questions

- **OQ-1:** Should `PerFirm` resolution defer to `PerSymbol` (as
  proposed) or override it? A symbol with a tight cap may need to win
  over a permissive firm cap. Tentative answer: keep the proposed
  order but allow `PerSymbol` to set fields the firm leaves null
  (which is what first-non-null already gives us).
- **OQ-2:** Margin TTL default — 5s feels right for the stub but a
  real provider may want 30s+ to amortize back-office cost. Leaving
  it configurable; default revisits when the real adapter arrives.
- **OQ-3:** Rate limit window — fixed-window vs sliding-log. Sliding
  is more accurate but more memory; fixed is simpler. Lean fixed for
  v2 unless the conformance scenarios show false negatives.
- **OQ-4:** Should `/admin/risk/reload` be per-firm or global?
  Starting global; per-firm only if a multi-firm config-source
  provider needs it.
