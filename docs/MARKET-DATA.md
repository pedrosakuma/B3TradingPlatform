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

The book flag is opt-in; the trading-host subscribes to it only
when `Trading:MarketData:EnableBook` is `true`. With the flag off
the host runs in legacy mode: no MBO events, Pegged algos fall
back to last-trade for `PegRef.Mid`/`Best`.

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
      "EnableBook": true
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
