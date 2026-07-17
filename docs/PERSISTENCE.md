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
accelerator. The class-aware durability contract is
[`durability-classes-fail-closed-v0`](rfcs/durability-classes-fail-closed-v0.md);
the committed-prefix substrate described below is implemented, while later
Class L/V/O business-flow slices remain staged separately.

Every new event must therefore identify whether the venue can replay it or the
platform is its only authority; the linked RFC defines that decision.

## Layout

Per firm, under `Trading:Persistence:DataDirectory`:

```
data/{firm}/wal/2026-05-01/
  000.log    # data segment, append-only
  000.idx    # sparse index, fixed 24-byte records
  000.log.firstseq # generation-bound segment sequence metadata
  001.log
  001.idx
data/{firm}/wal/commit.marker # checksum-protected committed prefix + generation
data/{firm}/snapshots/
  snap-000042.json
  latest.txt          # plain decimal snapshot seq hint
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

### Committed prefix

Admission, frame append, log fsync and commit are separate boundaries.
`FlushThroughAsync(N)` completes only after the checksum-protected marker
publishes a contiguous segment manifest through sequence `N`, using staged
file fsync, atomic replacement and WAL-directory fsync. Recovery validates only
that marker generation and manifest. Complete frames beyond it are uncommitted
survivors and are truncated; missing or corrupt data at/below it fails startup
closed.

Whole survivor segments are removed metadata-first: index, first-sequence,
temporary and migration companions are deleted and the day directory fsynced
before the `.log` is deleted. The child is fsynced again, then an empty day
directory may be removed and the WAL root fsynced. Recognized companions left
orphaned by an older/interrupted cleanup are removed through the same durable
sequence before ordinals may be reused; unknown artifacts fail startup closed.

A non-empty legacy WAL without `commit.marker` is never promoted from its
highest CRC-valid frame automatically. The default
`LegacyWalStartupMode=RejectUnknownShutdown` requires reconciliation. Set
`ControlledCleanShutdown` only for the one-time upgrade after draining ingress,
successfully flushing the old process and stopping it without further
admission; the new process fsyncs that quiesced prefix and publishes its first
generation marker. Generation-bound segment metadata is staged and directory-
fsynced before marker publication, then promoted afterward; a crash at any
boundary resumes from legacy metadata before the marker or from validated
staging metadata after it.

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
links, the ClOrdID watermark, bot mappings and the one-per-original pending
cancel registry survive restart. A retry observes the existing cancel ClOrdID
and does not allocate or send another mutation. The eventual cancel/reject ER
remains the authoritative resolution.

Replace intents distinguish two gateway failure classes. A proven pre-send
failure appends `OrderReplacePreSendFailedEvent`, terminally removing the
pending intent so replay cannot resurrect it. An unclassified exception is
recorded as `OrderReplaceAmbiguousMarginHeldEvent`; the intent and ownership
link remain available for a late venue ER. This is deliberately narrower than
Wave 2 / #628: this slice does not blindly resend pending cancel/replace
intents after restart. Durable attempted/sent substates, session-version proof
and automated reconciliation remain owned by that RFC.

Before appending a cancel/replace resolution, the platform fsyncs an
out-of-band marker under `<data>/<firm>/reconciliation/`. WAL backpressure is
retried and the resolution is flushed before the marker is removed. If the WAL
remains saturated/faulted, the marker survives process crash; startup replay
then applies the safest known posture and begins drain instead of treating the
request as an ordinary pending mutation. Proven-unsent intents are removed
(and replace margin aborted); ambiguous replaces retain margin and are
TTL-marked. Operator reconciliation is required before ingress can reopen.
Marker publication fsyncs the marker file, renames it into place, then fsyncs
the reconciliation directory; removal deletes the file and fsyncs the
directory again. Unsupported directory-fsync platforms/filesystems fail
explicitly rather than acknowledging false durability.
The staging `<id>.json.writing` entry is itself directory-fsynced before
rename and is a valid recovery artifact. Startup validates both final and
staging files, deterministically deduplicates equal pairs, and drains on
partial, conflicting, or unexpected artifacts instead of skipping them.
If sidecar publication fails before that first directory fsync, the engine
attempts the WAL resolution directly. When both channels fail, it retains the
original unresolved pending intent in memory and drains; cleanup/TTL marking
is applied only when either the WAL resolution or sidecar is known durable.
After every cold snapshot+WAL+sidecar replay, any remaining pending cancel or
replace is treated as lacking current-process send proof. Recovery preserves
its ClOrdID/ownership state but begins drain before readiness opens; Wave 1
never converts such intents into idempotent success or blind resend. Venue or
operator reconciliation remains the #628 boundary.
On first use, each newly-created data/firm/reconciliation directory is followed
by an fsync of its parent before the store becomes available, so the
reconciliation directory entry itself also survives power loss.

Proven pre-send cancel failures follow the same model. A durable
`OrderCancelPreSendFailedEvent` consumes the pending cancel, removes its
cancel-side ownership/bot mappings, and leaves the original order working so a
retry allocates a fresh ClOrdID and actually reattempts the venue mutation. If
that resolution cannot append/flush, the sidecar remains, live state is cleaned
up, ingress drains, and the caller receives `reconciliation_required`;
ordinary retries remain blocked until operator reconciliation.

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

### Application-consistent backup and restore drill

Do not copy the live named volume: the WAL writer, snapshot pointer, SQLite
WAL, and DataProtection keys can otherwise represent different instants.
Use [`scripts/backup/backup-and-restore-drill.sh`](../scripts/backup/backup-and-restore-drill.sh).
It:

1. gracefully stops the host, which closes ingress, flushes the WAL and writes
   the final snapshot;
2. refuses the backup unless `latest.txt` references a matching snapshot and a
   WAL segment exists;
3. archives the entire `b3-trading-data` volume with a SHA-256 manifest;
4. restores into an isolated volume, verifies every file, and boots the exact
   image in `Mode=Real` against the sandbox matching/market-data stack;
5. fails unless the seeded bot credential remains queryable, snapshot/WAL and
   reconciliation-sidecar recovery leave readiness open, and a fresh crossed
   order pair reaches `Filled`;
6. gracefully releases the restored FIXP session, restarts the original host,
   and requires its real-mode readiness to return.

The scheduled `.github/workflows/recovery-drill.yml` seeds durable state and
runs this procedure weekly. A storage platform with atomic volume snapshots
may replace the stop window only if it snapshots the complete volume as one
unit and runs the same isolated restore/boot verification.

`SnapshotService` is a `BackgroundService` that fires every
`Trading:Persistence:SnapshotInterval` (default 5 min) and once more on
graceful shutdown. It captures:

- `WorkingOrderBook` (open orders only)
- `PositionKeeper` (non-flat positions only)
- `KillSwitchService` (killed end-clients + firms)
- `OrderOwnershipMap` (`ClOrdID → endClientId`)
- `ClOrdIdPrefixRegistry` (`_nextPrefix` watermark + per-end-client
  counter watermarks)
- `CashLedger` balances keyed by `(firmId, endClientId)`. New snapshots set
  `CashBalancesFirmScoped=true`. Legacy owner-only rows are migrated only when
  exactly one firm can be inferred from recovered orders,
  `Trading:Auth:Users`, or `Trading:Cash:Seeds`; conflicting or absent hints
  fail startup. A seed used as the mapping never overwrites or adds to the
  migrated balance.
- operator deposit/withdrawal `CashKeeper` balances use the same firm scope.
  Their snapshot dictionary keys are `{firmId}|{endClientId}`; legacy plain
  keys also restore only into `DEFAULT`.

After the raw state is captured under the dispatcher lock, the lock is released
and `SnapshotService` awaits `FlushThroughAsync(snapshotSeq)`. Projection and
publication happen only after the marker proves that complete prefix durable;
failure or cancellation publishes nothing. Recovery ignores a snapshot whose
sequence is ahead of `LastCommittedSeq` and falls back to full committed-WAL
replay. Generation/lineage metadata remains the follow-up scope of #638.

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

`POST /admin/eod` (admin-only) validates the persisted marker generation and
complete committed segment manifest, reads each segment only through its
recorded `EndOffset`, and writes
`data/{firm}/eod/eod-{date}.json`:

- Counts per `WalEvent` kind.
- SHA-256 over concatenated payloads (content checksum for diffing).
- File path of the materialised summary.

CRC-valid survivor frames beyond the marker never enter the report. A missing
or inconsistent marker/segment/metadata tuple fails closed. Pre-marker WAL is
read only when `LegacyWalStartupMode=ControlledCleanShutdown`, using the same
full-prefix validation as controlled migration.

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
