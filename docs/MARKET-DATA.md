# Market data

This document describes how the trading-host consumes the
`B3.MarketData.WebSocketClient` SDK and how MBO (Market-by-Order /
L3) events flow through the application after PR #286 (Stages A–C).

## SDK version

`B3.MarketData.WebSocketClient` is pinned in
`backend/Directory.Packages.props`. The current version exposes:

- `SubscribeFlags.Info` — instrument metadata / snapshots
- `SubscribeFlags.Trades` — public trade prints
- `SubscribeFlags.Book` — MBO order-by-order events
  (`OrderAdded` / `OrderUpdated` / `OrderDeleted` /
  `BookSnapshot` / `BookCleared`)
- `SubscribeFlags.SecurityDefinition` — bootstrap + delta of
  `SecurityDefinition_12` (tick, lot, contract multiplier, option
  metadata). Projected by `SdkMarketDataSubscriber` into
  `SecurityDefinitionRegistry`, which `SymbolDirectory.TryGetSpec`
  consults before falling back to the operator-configured static
  dictionary. Added in SDK 0.5.0 (#486 / upstream
  `pedrosakuma/B3MarketDataPlatform#55`).
- `SubscribeFlags.PriceBand` — bootstrap + delta of `PriceBand_22`
  (the venue's authoritative dynamic price band per symbol).
  Projected by `SdkMarketDataSubscriber` into `PriceBandRegistry`,
  which the new pre-trade `PriceBandCheck` (Order=305) consults as
  the source of truth for fat-finger rejection — replacing the
  static-config `PriceCollarCheck` (Order=300) on the symbols
  where the venue actually publishes. Added in SDK 0.6.0 (#487 /
  upstream `pedrosakuma/B3MarketDataPlatform#56`). Today only the
  `PriceLimitType=PRICE_UNIT` (absolute) variant is projected;
  `TICKS` / `PERCENTAGE` frames are dropped (see
  `PriceBandRegistry.TryProject` docs — fail-open into the bypass
  counter).

The book flag is opt-in; the trading-host subscribes to it only
when `Trading:MarketData:EnableBook` is `true`. With the flag off
the host runs in legacy mode: no MBO events, Pegged algos fall
back to last-trade for `PegRef.Mid`/`Best`.

The `SecurityDefinition` flag is **opt-out**: enabled by default
once the SDK is bumped past 0.5.0 because the OPT umbrella ships
hundreds of option series per underlying and config-only entry is
infeasible. Set `Trading:MarketData:EnableSecurityDefinition=false`
as an emergency kill-switch — `SymbolDirectory` will then keep
returning the static config values exclusively (legacy v1
behaviour).

The `PriceBand` flag is **opt-out** for the same reason: once the
SDK is past 0.6.0, the venue band is always more authoritative than
the static collar. Set `Trading:MarketData:EnablePriceBand=false`
to fall back to `PriceCollarCheck` exclusively (the band check then
fails open into the `trading.risk.price_band.bypassed_no_band`
counter so ops still see the coverage gap).

## Configuration

```jsonc
{
  "Trading": {
    "MarketData": {
      // Live SDK endpoint. Set to empty to fall back to
      // NullMarketDataSubscriber (offline mode for dev/tests).
      "Endpoint": "wss://marketdata.example/ws",

      // Symbols to subscribe at startup. Live additions go through
      // IMarketDataSubscriber.SubscribeAsync at runtime.
      "Symbols": ["PETR4", "VALE3"],

      // When true, includes SubscribeFlags.Book in the SDK
      // subscribe call so MBO events flow into MboBookStore.
      // Default: false.
      "EnableBook": true,

      // OPT-D (#486). When true, includes
      // SubscribeFlags.SecurityDefinition so the SDK pushes tick /
      // lot / multiplier / option metadata per symbol; the host
      // projects each frame into SecurityDefinitionRegistry which
      // SymbolDirectory.TryGetSpec consults before falling back to
      // static config. Default: true (kill-switch only).
      "EnableSecurityDefinition": true,

      // OPT-E (#487). When true, includes SubscribeFlags.PriceBand
      // so the SDK pushes the venue's authoritative dynamic price
      // band per symbol; the host projects each frame into
      // PriceBandRegistry which the new pre-trade PriceBandCheck
      // (Order=305) consults before approving a LIMIT order.
      // Today only PRICE_UNIT (absolute) bands are projected; TICKS
      // and PERCENTAGE variants are dropped (fail-open into the
      // trading.risk.price_band.bypassed_no_band counter so ops
      // still see the coverage gap). Default: true (kill-switch
      // only — turning it off leaves PriceCollarCheck as the only
      // line of defence).
      "EnablePriceBand": true
    }
  }
}
```

## Event flow

```
B3.MarketData.WebSocketClient (SDK)
        │
        ▼
SdkMarketDataSubscriber  (Host)             ← adapter: SDK types → app DTOs
        │ raises events on the IMarketDataSubscriber interface
        ▼
IMarketDataSubscriber  (Application)
   ├── Trade ─────────► MarketDataVolumePump ──► VolumeCurveEstimator
   ├── Trade ─────────► MarketDataPegBookPump ─► PegBookTopCache (Last leg)
   ├── InfoSnapshot ──► MarketDataReferencePrice, AuctionStateStore
   └── Book* (gated on EnableBook)
        │
        ▼
   MboBookStorePump ──► MboBookStore  (L3, in-process)
        │                  │
        │                  └─ BookChanged(symbol) event
        ▼                         │
   WS book sink                   ▼
                          MboPegBookPump ──► PegBookTopCache (Bid/Ask legs)
```

`MboBookStore` keeps a per-symbol order-by-order side map; reads
expose `GetTopOfBook(symbol)` and `GetView(symbol)` (L2-aggregated
view derived from the L3 store). All apply methods are serialised
per symbol; `BookChanged` is raised outside the lock so subscribers
cannot deadlock the apply path.

## Subscriber surface

`IMarketDataSubscriber` is the seam between Host (which owns the
SDK lifetime) and Application (pumps + stores + algos):

- `Trade`, `InfoSnapshot`, `Imbalance`, `AuctionUpdate` — pre-MBO
  events available regardless of `EnableBook`.
- `OrderAdded`, `OrderUpdated`, `OrderDeleted`, `BookSnapshot`,
  `BookCleared` — raised only when `EnableBook` is `true`.

The application **never** references SDK types directly; only
`SdkMarketDataSubscriber` (in `B3.Trading.Host`) does the SDK→DTO
mapping. Swapping the SDK or running offline against
`NullMarketDataSubscriber` is a Host concern.

## Smoke / round-trip

- `MboBookStorePumpTests` / `MboBookStoreTests` cover snapshot,
  add/update/delete, cleared, top-of-book, side-aggregation.
- `MboPegBookPumpTests` covers the BBO bridge into
  `PegBookTopCache`.
- The integration-real-stack workflow exercises a live subscribe
  end-to-end when the upstream marketdata-platform image is
  present.

## See also

- [`docs/rfcs/perf-hardening-v0.md`](rfcs/perf-hardening-v0.md) —
  load-test methodology (the same MBO path is exercised under load).
- [`docs/ARCHITECTURE.md`](ARCHITECTURE.md) §5 — high-level view.
- Issue [#286](https://github.com/pedrosakuma/B3TradingPlatform/issues/286) —
  MBO feed tracking (this doc is the closing artefact for criterion 4).
- Issue [#293](https://github.com/pedrosakuma/B3TradingPlatform/issues/293) —
  follow-up: surface MBO over the trader-UI WS protocol.
