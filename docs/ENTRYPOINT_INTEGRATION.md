# EntryPoint integration

How the trading platform talks to **B3 EntryPoint** (the FIXP/SBE order
entry gateway) via the [`B3.EntryPoint.Client`](https://www.nuget.org/packages/B3.EntryPoint.Client)
NuGet package.

> Status: Phase 1B — first end-to-end wiring of the real
> `EntryPointClient`. Tests still exercise the in-memory
> `MockEntryPointClient`; the real client is opt-in via config.

## Package

| Package                   | Version | Source       |
| ------------------------- | ------- | ------------ |
| `B3.EntryPoint.Client`    | 0.5.0   | nuget.org    |
| `B3.EntryPoint.Sbe`       | 0.5.0   | transitive   |

Both are MIT-licensed. Upgrade by bumping the `<PackageReference>` in
[`backend/src/B3.Trading.Infrastructure/B3.Trading.Infrastructure.csproj`](../backend/src/B3.Trading.Infrastructure/B3.Trading.Infrastructure.csproj).

## Topology

```
                                   ┌────────── upstream EntryPointClient (firm A) ──┐
                                   │                                                 │
HTTP /orders ─→ OrdersEndpoints ─→ MultiFirmExchangeGateway ─→ B3EntryPointClientGateway ─→ B3 gateway
                                   │   (routes by Order.FirmId)                      │
                                   └────────── upstream EntryPointClient (firm B) ──┘
                                                       │
                                                       │  IAsyncEnumerable<EntryPointEvent>
                                                       ▼
                            FirmGatewayRegistry (aggregates ER stream from all firms)
                                                       │
                                                       │  Action<ExecutionReportEnvelope>
                                                       ▼
                                       EntryPointExecutionReportRouter
                                                       │
                                                       ▼
                                            ExecutionReportProcessor → WAL + state + WS sink
```

Three modes are selectable via config:

| `Trading:Exchange` keys                                   | Wiring                                                      | Use case                |
| --------------------------------------------------------- | ----------------------------------------------------------- | ----------------------- |
| `UseStubGateway = true`                                   | `StubExchangeGateway` only (no client at all).              | API smoke tests, CI.    |
| `UseStubGateway = false`, `UseRealEntryPointClient=false` | `MockEntryPointClient` + `EntryPointClientGateway` (in-process, no TCP). | Default dev loop. |
| `UseStubGateway = false`, `UseRealEntryPointClient=true`  | One upstream `EntryPointClient` per `FirmConfig`, dispatched by `MultiFirmExchangeGateway`. | UAT / production.       |

## ClOrdID — packed `ulong`

Upstream `ClOrdID` is `readonly record struct(ulong)` and rejects `0`.
Our `ClOrdIdPrefixRegistry.Generate(EndClientId)` emits packed values:

```
ClOrdID = (prefixIdx << 40) | counter
```

| Field        | Width   | Range                    |
| ------------ | ------- | ------------------------ |
| `prefixIdx`  | 21 bits | up to ~2 M end-clients   |
| `counter`    | 40 bits | up to ~1.1 T orders/EC   |

The MSB stays 0; first generated value is `1`, never zero. Packed values
exceed JS `Number.MAX_SAFE_INTEGER`, so HTTP/WebSocket DTOs serialise the
ClOrdID as a **decimal string** (`ClOrdId.ToString()`). Internally
(domain, WAL, snapshots) the field is `ulong`.

## SecurityID

The upstream wire identifies an instrument by `uint64 SecurityId` —
`Symbol` is a UI-only concept on our side. `POST /orders` accepts
`securityId` directly; the frontend resolves Symbol→SecurityId from a
config-fed lookup. Our `Order` carries both, but only `SecurityId` is
forwarded.

## ExecutionReport translation

`B3EntryPointClientGateway.Translate` maps each upstream
`EntryPointEvent` subtype to our `ExecutionReportEnvelope`:

| Upstream event       | `ExecType`          | Notes                                                                                    |
| -------------------- | ------------------- | ---------------------------------------------------------------------------------------- |
| `OrderAccepted`      | `New`               | seeds `LeavesQty`/`CumQty`.                                                              |
| `OrderTrade`         | `Fill`/`PartialFill`| Discriminated by `OrderStatus.Filled` vs `PartiallyFilled`.                              |
| `OrderCancelled`     | `Canceled`          | `OrigClOrdID` carried — processor mutates the **original** order, not the cancel-side ID.|
| `OrderModified`      | `Replaced`          | `OrigClOrdID` required.                                                                  |
| `OrderRejected`      | `Rejected`          | `Reason` if present, else `reject_code=N`.                                               |
| `BusinessReject`     | n/a (null envelope) | No ClOrdID — surfaced via `trading.entrypoint.business_rejects` metric only.             |

`OrigClOrdId` lives on the envelope and the WAL `ExecutionReportReceivedEvent`
so recovery is loss-less. `ExecutionReportProcessor.Apply` looks up the
order by `OrigClOrdId` for cancel/replace acks.

## Multi-firm

One upstream `EntryPointClient` per `FirmConfig` — per the README,
the platform fronts N FIXP sessions. `Order.FirmId` is set from the JWT
`firm` claim at submit time and is the routing key in
`MultiFirmExchangeGateway`. `FirmGatewayRegistry` doubles as the
aggregate `IEntryPointClient` consumed by
`EntryPointExecutionReportRouter`, fanning every firm's ER stream into a
single subscriber.

## Configuration

```jsonc
{
  "Trading": {
    "Exchange": {
      "UseStubGateway": false,
      "UseRealEntryPointClient": true,
      "Firms": [
        {
          "FirmId": "FIRM-A",
          "Endpoint": "ep-uat.b3.com.br:9100",
          "SessionId": 12345,
          "SessionVerId": 1,
          "EnteringFirm": 999,
          "AccessKey": "redacted-utf8-token",
          "SenderLocation": "SP01",   // ≤ 10 chars
          "EnteringTrader": "TR01",   // ≤ 5 chars
          "KeepAliveIntervalMs": 1000,
          "InitialReconnectDelay": "00:00:01",
          "MaxReconnectDelay": "00:00:30",
          "DnsResolutionTimeout": "00:00:05",
          "GracefulTerminateTimeout": "00:00:02"
        }
      ]
    }
  }
}
```

`AccessKey` is wrapped via `Credentials.FromUtf8`. `Endpoint` shape and all
timeouts are validated at host startup. DNS resolution is asynchronous and
runs inside each serialized cold-connect/reconnect attempt, so a resolver
failure follows the normal retry/backoff path instead of blocking service
construction.

## Observability

| Metric                                         | Type                 | Tags                            |
| ---------------------------------------------- | -------------------- | ------------------------------- |
| `trading.entrypoint.connected`                 | UpDownCounter        | `firm`                          |
| `trading.entrypoint.events_received`           | Counter              | `firm`, `event_type`            |
| `trading.entrypoint.translation_errors`        | Counter              | `firm`                          |
| `trading.entrypoint.business_rejects`          | Counter              | `firm`, `reason`                |
| `trading.entrypoint.terminated`                | Counter              | `firm`, `code`, `initiated_by_client` |
| `trading.entrypoint.reconnect_attempts`        | Counter              | (reserved for follow-up)        |

See [OBSERVABILITY.md](OBSERVABILITY.md) for the meter shape.

`/ready` blocks while any required firm session is missing, disconnected, or
not established; `/live` remains process-only.

## Known follow-ups (issue #7)

These are deliberately **not** in the v1 wiring:

- **Reconnect state machine.** Peer termination or an unexpected event-stream
  stop marks the firm disconnected immediately and schedules the single-flight
  reconnect loop. Cold connect and reconnect share one serialization fence.
  WAL backpressure/fault is different: the gateway marks health disconnected,
  terminates the session best-effort, and rejects outbound mutations without
  auto-reconnecting until persistence recovery is controlled externally.
- **`BusinessReject` correlation.** Rejects are translated to a firm-scoped
  envelope, deduplicated by inbound sequence, counted, and persisted to the WAL
  for operator reconciliation. The upstream package still does not expose a
  `RefSeqNum → ClOrdID` map, so the platform does not synthesize an order-level
  rejection.
- **In-process FIXP test peer.** Once upstream ships an in-memory peer,
  integration tests can exercise the real adapter end-to-end without
  TCP.
