# Persistence

Phase 6 of the platform replaces the previous fully-ephemeral in-memory
state with a local **event-sourced write-ahead log (WAL)** plus
periodic snapshots. The model is deliberately purist: events are the
only source of truth on disk, snapshots are a derived cache, and
recovery is a linear replay.

This document describes the on-disk layout, durability guarantees, and
the rules that make the design safe.

## Source-of-truth invariant

> The inbound `ExecutionReport` stream from the B3 EntryPoint
> (`IEntryPointClient`) is the canonical source of truth for state.
> The B3 replays missed ERs on FIXP recovery.

This applies to venue-derived order/execution state. The WAL now also contains
local-only controls, credentials, cash, sub-accounts and algo lifecycle state
that B3 cannot replay, so it is no longer universally only an audit log + boot
accelerator. The proposed class-aware durability contract is
[`durability-classes-fail-closed-v0`](rfcs/durability-classes-fail-closed-v0.md);
this document continues to describe the current runtime until its
implementation slices land.

Every new event must therefore identify whether the venue can replay it or the
platform is its only authority; the linked RFC defines that decision.

## Layout

Per firm, under `Trading:Persistence:DataDirectory`:

```
data/{firm}/wal/2026-05-01/
  000.log    # data segment, append-only
  000.idx    # sparse index, fixed 24-byte records
  001.log
  001.idx
data/{firm}/snapshots/
  snap-000042.json
  latest.txt          # {seq, file, ts} pointer for O(1) boot
data/{firm}/eod/
  eod-2026-05-01.json # daily reconciliation summary
```

Segments rotate on (a) day boundary by event `TimestampUtc`, or
(b) `Trading:Persistence:SegmentMaxBytes` (default 64 MiB).

### Record framing (`.log`)

```
[u32 length][u32 crc32][JSON payload of length bytes]
```

- `length` is the byte length of the payload only.
- `crc32` is `System.IO.Hashing.Crc32` over the payload only.
- Payload is `System.Text.Json` of `WalEvent` (polymorphic discriminator
  field `kind`).

### Sparse index (`.idx`)

Fixed 24-byte records, written every 64 events or every 4 KiB of log:

```
[u64 seq][u64 offsetInLog][u64 timestampMs]
```

Rebuildable from the `.log` if missing or corrupt. Used to skip ahead
during `ReadFromAsync(sinceSeqExclusive)`.

## Event stream

`WalEvent` types are declared in
`B3.Trading.Application.Persistence.WalEvents`. Major families include:

- order submit/cancel/replace intent and terminal transitions;
- inbound real and synthetic execution reports;
- kill-switch, halt, session-phase and staleness controls;
- algo lifecycle and scheduling progress;
- bot credentials, session versions and sequence checkpoints;
- cash, fees, realised P&L, sub-accounts and audit events.

Cancel commands are persisted as `OrderCancelRequestedEvent` so ownership
links, the ClOrdID watermark and bot mappings survive restart; the eventual
cancel ER remains the authoritative terminal order transition.

WebSocket fan-out frames are **not** persisted — they are projections
recomputable from the WAL.

### Schema evolution

- Never rename a field.
- New fields must be optional (default value).
- To remove, mark `[Obsolete]` and leave on the record until every
  retained segment has rotated out.

## Durability policy

`FileEventStore` runs an **async write-behind** loop:

- A bounded `Channel<LogEntry>` (capacity =
  `Trading:Persistence:ChannelCapacity`, default 4096) buffers appends.
- Group commit drains up to `GroupCommitMaxRecords` (default 512) or
  waits up to `GroupCommitWindow` (default 10 ms), whichever first,
  then `FileStream.Flush(flushToDisk: true)` on the active segment.
- Backpressure: if the channel is full, `Append` throws
  `WalBackpressureException`. The endpoint layer handles this:

| Call site | Backpressure response |
| --- | --- |
| `POST /orders` (`OrderSubmittedEvent`) | Returns **503**; order never enters book. |
| Synthetic rejection helper | Mutates state + publishes to sink (audit lost — ghost orders are worse). |
| `EntryPointExecutionReportRouter` | Calls `processor.Apply(...)` anyway (state preserved, audit lost). |

Crash exposure includes the bounded channel plus the in-progress group-commit
batch (at current defaults, at most 4096 + 512 admitted records). The 10 ms
window bounds batching delay once the writer is draining normally; the next
FIXP session can replay only the venue-recoverable subset.

## Consistency model

All append + apply critical sections go through a single
`EventDispatcher` (in `B3.Trading.Application.Persistence`):

```csharp
dispatcher.Dispatch(evt, () => { /* apply to in-memory state */ });
```

A single `lock` guarantees:

1. The append (and seq increment) and the in-memory mutation happen
   atomically with respect to other dispatches.
2. `WithSnapshotLock(Action<long> capture)` brackets snapshot captures
   in the same lock — so `snapshot.seq` is always a position past which
   replay is correct (no double-apply, no skipped event).

Sequence numbers are derived from log position, not stored in the
payload. They are monotonic and start from 1 on a fresh store.

## Snapshots

`SnapshotService` is a `BackgroundService` that fires every
`Trading:Persistence:SnapshotInterval` (default 5 min) and once more on
graceful shutdown. It captures:

- `WorkingOrderBook` (open orders only)
- `PositionKeeper` (non-flat positions only)
- `KillSwitchService` (killed end-clients + firms)
- `OrderOwnershipMap` (`ClOrdID → endClientId`)
- `ClOrdIdPrefixRegistry` (`_nextPrefix` watermark + per-end-client
  counter watermarks)
- `CashLedger` balances keyed by `(firmId, endClientId)`. Legacy snapshots
  without `FirmId` restore into the `DEFAULT` bucket only; recovery never
  duplicates one historical balance across every configured firm.
- operator deposit/withdrawal `CashKeeper` balances use the same firm scope.
  Their snapshot dictionary keys are `{firmId}|{endClientId}`; legacy plain
  keys also restore only into `DEFAULT`.

Write is atomic via temp file + `File.Move(overwrite: true)`. The
`latest.txt` pointer is then updated; if it is missing or corrupt at
boot, `SnapshotStore.LoadLatest()` falls back to the highest-numbered
`snap-*.json` it can parse.

Snapshots are a **derived cache**. Deleting them is harmless — the WAL
is sufficient (boot will be slower).

## Recovery

Synchronous, runs once between `app.Build()` and `app.Run()` in
`Program.cs`:

1. `SnapshotStore.LoadLatest()` → seed in-memory state via
   `StateSnapshotter.Restore(snap)`.
2. `FileEventStore.ReadFromAsync(snap?.Seq ?? 0)` walks every segment
   starting at the snapshot's position; for each `(seq, evt)`,
   `EventReplayer.Apply(evt)` reapplies it.
3. CRC failures truncate the active segment at the last valid record
   (`SegmentReader.LastValidEnd`). Earlier segments are read-only.

After snapshot + WAL replay, margin reservations are rebuilt from every
non-terminal, non-stale Buy order using its executable leaves. Because the
order-rate and rolling-notional ledgers are intentionally in-memory, recovery
activates a conservative fence for each configured window: throttled submits
and modifies are rejected until that window has elapsed from restart. This
prevents restart from restoring risk capacity without adding throttle entries
to the persistence schema.

`EventReplayer` does **not** publish to `IExecutionEventSink` during
replay — there are no live subscribers and replaying historical ERs
would just be noise.

## EOD reconciliation

`POST /admin/eod` (admin-only) walks the day's WAL directory and writes
`data/{firm}/eod/eod-{date}.json`:

- Counts per `WalEvent` kind.
- SHA-256 over concatenated payloads (content checksum for diffing).
- File path of the materialised summary.

Returns **409** if persistence is disabled. Comparison against an
EP-side EOD report is a future hook; B3 does not expose one yet.

## Configuration

```jsonc
"Trading": {
  "Persistence": {
    "Enabled": true,
    "DataDirectory": "data",
    "FirmId": "default",
    "SnapshotInterval": "00:05:00",
    "SegmentMaxBytes": 67108864,
    "ChannelCapacity": 4096,
    "GroupCommitWindow": "00:00:00.0100000",
    "GroupCommitMaxRecords": 512
  }
}
```

Setting `Enabled = false` wires `NullEventStore` (the dispatcher lock
still runs so concurrency semantics are unchanged) and disables the
snapshot service. The 27 API integration tests use this so they remain
file-system-free.

## Out of scope (v1)

- Replication / multi-node consensus.
- Encryption at rest.
- Log compaction beyond daily segmentation.
- Retention / archival policy (operational concern for Phase 7).
- Bidirectional EOD reconciliation against B3's report.
