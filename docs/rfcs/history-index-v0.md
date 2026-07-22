# RFC: Indexed reader for `/api/orders/history` v0

| Field    | Value                                                                |
| -------- | -------------------------------------------------------------------- |
| Status   | Proposed                                                             |
| Tracking | [#453](https://github.com/pedrosakuma/B3TradingPlatform/issues/453)  |
| Refs     | [#439](https://github.com/pedrosakuma/B3TradingPlatform/issues/439) (B3 compliance audit — TODO sweep), [#455](https://github.com/pedrosakuma/B3TradingPlatform/issues/455) (deferral note) |
| Replaces | n/a (closes `TODO(history-index)` at `HistoryEndpoints.cs:42`)       |

## 1. Context

`GET /api/orders/history` and `GET /api/executions/history` (RFC §4.2) are the
operator/trader-facing read surfaces over the order lifecycle. Today both
materialise their result by walking the **entire WAL from genesis** on
every request:

- `ProjectOrdersAsync` → `store.ReadFromAsync(0, ct)` (`HistoryEndpoints.cs:202`)
- `ProjectExecutionsAsync` → `store.ReadFromAsync(0, ct)` (`HistoryEndpoints.cs:444`)

The cost is **O(N) in WAL depth**, not in page size. The class header
already flags this as `TODO(history-index)` (`HistoryEndpoints.cs:42`),
and the B3 compliance TODO sweep (#439) called it out as a scale limit.

At participant-side volumes (≤ 30k events/day, RFC §4.2) a full scan
costs single-digit milliseconds and is acceptable. The concern is purely
**forward-looking**: as WAL retention grows (multi-day, multi-month) the
per-request cost grows linearly and the read path eventually dominates.
This was explicitly triaged as **"infra investment, no immediate ROI"**
and deferred via #455 → #453.

This RFC does **not** ship code. It maps the limitation to a concrete
design, names the invariant that makes the naive fix wrong, decomposes
the work into shippable sub-issues, and recommends **deferring
implementation** until a measured trigger fires.

### 1.1 Why the naive fix does not work

The obvious idea — "the WAL is day-segmented on disk
(`{DataDirectory}/{FirmId}/wal/{yyyy-MM-dd}/*.log`), so for a date-range
query just read the day-segments in range" — is **wrong**, and this is
the crux of the whole issue.

The history projection is **stateful**. Execution Reports from the venue
carry no `owner` / `symbol` / `side`. The endpoints reconstruct those by
backfilling from an in-memory side-table (`ownerByClOrdId`) that is
populated by the original `OrderSubmittedEvent` / `OrderReplaceRequestedEvent`
(`HistoryEndpoints.cs:448-453`). A fill that lands **today** can reference
an order that was **submitted five days ago**. There are further
dependency links — `cancelLinks` / `replaceLinks` (`HistoryEndpoints.cs:440-441`,
mirroring `OrderOwnershipMap.RegisterCancelLink/RegisterReplaceLink`) —
so that cancel/replace acks the venue emits with `OrigClOrdId=0` still
resolve back to the originating order's owner for the firm-isolation
filter.

**Consequence:** any indexed reader must either (a) persist the
*resolved* owner/symbol/side alongside each indexed row, or (b) retain
enough back-pointer state to resolve the chain without re-reading
genesis. A pure seq/offset index that still needs the genesis side-table
buys nothing.

## 2. Goals

1. **Close `TODO(history-index)`** — replace the O(N)-per-request
   genesis scan with an indexed read whose cost scales with the result
   page, not WAL depth.
2. **Preserve the external wire contract.** The opaque
   `cursor={seq, ts, snapshotSeq}` envelope, `MaxLimit=500`,
   `DefaultLimit=100`, firm-isolation, and owner/symbol/date filters
   stay byte-identical. ✓ issue acceptance "wire externo inalterado".
3. **Preserve correctness invariants** — firm isolation, owner backfill
   correctness (including `OrigClOrdId=0` cancel/replace acks), and the
   stable-pagination `snapshotSeq` fence that keeps a mutable order
   projection consistent across pages.
4. **Recoverable index** — the index is a *cache*, never the source of
   truth (AGENTS.md: "the WAL is the source of truth"). It must rebuild
   from the WAL when absent or stale.
5. **Leverage existing primitives** — reuse the day-segment layout and
   `SegmentReader` plumbing that the EOD materialiser already owns,
   rather than introduce a parallel reader stack.

### 2.1 Non-goals

- Changing the WAL format, segment layout, or framing.
- Indexing anything beyond the two history endpoints (no general
  query engine).
- Sub-millisecond targets — the goal is to remove the linear cliff,
  not to win a latency benchmark.

## 3. Invariants that must survive

| # | Invariant | Where it lives today |
| - | --------- | -------------------- |
| I1 | WAL is the source of truth; the index is a rebuildable cache. | AGENTS.md "Per-session state in Application" |
| I2 | Owner/symbol/side backfill from the prior submit/replace, including `OrigClOrdId=0` ack resolution. | `HistoryEndpoints.cs:434-470` |
| I3 | Firm isolation — a request only ever sees its own firm's rows. | `OwnerMatches` + firm filter in both projections |
| I4 | Stable pagination — `snapshotSeq` fence freezes a mutable order projection across pages. | cursor `SnapshotSeq`, `ProjectOrdersAsync:207` |
| I5 | Wire-stable cursor envelope. | `EncodeCursor` / `TryParseCursor` (`:642-676`) |

## 4. Existing primitives (what we build on)

- **Day-segmented WAL.** `EodMaterialiser` reads
  `{DataDirectory}/{FirmId}/wal/{yyyy-MM-dd}/*.log` with a fresh
  `SegmentReader` per `.log` file
  (`Infrastructure/Persistence/EodMaterialiser.cs:39-54`). The
  date→segment-tree mapping already exists.
- **`IEodMaterialiser`** — an Application-layer port over the
  Infrastructure `EodMaterialiser` concretion that "owns the
  segment-reader plumbing" and already performs **one ordered pass over
  a day's segments** producing a per-`(date, firm)` summary
  (`IEodMaterialiser.cs:28-50`). This is the pass to piggy-back on.
- **`IEventStore.ReadFromAsync(sinceSeqExclusive)`** — already supports
  skipping to a seq via the FileEventStore sparse index
  (`IEventStore.cs:57-62`); the history endpoints simply pass `0`.
- **Snapshot/seq machinery** — snapshots already record a reference seq
  after `FlushAsync`, giving a natural `(snapshotSeq → resolved state)`
  anchor for the stable-pagination fence (I4).

## 5. Design

### 5.1 Shape: a resolved per-day history index

At end-of-day, in the **same pass the EOD materialiser already makes**
over a day's segments, emit a compact, **owner-resolved** index sidecar
per `(date, firm)`:

```
{DataDirectory}/{FirmId}/history-index/{yyyy-MM-dd}.idx
```

Each index row is the already-resolved projection tuple, sorted by
`(ts, seq)`:

```
(ts, seq, owner, symbol, side, clOrdId, kind, status, qty, leaves, price, …)
```

Because the row carries the **resolved** owner/symbol/side (I2), a
range query reads only the index files whose day falls in `[from, to]`
plus the cursor offset — no genesis walk. The cross-day dependency
(fill today → submit last week) is resolved **once, at write time**,
when the EOD pass for the fill's day still has the side-table warm from
that day's own submits **plus a carried-forward open-order table**
seeded from the prior day's index tail. This carried-forward open-order
table is the only stateful bridge and is bounded by the count of
*still-open* orders, not by WAL depth.

### 5.2 Read path

`MaterializeRowsAsync` (the full-WAL scan) is replaced by an indexed
reader that:

1. Resolves the `[from, to]` filter to the set of day-index files.
2. For each day file, binary-searches to the cursor `(ts, seq)` offset
   (the wire-stable cursor already carries `seq` + `ts`, I5).
3. Streams rows, applying owner/symbol/firm filters (cheap — fields are
   in the row), until the page is full.
4. **Hot-day fallback:** the *current* (not-yet-EOD) day has no index
   file. For that day only, fall back to a bounded WAL read —
   `ReadFromAsync(startOfDaySeq)` seeded by the carried-forward
   open-order table — instead of `ReadFromAsync(0)`. This keeps the hot
   path O(today), not O(history).

### 5.3 Recovery (I1)

The index is a cache. On boot or on a missing/short/corrupt day file:

- **Missing** `{date}.idx` → reconstruct by running the same EOD pass
  for that date on demand (it is deterministic over the day's segments).
- **Corrupt / length-mismatch** → discard and reconstruct.
- The reconstruction primitive is *literally the EOD materialiser pass*,
  so there is one code path for "produce the index", shared between the
  nightly job and the recovery path. No second implementation to drift.

### 5.4 Storage decision (issue acceptance item 1)

Three candidates were weighed:

| | A) SQLite sidecar / day | B) Append-only `.idx` (sorted rows) | C) Extend EOD materialiser pass → flat resolved index |
| --- | --- | --- | --- |
| Query owner+range | native index | binary-search on `(ts,seq)` | binary-search on `(ts,seq)` |
| New dependency | **yes** (SQLite on hot path) | no | no |
| Resolves I2 statefulness | store resolved row | store resolved row | resolved *during* the pass |
| Shares code with EOD pass | partial | partial | **fully** (one pass) |
| Recovery | rebuild from WAL | rebuild from WAL | rebuild = re-run the pass |
| Complexity | medium (schema, migrations) | low-medium | **low** (reuse) |

**Recommendation: Option C** — extend the EOD materialiser to emit a
flat, owner-resolved, `(ts, seq)`-sorted index file per `(date, firm)`,
binary-searchable for the cursor. It avoids a new hot-path dependency,
reuses the day-segment `SegmentReader` and the single ordered pass that
already exists, and collapses "build" and "recover" into one
deterministic function. SQLite (A) is reconsidered in v1 only if a
richer ad-hoc query surface is ever required.

## 6. Decomposition (sub-issues)

1. **`history-index` writer** — extend the EOD pass to emit
   `{date}.idx` with the resolved row schema + carried-forward
   open-order tail. (Infrastructure)
2. **Indexed reader** — `IHistoryIndexReader` Application port; swap
   `MaterializeRowsAsync` for it behind the existing projection methods;
   keep the hot-day WAL fallback. (Application + Api)
3. **Recovery / on-demand rebuild** — reconstruct a missing/corrupt day
   file via the shared EOD pass. (Infrastructure)
4. **Benchmark harness** — synthetic 1M-event WAL; assert ≥ 10×
   improvement on a representative owner+range query vs the genesis
   scan (issue acceptance item 4). (Tests)
5. **Wire-parity test** — golden test asserting the indexed reader
   produces byte-identical pages + cursors to the genesis scan for a
   fixed corpus (guards I2–I5). (Api.Tests)

## 7. Recommendation: build the design, defer the code

The issue is explicitly tagged **"sem ROI imediato"** at current volumes
(≤ 30k events/day → low-ms scans). Shipping the index now spends infra
budget on a problem that does not yet bite, and adds a cache to keep
coherent across recovery for no measured user-facing win.

**Proposed trigger to dequeue implementation** (any one):

- Sustained WAL retention crosses ~250k events (≈ 8+ trading days at the
  RFC §4.2 ceiling), **or**
- A measured `/api/orders/history` p99 crosses ~50 ms on the production
  pool, **or**
- A compliance/audit requirement forces longer online retention.

Until then: this RFC stands as the agreed design, the `TODO(history-index)`
comment links to it, and #453 stays open as "designed, deferred".

## 8. Open questions

- **O1 — Index granularity.** Per-`(date, firm)` is assumed. Do any
  tenants need sub-firm (per-end-client) index files for hot isolation,
  or is in-row filtering sufficient at expected fan-out?
- **O2 — Carried-forward open-order tail bound.** Is the count of
  simultaneously-open orders safely bounded (it should be, by working-
  order limits), or do we need a cap + spill strategy?
- **O3 — Retention vs index lifecycle.** When a day's WAL segments are
  pruned by retention, is the `{date}.idx` the surviving artifact for
  history, or are both pruned together? (Affects whether the index
  becomes a de-facto archival source — which would violate I1.)
- **O4 — Benchmark corpus realism.** Should the 1M-event harness model a
  realistic open/fill/cancel mix and cross-day order lifetimes, or is a
  uniform synthetic stream enough to demonstrate the 10× claim?
