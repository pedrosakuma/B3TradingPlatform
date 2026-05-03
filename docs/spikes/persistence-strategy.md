# Spike: persistence strategy for the participant-side OMS

> **Status:** exploratory. Not a decision; input for a future RFC if/when
> HA / multi-instance pressure becomes concrete.
> **Author:** opened from the question "FIX engines tend to use files, but
> exchange-side might be different — what does the market actually do?"
> **Scope:** persistence + durability + recovery options for this repo's
> server process. Excludes browser/client storage and out-of-process
> reporting warehouses.

## TL;DR

- **The current `FileEventStore` + snapshot design is consistent with mainstream FIX-engine and OMS practice.** Append-only files, async fsync, snapshots-as-cache, ER stream as source-of-truth — this is what QuickFIX, Chronicle, and most sell-side OMS engines do on the hot path.
- **Swapping the WAL for a SQL database (Postgres/MySQL) would be an anti-pattern**: 10×–100× the write latency, more failure modes, and zero gain over what the FileEventStore already provides for our single-host, single-tenant deployment.
- **The real question is HA, not "files vs DB".** When we eventually need HA, the idiomatic answers in this space are (in order of fit): replicated journal (Aeron/RAFT-style), EventStoreDB, or Postgres with logical replication used **only as a secondary projection** behind a hot in-memory state.
- **Recommendation:** keep the FileEventStore; do **not** open a "move to SQL" issue. Open a focused HA spike only when there's a concrete deployment requirement that demands more than one process can serve.

---

## What the market actually does

### 1. FIX engines (the layer we sit on top of)

The dominant pattern across QuickFIX, QuickFIX/J, FIX Antenna, Onixs, and similar engines is:

- **File-based message store as the default**, because the FIX session protocol's recovery primitive (`ResendRequest` / sequence numbers) only needs an append-only ordered log of sent/received messages.
- Optional database backends exist (`MySqlStore`, `MSSqlStore`, `PostgresStore`) for environments that want centralised audit/compliance. These are well-known to be **2×–10× slower** than the file store — the engineering trade-off is "slower hot path, easier ops/audit".
- The file is a **session-level concern**, not a business-level event store. Sessions get rotated/archived; long-term reporting goes through a separate projection.

This matches what we have. `FixpClientState` (SDK 0.8.0) handles its own session-state file via `FileSessionStateStore` (#17 wired this), and our `FileEventStore` covers the ER application log on top.

### 2. Sell-side OMS / EMS (peer of what this repo is)

The non-secret architecture talks (CME, FIA Tech, Aeron/Real Logic, OpenHFT) converge on:

- **Hot path is in-memory, deterministic, single-writer.** Order book, working orders, positions all live in memory; the process is event-sourced over an append-only journal.
- **Persistence is journaling, not database writes.** Tools used in production: **Chronicle Queue** (Java/Kotlin shops), **Aeron archive + cluster** (Java, RAFT-replicated log), **EventStoreDB** (.NET-friendly). All three are append-only, file-backed, microsecond-class.
- **Database is a secondary projection** (CQRS read model) updated asynchronously off the journal, used for reporting / compliance / risk analytics — never for hot-path state.

The most cited public reference for this pattern is the Aeron Cluster + Chronicle Queue stack (Real Logic / OpenHFT), and the LMAX architecture talks. EventStoreDB markets itself explicitly to this OMS niche.

### 3. Exchange-side matching engines (what `B3MatchingPlatform` represents)

Even more allergic to databases on the hot path:

- LMAX (the open architecture talks), Nasdaq INET, CME Globex, Eurex T7 — all journal-based, in-memory state, replicated via custom binary log shipping or RAFT consensus over the journal.
- The pattern is "in-memory + replicated journal + SSDs + zero database in the request path". Databases only ever appear in the surveillance / settlement plane, behind a queue.

So the user's intuition is right: exchange-side is **not** different in the "uses a database" direction — it's even more file/journal-centric than FIX engines.

### 4. Where databases legitimately appear

- **Reporting / regulatory archive** (T+1 batch loads from the journal).
- **Reference data** (instrument master, end-of-day prices, user/firm config) — not transactional state.
- **Customer-facing CRUD** (account settings, watch lists) — not order state.
- **Multi-tenant SaaS OMS** offered as a hosted product, where the operator can't deploy per-tenant journals — but those vendors still typically front-end the DB with an in-memory engine.

None of these apply to the hot path of order/ER state in this repo today.

---

## What we have today

`backend/src/B3.Trading.Infrastructure/Persistence/FileEventStore.cs` and the snapshot service (Phase 6 / checkpoint 004) implement, per firm:

- Append-only segmented WAL (`.log` + sparse `.idx`), 64 MiB rotation, per-day directories.
- CRC32 record framing, length-prefixed JSON payload.
- Async fsync (justified by the ER source-of-truth invariant — see `docs/PERSISTENCE.md`).
- Periodic snapshot to compact recovery time; `latest.txt` pointer for O(1) boot.
- EOD reconciliation summary (`eod-YYYY-MM-DD.json`).

This is structurally the same as what Chronicle Queue and EventStoreDB give you, hand-rolled to fit our footprint. The on-disk format is documented and stable.

**Properties we already have:**
- Crash recovery from the WAL.
- Idempotent ER replay (PR #16) — duplicates land safely.
- Warm restart with `SessionVerId` continuity (PR #17).
- Bounded memory via snapshots.

**Properties we don't have:**
- Replication. A single host failure loses the in-flight in-memory state until the ER stream replays it from the broker (which the source-of-truth invariant says is fine, but there's a 1-N second blast radius during failover).
- Multi-writer / multi-process. The store is per-process, locked.
- Cross-process query without going through the running host.

---

## Options if/when HA becomes a requirement

Listed in order of "fits our existing design" rather than "most popular".

### Option A — leader/follower journal replication (extend `FileEventStore`)

Add a tail-and-ship process that mirrors `.log` segments to a follower host as they're appended. The follower replays into its own snapshot/WOB and stays warm. On primary failure, the follower promotes itself, takes over the FIXP session under a new `SessionVerId` (we already handle that bookkeeping).

- **Pros:** minimal new dependencies; reuses everything we have; the on-disk format is already sane for tailing.
- **Cons:** we own the replication protocol (split-brain prevention, fencing tokens, lag monitoring). Non-trivial to get right.
- **Fit:** good if we want one extra hot standby and nothing else.

### Option B — EventStoreDB

Drop the `FileEventStore` implementation behind the existing interface and persist to EventStoreDB instead. EventStoreDB has a maintained .NET 10 client, gRPC transport, RAFT consensus across 3+ nodes, projections for read models, and supports our exact append-only / replay model.

- **Pros:** managed durability + replication + projections out of the box. Our domain code doesn't change shape (it's already event-sourced). Open source, .NET-native ergonomics.
- **Cons:** an extra service to operate (3 nodes for HA). Latency goes from "local fsync" to "gRPC round trip + RAFT commit" — typically still <1 ms but not zero. Operator burden for backup/upgrade/snapshot.
- **Fit:** good when we want HA + the read-model story without writing it ourselves.

### Option C — Aeron Cluster + a journal library

The "industry-canonical" OMS stack, but Java-first. .NET ports exist (`Aeron.NET`, `Adaptive.Aeron.Cluster`) but are less mature than the JVM equivalents.

- **Pros:** ceiling on latency is the lowest of the three options; the same pattern that LMAX/CME-style systems use.
- **Cons:** ecosystem maturity in .NET; significant rewrite of the journaling/state machine plumbing. Operator complexity is not trivial.
- **Fit:** only if we need μs-class latency with HA, which we currently don't.

### Option D — Postgres / SQL as the source-of-truth

Listed for completeness because the original question raised it.

- **Cons:** writes go from "appended bytes flushed asynchronously" to "WAL + transaction commit + index updates"; expect 10×–100× the per-record latency. Adds a service to operate. Doesn't actually buy HA without further work (still need streaming replication, failover orchestration, application-level fencing).
- **Fit:** if we want a SQL-queryable view of order state, the right answer is to project from our existing journal into a Postgres read model — **not** to make Postgres the write path. The existing event-sourced shape is already CQRS-friendly.

---

## Decision criteria (what to watch for)

This spike doesn't recommend a switch. It recommends watching for any of these to flip:

1. **Multi-instance deployment**: we want N>1 trading-host replicas of the same firm to share working-orders state. Today this is impossible by design (single writer per firm).
2. **Sub-second failover SLA** — the operator needs the platform back in <1s after a host crash, faster than `SessionVerId` warm-restart + ER replay (which is bounded by broker latency, typically O(seconds)).
3. **Reporting / surveillance team** — wants ad-hoc SQL across orders/positions historical data without going through the running process. (This is the easiest to satisfy: project the journal asynchronously to Postgres; doesn't touch the hot path.)
4. **Disk failure rate** — single-host SSD failures become a real production loss. (Mitigatable cheaper with RAID-1 / replicated block storage on the cloud provider, before going full HA.)

If none of these are pressing, the FileEventStore is the correct answer — and that aligns with what FIX engines and exchange-side stacks both do.

---

## References

- Aeron / Real Logic: <https://aeron.io/>, talks by Martin Thompson on LMAX/Real Logic architecture.
- Chronicle Queue (OpenHFT): <https://github.com/OpenHFT/Chronicle-Queue>.
- EventStoreDB: <https://www.eventstore.com/eventstoredb> — open source, .NET client, designed for this exact workload.
- QuickFIX message store comparison and benchmarks: QuickFIX engine docs + community threads (file vs DB store latency typically 2–10×).
- LMAX architecture (Martin Fowler): <https://martinfowler.com/articles/lmax.html>.
- Our current design: `docs/PERSISTENCE.md`, checkpoint `004-phase-6-event-sourced-persiste.md`.

---

## Recommendation

1. Keep `FileEventStore` as-is. The current design is mainstream-correct for our deployment shape.
2. Do **not** open a "move to SQL" issue. SQL belongs in a future read-model projection, never on the write path.
3. Defer HA work until at least one of the four decision criteria above becomes concrete. When it does, write a focused RFC comparing **Option A (replication of our own journal)** and **Option B (EventStoreDB)**; skip C and D unless requirements drift very far from where we are.
