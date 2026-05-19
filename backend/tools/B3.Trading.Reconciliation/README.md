# B3 Trading-Host Reconciliation Tool

D+1 reconciliation between the matching platform's EOD fills CSV
([B3MatchingPlatform#330](https://github.com/pedrosakuma/B3MatchingPlatform/issues/330))
and the trading-host's daily statement
(`GET /statement/{date}.csv`). Closes the trading-host side of
[B3TradingPlatform#274](https://github.com/pedrosakuma/B3TradingPlatform/issues/274).

## Inputs

* **Matching CSV drop** — produced by `EodFillsExporter` at
  `{dropRoot}/{channel}/{yyyy-MM-dd}/fills.csv` (consumer-visible
  when the `.done` sidecar appears). Columns (frozen by ADR-0001):

  ```
  tradeId,ts,symbol,aggressorSide,qty,price,buyClOrdId,sellClOrdId,buyFirm,sellFirm
  ```

  The tool verifies the SHA-256 declared in `.done` matches the
  file bytes and rejects partial / corrupt drops.

* **Trading-host statement** — fetched via
  `GET {tradingHostUrl}/statement/{date}.csv` with a Bearer token.
  Multi-section CSV; only the *fills* section is consumed.

## What it checks

Compares **(symbol, firm-relative side)** aggregates between the two
sources:

* count of fills
* sum of fill quantity
* sum of fill notional (qty × price)

The matching exporter emits one row per trade with both firms on the
same row; the tool projects the firm-relative view by:

* expanding internal crosses (`buyFirm == sellFirm == ourFirm`) into a
  Buy + Sell pair, mirroring the host's per-ER `FillRowDto` shape;
* skipping rows where neither side matches the requested firm.

A per-trade join on `tradeId` is intentionally **not** done — the
trading-host's `executionId` is synthesised as `{clOrdId}:{cumQty}`
and is not the venue tradeId.

## Exit codes

| Code | Meaning |
|------|---------|
| 0    | All buckets aligned. |
| 2    | At least one bucket differs — see stdout for the diff report. |
| 1    | Argument / IO / integrity error. |

## Usage

```bash
b3-reconcile \
  --matching-fills-dir /var/post-trade/drops \
  --channel 1 \
  --date 2026-05-19 \
  --firm FIRM01 \
  --trading-host https://trading.local \
  --auth-token "$TRADING_HOST_TOKEN"
```

## Wiring this into D+1 operations (suggested)

1. Wait for the matching `.done` sidecar to appear (filesystem watch
   or polling).
2. Wait for the trading-host's daily reset to flush in-flight WAL events
   (host emits a metric on the snapshot boundary).
3. Run `b3-reconcile` per firm. Page on exit code 2.

## Out of scope (filed as follow-ups)

* End-to-end CI scenario (real matching → real trading-host → diff = 0)
  — depends on B3MatchingPlatform#330 PR-3 (DailyResetScheduler hook)
  and a docker-compose harness; filed as a follow-up to #274.
* Per-trade audit (matching every host fill ER to a venue trade row)
  — requires the trading-host to start carrying the venue tradeId on
  the ER, which today is dropped by the SDK.
