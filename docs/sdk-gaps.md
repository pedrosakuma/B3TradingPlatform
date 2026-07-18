# SDK gaps — `B3.EntryPoint.Client`

Tracks fields/contracts that the **B3.EntryPoint.Client** package does not yet expose on its public surface, plus recently closed gaps that are already mapped by this platform.

Verified against: `B3.EntryPoint.Client 0.17.0` (18/07/2026).

## Durable outbound-attempt evidence

RFC
[`durable-outbound-mutations-v0`](rfcs/durable-outbound-mutations-v0.md)
requires two durable boundaries: a platform-owned attempt intent before gateway
entry, then an SDK callback after sequence reservation/encoding but before the
first possible transport write. SDK 0.17.0 closes the upstream gap through
`SubmitWithReceiptAsync`, `ReplaceWithReceiptAsync` and
`CancelWithReceiptAsync`.

The callback receives immutable `SessionId`, `SessionVerId`, outbound
`MsgSeqNum`, operation, ClOrdID, frame length and SHA-256 identity while SDK
reserve → encode → callback → write remains serialized. Callback failure
prevents transport write. Successful completion proves only local
`TransportWriteCompleted` (or later SDK state persistence), never venue
acceptance. Typed failures expose the last stage and the SDK's explicit
`NoTransportWritePossible` proof.

The platform maps this API behind platform-owned receipt types in
`IExchangeGateway`. Existing order services intentionally remain on the legacy
compatibility methods until #642/#643 commit `AttemptIntentPrepared` and
`FramePrepared` through the coordinator.

There is still no exact-original-frame/original-sequence replay operation.
Failures after frame preparation require reconciliation rather than resend. A
dead epoch with intent but no committed `FramePrepared` becomes proven-unsent
only after SDK sequence-state reconciliation under the callback no-write
contract.

Tracked upstream:
[B3EntryPointClient#223](https://github.com/pedrosakuma/B3EntryPointClient/issues/223).
The remaining coordinator and service rewiring is tracked by #642/#643.

This is not an order-status-query request. B3 EntryPoint 8.4.2 has no
MassStatus/OrderStatus template; upstream
[#193](https://github.com/pedrosakuma/B3EntryPointClient/issues/193) closed that
idea as not applicable to the wire.

## Outbound request shape — missing properties

As of `0.17.0`, the remaining SDK-surface gaps on `NewOrderRequest` / `ReplaceOrderRequest` are:

| Field | FIX tag | Compliance use | Platform compensation today | Tracking |
| --- | --- | --- | --- | --- |
| `ExecInst` | 18 | Flags (STP mode, do-not-aggregate, work-up, AON, …) | STP runs 100% host-side (sees every firm, doesn't need venue cooperation). AON / work-up have no host-side compensation | [#441](https://github.com/pedrosakuma/B3TradingPlatform/issues/441) |
| `DisplayResetPolicy` / `RefreshPolicy` | n/a (B3 iceberg) | Iceberg refresh-on-execution policy (`Always` / `OnPartialFill` / `Never`) | REST + risk gate-bloqueia a escolha do trader a `Always` (#297) para que a omissão no wire seja faithful; loses the other two policies | [#298](https://github.com/pedrosakuma/B3TradingPlatform/issues/298), [#436](https://github.com/pedrosakuma/B3TradingPlatform/issues/436) (closed → covered here) |

Reflection against `B3.EntryPoint.Client 0.17.0` confirms both request types expose `TradingSubAccount` but still do **not** expose any `ExecInst` / `ExecutionInstruction` / `ExecutionInstructions` or `DisplayResetPolicy` / `RefreshPolicy` / `DisplayRefreshPolicy` property.

## Resolved since the original compliance audit

| Field | SDK surface now | Platform status | Tracking |
| --- | --- | --- | --- |
| `SubAccount` | `TradingSubAccount` on `NewOrderRequest` and `ReplaceOrderRequest` | Resolved: `Domain.Order.SubAccountId` is mapped to the venue-visible SDK field in `B3EntryPointClientGateway`, with default DI wiring via `ISubAccountWireIdMapper` | [#441](https://github.com/pedrosakuma/B3TradingPlatform/issues/441), [#458](https://github.com/pedrosakuma/B3TradingPlatform/issues/458), [#471](https://github.com/pedrosakuma/B3TradingPlatform/issues/471) |

## Tripwire test

[`backend/tests/B3.Trading.Application.Tests/B3EntryPointSdkTripwireTests.cs`](../backend/tests/B3.Trading.Application.Tests/B3EntryPointSdkTripwireTests.cs) asserts via reflection that each still-missing property is **absent** from `NewOrderRequest` and `ReplaceOrderRequest`. When the SDK adds the property (and we bump the package version) the test goes red — that is the signal to:

1. **Wire the field end-to-end** in `Domain.Order` → WAL `OrderSubmittedEvent` → `OrderSubmissionRequest` → `BuildNewOrderRequest` / `BuildReplaceOrderRequest`.
2. **Add wire-pinning coverage** in `B3EntryPointClientGatewayMapTests` / `B3EntryPointClientGatewayTranslationTests`.
3. **Remove the matching REST/risk guard** when applicable (e.g. for `DisplayResetPolicy` drop the `Always`-only restriction in `OrdersEndpoints` + `OrderSubmissionService`, closing #298).
4. **Delete the tripwire test** for that specific field.

The lookup is case-insensitive and covers known FIX-style aliases (`ExecInst`/`ExecutionInstruction`, `DisplayResetPolicy`/`RefreshPolicy`); if the SDK ships with an entirely different name the tripwire stays green and the next auditoria catches the gap — risk accepted in favour of not false-positiving on capitalisation changes.

## Outbound request shape — present but not plumbed

These fields **are** exposed by 0.17.0 but the platform does not populate them today. Tracked separately (no tripwire needed — straight implementation):

| Field | Tracking |
| --- | --- |
| `NewOrderRequest.MinQty` / `ReplaceOrderRequest.MinQty` | [#457](https://github.com/pedrosakuma/B3TradingPlatform/issues/457) |
| `NewOrderRequest.Account` (CBLC numeric) | [#458](https://github.com/pedrosakuma/B3TradingPlatform/issues/458) (blocked on modelling decision) |

## How to refresh this doc when the SDK bumps

```bash
# 1. Bump the package
$EDITOR backend/Directory.Packages.props          # update B3.EntryPoint.Client version

# 2. Re-run the tripwire
dotnet test backend/tests/B3.Trading.Application.Tests/B3.Trading.Application.Tests.csproj \
  --filter "FullyQualifiedName~B3EntryPointSdkTripwire"

# 3. For each tripwire that goes red: plumb the field, update this table, delete the test.
```
