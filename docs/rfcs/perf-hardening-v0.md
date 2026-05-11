# RFC: Performance hardening v0 (pre-production)

| Field    | Value                                                              |
| -------- | ------------------------------------------------------------------ |
| Status   | Proposed                                                           |
| Tracking | [#189](https://github.com/pedrosakuma/B3TradingPlatform/issues/189) |
| Replaces | n/a (pure perf hardening on top of #166 + #183)                    |

## 1. Context

After the FIXP listener landed (#166 → #174) and outbound multiplexer
+ retransmit completed (#172, #173, #183), the trading-host now has
three inbound order surfaces (REST, WS, FIXP) and one outbound bot
ER fan-out path on top of the existing exchange gateway. A focused
perf review (`gpt-5.5`) on the post-#183 codebase identified **4
Critical** and **5 High** bottlenecks, summarised in #189.

A hand-traced budget — JSON serialisation cost, lock-hold time on
the dispatcher, ER fan-out work, outbound socket fan-out — puts
the **current sustained ceiling at ~10–50k msg/s** on a single
participant pool, with the entire pipeline (REST submit →
pre-trade → WAL durable → matching → ER → bot delivery) under
load. The dominant constraints are:

- **Dispatcher lock width.** `EventDispatcher.Dispatch` holds one
  process-global lock around `IEventStore.Append` (which itself
  serialises the event to JSON) AND the in-memory `apply()`
  callback (which performs the ER fan-out, including a synchronous
  call into `IExecutionEventSink.Publish` and `IBotErRouter.Route`).
  Every order submission, every ER, every cancel contends here.
- **Outbound socket fan-out.** The FIXP outbound path schedules a
  fresh `Task.Run` per ER per bot session; 10k ERs/s with 50 active
  bots = 500k Task allocations/sec.
- **Allocation pressure.** ER bytes are copied at least twice
  (encoder allocates a `byte[]`, buffer copies it again with
  `bytes.ToArray()`); inbound SOFH frames are copied via
  `Payload.ToArray()` per message; snapshot capture builds a fresh
  `List<>` for every projection under the dispatcher lock.

**Target for v0:** lift the single-pool sustained ceiling to a
**B3-realistic ~100k msg/s** with end-to-end p99 latency in the
low single-digit milliseconds for the REST → WAL durable → bot ER
delivery path, **without** adding a second machine, a sharded WAL,
or a non-managed dependency. The goal is to remove obvious
serial-section / allocation cliffs, not to rewrite the platform.

This RFC does **not** ship code. It maps each finding to a concrete
design, calls out the invariants that must survive every fix,
sequences the work so prerequisites land first, and decomposes
into shippable sub-issues.

## 2. Goals

1. **Address every Critical/High finding from #189.** No silent
   drops; every item gets either a concrete design or an explicit
   "deferred to v1, here's why" note.
2. **Preserve every existing correctness invariant.** Performance
   work that compromises WAL durability, total ordering, snapshot
   consistency, ClOrdId monotonicity, or single-active-session-per-
   credential is unacceptable. §3 enumerates the contract.
3. **Make backpressure first-class.** Today the pipeline mixes
   "unbounded channel + assume-fast-enough" with "throw on full"
   ad-hoc per call site. v0 picks one coherent story per origin
   (FIXP / REST / WS) and documents it.
4. **Land a measurement harness.** `BenchmarkDotNet` micro-benchmarks
   plus an integrated load test that drives synthetic order submit →
   ER → bot delivery, so each subsequent perf PR has a number to
   beat instead of a vibe.
5. **Decompose into independently shippable PRs.** Every sub-issue
   is one focused change with its own bench delta, its own
   review surface, and its own risk envelope.

## 3. Non-goals

- **Sharding the WAL across firms / pools.** v0 stays single-writer
  per process. The fact that `FileEventStore` is per-firm already
  gives multi-pool horizontal scaling at the deployment layer.
- **Replacing `JsonSerializer` with a custom binary WAL format.**
  Source-generated JSON contexts (mandatory in v0, see §6.1) close
  most of the gap; a binary format is a v1+ RFC if benchmarks
  show JSON encode is still the bottleneck after sourcegen.
- **Lock-free / wait-free data structures inside the dispatcher.**
  We narrow the lock; we do not eliminate it. The "single global
  serialisation point" is what makes the WAL→state ordering
  invariant trivial to reason about. Lock-free is v2+ territory.
- **Multi-reader fan-out for the bot ER router.** v0 collapses
  the global multiplexer channel into a synchronous resolve in
  `Route` (§5.4); sharding by credential to N readers is
  documented as the alternative for v1 if the per-sink dispatcher
  drain thread becomes the next ceiling.
- **End-to-end zero-copy from socket-recv buffer to WAL payload.**
  We eliminate the ER outbound double-copy and the inbound
  `ToArray` per frame, but the WAL still owns its own buffer
  (it must — the channel writer survives the originating call).
- **Tuning fsync defaults for cloud / non-local-NVMe disks.** v0
  raises defaults appropriate for direct-attached NVMe; cloud
  block storage gets a separate options profile in a follow-up
  if/when somebody deploys there.
- **Synchronous-fsync ack mode.** The current "ack-after-channel-
  enqueue" semantics is preserved; an opt-in
  fsync-before-ack mode for compliance-sensitive deployments is
  a v1 RFC topic (§13).

## 4. Invariants — MUST NOT change

These are carried verbatim from the FIXP RFC (§4 of
`user-bot-fixp-listener-v0.md`) and from the persistence design
in `EventDispatcher`. Every proposal in §5 is gated on preserving
them. Property-based tests (§7) enforce them in CI.

### 4.1 Total WAL ordering

Every event written to the WAL has a strictly monotonic `seq`
assigned under the dispatcher lock. Replaying events in `seq`
order from any starting point reproduces the same in-memory
state. **Any change that allows two events to be appended out-of-
order with respect to their causal application is a regression.**

Concretely: if event B's `apply()` observes the side effects of
event A's `apply()`, then B's `seq > A's seq`. The current
implementation guarantees this trivially because `Append` and
`apply` share one lock. Lock-narrowing proposals (F1, F2) must
preserve this by design, not by accident.

### 4.2 Durability semantics

An event is "acknowledged-as-applied" to its originator only
after (a) the WAL `Append` returns successfully — meaning the
record has been JSON-serialised, seq-assigned, and **enqueued**
onto the bounded `Channel<PendingRecord>` whose drain thread
fsyncs at the configured group-commit cadence — and (b) the
in-memory `apply()` callback has executed to completion.

**This is "ack after enqueue + apply", not "ack after fsync".**
Events that have been acknowledged but not yet fsynced (those
sitting in the channel queue or in the in-progress batch)
will be lost on a hard kernel crash. The current upper bound
on at-risk records is `ChannelCapacity + GroupCommitMaxRecords`
(today: 4096 + 64 = 4160). F7 widens `GroupCommitMaxRecords`
and therefore widens this bound to 4096 + 512 = 4608; the
trade-off is documented explicitly in §5.7.

Backpressure (channel-full) is surfaced as `WalBackpressureException`
to the caller, which is responsible for refusing the originating
client request. **No fix may introduce a path where an event is
"applied in memory but not even enqueued onto the WAL channel"**,
because such a path makes recovery diverge from the live state.

The one exception is the existing
`EntryPointExecutionReportRouter` fallback (`router.cs:70-80`):
on WAL backpressure it intentionally applies the ER without the
WAL record. That path is documented and metric'd; v0 keeps it,
because losing the ER audit log is preferable to losing the
state mutation (a partial fill we drop is an open position we
think isn't there). F2's lock-narrowing must not extend this
"apply without log" exception to other call sites.

### 4.3 Snapshot consistency

`PlatformSnapshot.Seq = N` means the snapshot's in-memory state
reflects every event with `seq ≤ N` and zero events with
`seq > N`. Recovery: load snapshot → replay events with
`seq > N` → identical state. **Any change that lets an event
mutate state visible to the snapshot capture without that
event's `seq` being included in the snapshot's `Seq` field is a
regression.**

This is what `EventDispatcher.WithSnapshotLock` exists to
guarantee. F8 (snapshot allocation discipline) explicitly keeps
the lock around the *capture-snapshot* point but moves the
sort/serialise work outside, which is safe iff the captured
arrays are stable references.

### 4.4 ClOrdId monotonicity per owner

`ClOrdIdPrefixRegistry` allocates `ulong` ClOrdIds per owner
prefix and guarantees monotonicity within an owner across
restarts (the registry's snapshot watermark is replayed). **No
fix may reorder ClOrdId allocation with respect to WAL
`OrderSubmittedEvent` write, because recovery relies on
`max(seen ClOrdId in WAL) ≤ registry watermark` to avoid double-
issuing an id.**

This pins F2 (ER fan-out outside lock) to a specific shape: the
fan-out happens AFTER the dispatch returns, but ClOrdId
allocation happens BEFORE the dispatch is even called (in the
submit service), so the two are independent. We just have to
not move ClOrdId allocation to a worker thread.

### 4.5 Single active session per credential

`UserBotSessionRegistry`'s contract from the FIXP RFC (sub-issue
D, §4.5 of that RFC): at most one Established FIXP session per
credential at any time. New Establish under the same credential
displaces the old one synchronously. **No fix may make session
displacement asynchronous**, because the new session's first
inbound `NewOrderSingle` would race with the old session's WAL
events under the same `ExternalClOrdId` namespace.

F3 (per-connection writer loop) is bounded by this: the loop's
shutdown drain must complete before the registry can hand the
credential to a successor session. The recommended drain
semantics (§5.3.2) make this explicit.

## 5. Per-finding design

### 5.1 F1 — Dispatcher lock scope (Critical)

**Today.** `EventDispatcher.Dispatch` (`EventDispatcher.cs:51`)
holds `_lock` around:
1. `_store.Append(evt)` — which calls
   `JsonSerializer.SerializeToUtf8Bytes<WalEvent>(evt, JsonOptions)`
   inside (`FileEventStore.cs:88`). Reflection-based polymorphic
   serialisation per event.
2. The caller's `apply` callback (the in-memory state mutation +
   any synchronous fan-out the caller decided to inline).

JSON serialisation is the dominant CPU under the lock. With the
current `JsonSerializer.SerializeToUtf8Bytes` (no source-gen),
each event allocates the writer state, walks the polymorphic
dispatch table, allocates a `byte[]` for the result. At 50k
events/s this is most of a CPU core and 100% of it is serialised
behind a single lock.

**Proposal.**

Pre-serialise the event payload **outside** the lock, then hold
the lock only for:
1. seq assignment + channel enqueue;
2. the in-memory `apply()`.

Concretely the contract changes from:

```csharp
long Append(WalEvent evt)
```

to:

```csharp
// Existing path stays for compat; new path takes a pre-serialised
// payload and skips the in-store SerializeToUtf8Bytes call.
long Append(WalEvent evt, ReadOnlyMemory<byte> preSerialisedPayload);
```

`EventDispatcher.Dispatch` then becomes:

```csharp
public long Dispatch(WalEvent evt, Action apply)
{
    var payload = WalEventJson.Serialize(evt); // outside the lock
    lock (_lock)
    {
        var seq = _store.Append(evt, payload);
        apply();
        return seq;
    }
}
```

`WalEventJson.Serialize` uses the **source-generated**
`JsonSerializerContext` (mandatory in v0, see §6.1) so reflection
cost is gone too — encode is a tight switch over the polymorphic
discriminator.

**Trade-offs.**

- *Memory:* one extra `byte[]` allocation per event, but the
  lock-held cost was already an allocation; this just shifts when
  it happens. ArrayPool is **not** used here because the payload
  ownership transfers into the channel writer, which holds it
  past the originating call. Pool-leasing across that boundary
  is a known footgun (see §6.2). The pool is reserved for the
  outbound socket path (F5).
- *CPU:* small savings from avoiding serialisation under
  contention; large savings from sourcegen replacing reflection.
- *Lock contention:* the lock-held window drops by ~80% in
  micro-bench (estimate; sub-issue confirms). With ER fan-out
  also moved outside (F2), the residual lock body is "increment
  a long, enqueue onto a channel, run a small in-memory mutation".

**Threading model implications.** None — `Dispatch` is still
synchronous from the caller's viewpoint. The caller's call stack
sees the same exception surface (`WalBackpressureException`) on
the same code line. The in-memory `apply()` still runs serially
with all other `apply()`s, preserving §4.1.

**Invariants.** §4.1 preserved (seq + apply still under one
lock). §4.2 preserved (Append still happens under the same lock,
backpressure exception still propagates). §4.3 preserved
(`WithSnapshotLock` is unchanged).

### 5.2 F2 — ER apply/fan-out outside the dispatcher lock (Critical)

**Today.** `ExecutionReportProcessor.Apply` is the `apply`
callback for `ExecutionReportReceivedEvent` and
`OrderStaledEvent`. Inside it (`ExecutionReportProcessor.cs:263`)
it synchronously calls `_sink.Publish(ev)` (the WS hub fan-out)
and `_botErRouter?.Route(ev)` (the FIXP outbound multiplexer
enqueue). Both run while the dispatcher lock is held. The WS
sink's `Publish` enqueues onto each subscribed connection's
outbound channel; the bot router's `Route` enqueues onto the
multiplexer's channel. Neither does socket I/O under the lock,
but both walk subscriber lists / dictionaries that grow with
connected client count.

Worse: any subscriber whose `Publish` synchronously allocates
or copies (e.g. building the WS frame) does it under the lock.

**Proposal.**

The dispatcher lock should serialise **state mutation** and
**WAL append**, not **fan-out**. Restructure the apply
callback so it returns a small "what happened" struct that the
dispatcher captures inside the lock and hands to per-sink
channels (also under the lock, see ordering note below) for
asynchronous publication.

```csharp
// New dispatcher overload that captures an outcome under the lock
// and hands it to every registered fan-out sink, all under the
// same lock so subscriber drain order matches WAL seq order.
public (long seq, T outcome) Dispatch<T>(
    WalEvent evt,
    Func<T> applyAndCapture);

// ER processor:
var (seq, ev) = _dispatcher.Dispatch(walEvt, () =>
{
    ApplyToOrderBookAndPositions(...);
    return BuildExecutionEvent(...); // captured under the lock
});
```

**Subscriber semantics — the ordering problem.**

The contract today is "subscribers see ERs in WAL-append order".
A naive lift-and-shift ("release the lock, then call `Publish`")
**breaks this**: thread A appends seq N, releases the lock,
gets preempted; thread B appends seq N+1, releases the lock,
calls `Publish(N+1)`; A wakes and calls `Publish(N)`. Out-of-
order delivery. A second mutex around the publish step does
**not** fix this either — the order in which threads acquire
that mutex after releasing the dispatcher lock is not
guaranteed by the OS scheduler.

We need an ordering primitive that is sequenced **while still
under the dispatcher lock**. The chosen shape:

**Per-subscriber channel, written under the lock; subscriber
drain thread reads + dispatches.**

```csharp
// Each subscriber owns its own SingleReader/SingleWriter channel.
// Enqueue is a TryWrite — fast and non-blocking — and happens
// under the dispatcher lock so order matches WAL seq order.

public (long seq, T outcome) Dispatch<T>(WalEvent evt, Func<T> applyAndCapture)
{
    var payload = WalEventJson.Serialize(evt);
    long seq;
    T outcome;
    lock (_lock)
    {
        seq = _store.Append(evt, payload);
        outcome = applyAndCapture();
        // Hand outcome to each registered fan-out target while
        // still holding the lock. TryWrite is non-blocking (per-sink
        // channel sizing + overflow described below).
        foreach (var sink in _fanOutSinks)
            sink.Enqueue(seq, outcome);
    }
    return (seq, outcome);
}
```

The cost we pay under the lock is "N TryWrite calls into N
channels" — each is a few atomic ops, far cheaper than either
the JSON serialisation (now hoisted) or the actual `Publish`
work (now on the subscriber's drain thread). Subscribers' drain
threads consume in seq order because the channel is FIFO and
the writes happened under the lock.

**Per-sink channel sizing + overflow.** Each per-sink channel
is bounded by default (64K events), but the **bot-router sink
is unbounded** because dropping an ER before credential
resolution is unrecoverable (no per-bot signal can be emitted;
see §5.4 for the full argument). Memory pressure on the
unbounded bot-router channel is bounded transitively by the
per-credential `BotOutboundBuffer.MaxMessages` caps — the
drain thread reads at line-rate (a dictionary lookup + buffer
append per event) and steady-state queue depth is zero.
Per-sink overflow policies:

- **WS hub sink** (bounded): `FullMode = DropOldest` and emit
  a typed `SubscriptionResetEvent` so the WS client knows to
  refetch state. This is the existing WS reconnect-and-resync
  path (already in the WS hub for connection drops); we reuse
  it.
- **`IBotErRouter` (FIXP outbound) sink** (**unbounded** —
  see above): no per-sink overflow exists; the only drop point
  is the per-credential buffer overflow, which already triggers
  the version-bump path documented in §5.4.
- **Algo signals sink** (bounded): `DropOldest` + metric
  `AlgoSignalsDropped` (already exists in `MetricsRegistry`).

**Alternative (rejected): single-writer global publish channel.**

Captures the outcome onto a single in-memory channel inside the
lock; a dedicated global worker dequeues and calls `Publish`
on every sink in order. Strictly ordered, simple. Adds one more
thread hop to the ER critical path and serialises every sink's
work behind one drain thread — exactly the topology F4 is
trying to eliminate for the bot router. **Rejected** in favour
of per-sink channels.

**Invariants.** §4.1 preserved (seq + state mutation still
under `_lock`, and per-sink channel writes happen under the
same lock so subscriber drain order matches seq order). §4.2
preserved (durability gate is unchanged; the publish happens
AFTER `Append` returned successfully). §4.3 preserved (snapshot
capture still sees the same state it did before — publish is a
read-only fan-out, not a state write). §4.5 preserved (no async
session displacement introduced).

### 5.3 F3 — Per-connection writer loop replacing Task.Run-per-send (Critical)

**Today.** `FixpSessionConnection.IBotSessionOutboundSender.TryEnqueue`
(`FixpSessionConnection.cs:1094`) does:

```csharp
_ = Task.Run(async () =>
{
    await _writeMutex.WaitAsync();
    try { await stream.WriteAsync(framedBytes); ... }
    ...
});
```

One `Task` allocation per outbound message per session. At
100k ERs/s × N bots, this is the single largest GC pressure
source in the host.

**Proposal.**

Each `FixpSessionConnection` owns a `Channel<OutboundFrame>`
and a single drain loop, started at session establishment and
torn down at session close. `TryEnqueue` becomes a pure
channel write.

```csharp
private readonly Channel<OutboundFrame> _outboundChannel =
    Channel.CreateBounded<OutboundFrame>(new BoundedChannelOptions(capacity: 4096)
    {
        SingleReader = true,
        SingleWriter = false,
        FullMode = BoundedChannelFullMode.Wait, // see §5.3.1 below
    });

// Started by session-establish path:
private async Task DrainOutboundAsync(CancellationToken ct)
{
    await foreach (var frame in _outboundChannel.Reader.ReadAllAsync(ct))
    {
        try
        {
            await _stream.WriteAsync(frame.Bytes, ct).ConfigureAwait(false);
            TouchOutbound();
            // NOTE: do NOT dispose frame.Owner here. After successful
            // buffer.Append (F5), the buffer is the sole owner and
            // releases the pooled memory on EvictUpTo / overflow.
            // The drain loop only borrows.
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "fixp.outbound.write.error connectionId={ConnectionId}", _connectionId);
            _closed = true;
            try { _stream.Close(); } catch { }
            return; // drain loop ends; the buffer still owns remaining
                    // queued frames and will dispose on overflow / evict.
        }
    }
}

bool IBotSessionOutboundSender.TryEnqueue(OutboundFrame frame)
{
    // The frame was already accepted by buffer.Append in the caller.
    // From this point on, the buffer is the sole owner — TryEnqueue
    // never disposes, regardless of success/failure. A return-false
    // here triggers the per-credential version-bump path; the buffer
    // will dispose its entries when overflow/evict fires.
    if (_closed) return false;
    return _outboundChannel.Writer.TryWrite(frame);
}
```

**§5.3.1 Backpressure when channel full.**

The recommended policy for the FIXP outbound channel is:
**`TryWrite` → on failure, return false to the caller**.
The caller (the `BotErRouter.Route` synchronous path, §5.4)
surfaces this as a per-session overflow which falls into the
**same version-bump path** as `BotOutboundBuffer` overflow today
(see `BotErMultiplexer.cs:86` and `BotOutboundBuffer.cs:79-86`).
The bot is notified of a sequence gap and reconnects with a
`RetransmitRequest` against the WAL-backed buffer. This is the
existing, documented FIXP recovery path; reusing it means we
don't invent a new "connection got behind" semantics. **Drop-
without-signal is not acceptable** for FIXP — the protocol's
correctness depends on per-session sequence continuity, and
silent drops would manifest later as cancel-rejects with no
diagnosable cause.

**§5.3.2 Shutdown drain semantics.**

On Terminate / connection-close:
1. `_outboundChannel.Writer.Complete()` — no more enqueues.
2. The drain loop continues until the reader observes
   completion AND the channel is empty. With per-write timeout
   (e.g. 1s) so a dead peer doesn't block shutdown.
3. After drain (or timeout), the loop returns; remaining
   in-flight frames are still owned by the per-credential
   `BotOutboundBuffer` and are released either by the bot's
   next acked-watermark `Sequence` (`EvictUpTo`) or by the
   bulk-clear on the next overflow. The per-connection drain
   loop **never** disposes pooled memory.
4. **Only then** is the credential considered free for a
   successor session (§4.5). A new Establish under the same
   credential blocks until the previous drain completes or
   times out.

This is what makes the per-connection writer compatible with
the single-active-session invariant.

**Invariants.** §4.5 preserved by the explicit drain gate.
Total ordering of outbound bytes per session is preserved
(per-session channel is FIFO; the multiplexer drain thread and
the connection's own request-reply path both enqueue, but
neither races with itself, and per-session global order is
"as enqueued" which is exactly what FIXP requires).

### 5.4 F4 — Synchronous credential resolve; remove the global multiplexer channel (Critical)

**Today.** `BotErMultiplexer` (`BotErMultiplexer.cs:72`) creates
an **unbounded** `Channel<ExecutionEvent>` with `SingleReader`.
The justification (lines 63-71) is that memory pressure is
allegedly bounded by per-credential outbound buffer caps.

That justification is incomplete: the events queue up in the
multiplexer's channel **before** they reach the per-credential
buffer. A single slow bot (or a flapping connection) means the
ER event sits in the multiplexer's channel waiting for the
single drain thread to get to it — and every other ER queued
behind it waits too. With unbounded growth, OOM is the failure
mode, not backpressure.

But bounding the global channel and dropping on overflow is
**also unacceptable**: at the global drop point the credential
has not yet been resolved, so no per-bot signal can be emitted
and the bot has no way to detect the missing ER. The original
code comment in `BotErMultiplexer.cs:61-71` correctly warned
about exactly this for the bounded variant.

**Recommended path: synchronous resolve in `Route`; remove the
global multiplexer channel entirely.**

Move the mapping lookup **out of the drain thread and into
`Route` itself** — it's a single `TryGetOrderMapping` (a
`ConcurrentDictionary` lookup) plus a session-registry `TryGet`.
At that point we know which per-credential buffer the ER
belongs to, and can append synchronously. Backpressure happens
at the per-credential layer (`BotOutboundBuffer.MaxMessages`)
which already triggers the version-bump on overflow. **The
global multiplexer `Channel<ExecutionEvent>` is removed.**

```csharp
public void Route(ExecutionEvent ev)
{
    if (!_mappings.TryGetOrderMapping(ev.ClOrdId, out var mapping))
        return; // REST/WS-origin order
    var session = _sessions.TryGet(mapping.CredentialId);

    // Encode AFTER the mapping resolves so we never rent a pooled
    // buffer for a frame we won't append (avoids leak path; §5.5).
    var frame = _encoder.EncodeExecutionReport(ev, mapping);
    var buffer = _outbound.GetOrCreateBuffer(mapping.CredentialId);
    var seq = buffer.NextSeq();

    if (!buffer.Append(seq, frame))
    {
        // Buffer overflow → version-bump path (existing #173 wiring
        // via OnBufferOverflow → _overflowChannel). Append disposed
        // the pooled buffer for us per the rule in §5.5.
        return;
    }

    if (session?.OutboundSender is { } sender)
    {
        if (!sender.TryEnqueue(frame))
        {
            // Per-session channel full (F3 / §5.3.1). The frame is
            // already in the per-credential buffer, so retransmit
            // can replay it — but we must force the bot to
            // reconnect to surface the gap. Trigger the same
            // version-bump path as buffer overflow.
            _outbound.SignalSessionOverflow(mapping.CredentialId);
        }
    }
    // session == null → bot offline; the per-credential buffer
    // (always-on) holds the frame for retransmit on next Establish.
}
```

The overflow-signal channel (`_overflowChannel`) stays
unbounded — it carries credential ids only, and the version-
bump rate is bounded by the number of credentials, not the
ER rate.

**Why this is safe under §4.1.** `Route` is called from the
per-sink dispatcher drain thread (§5.2), which reads in seq
order. The synchronous resolve + buffer-append happens in seq
order because the drain thread reads in seq order. **Per-bot
order is preserved by construction.**

**Throughput consideration.** The cost we used to pay on the
multiplexer's drain thread (mapping lookup + buffer append) is
now paid on the per-sink dispatcher drain thread. That's the
same number of CPU cycles, just on a different thread;
throughput is unchanged. We *gain* by eliminating one channel
hop and one allocation per ER (the global
`Channel<ExecutionEvent>` write). We *lose* the implicit
batching the global channel used to provide — but the per-
sink channel introduced in §5.2 provides equivalent batching
(its drain thread reads many events per wakeup).

**Alternative path (documented, not recommended for v0): shard by credential.**

`N` reader threads, each owning a partition of credentials.
Per-credential order trivially preserved (a credential maps to
exactly one shard). Cross-credential order is not preserved,
which is fine because there is no cross-credential ordering
contract to begin with.

Pros: removes the single-drain-thread ceiling; scales linearly
to N cores.

Cons: adds complexity to overflow handling (each shard has its
own bounded channel; overflow is per-shard); rebalance on
credential add/revoke is fiddly; the per-sink dispatcher drain
thread is not yet the bottleneck per micro-bench (the per-event
work is small once F1+F2+F5 land).

**Decision: synchronous resolve + per-credential buffer for v0;
per-credential sharding deferred to v1** behind a benchmark
threshold — when a single per-sink dispatcher drain thread
consistently sustains <70% of the target ER/s under load.

**Invariants.** §4.1 unaffected (Route is post-WAL-append,
post-state-mutation, and runs on a per-sink drain thread that
preserves seq order). §4.5 unaffected (shard decision pinned
to single-active-session invariant if we ever go sharded —
shard-by-credential preserves "one credential, one shard, one
order").

### 5.5 F5 — Eliminate ER outbound double-copy (High)

**Today.** Outbound ER bytes are copied twice:

1. `OutboundExecutionReportEncoder.Frame` (line 169) does
   `var buf = new byte[frameSize]` and writes the SOFH frame.
   Returned as `byte[]`.
2. `BotOutboundBuffer.Append` (line 92) does `var copy = bytes.ToArray()`
   "defensively" because the comment (correctly) notes that the
   caller may pass a pooled buffer.

At 100k ERs/s × ~80 bytes/frame, this is ~16 MB/s of needless
copy + ~100k allocations/s of `byte[]`.

**Proposal.**

Encoder returns `IMemoryOwner<byte>` from a shared
`MemoryPool<byte>.Shared` (default `ArrayPool<byte>.Shared`-backed).
Buffer takes ownership; nobody else disposes.

```csharp
public sealed record OutboundFrame(
    IMemoryOwner<byte> Owner, // null if not pooled
    ReadOnlyMemory<byte> Bytes);

// Encoder:
public OutboundFrame EncodeExecutionReport(...)
{
    var frameSize = SofhFrameWriter.FrameSize(bodyLen);
    var owner = MemoryPool<byte>.Shared.Rent(frameSize);
    var bytes = owner.Memory.Slice(0, frameSize);
    SofhFrameWriter.WriteFrame(bytes.Span, ...);
    return new OutboundFrame(owner, bytes);
}

// Buffer:
public bool Append(ulong seq, OutboundFrame frame)
{
    lock (_gate)
    {
        if (_overflowed) { frame.Owner?.Dispose(); return false; }
        if (_entries.Count >= _maxMessages)
        {
            _overflowed = true;
            DisposeAll();
            _onOverflow?.Invoke(_credentialId);
            frame.Owner?.Dispose();
            return false;
        }
        var node = _entries.AddLast(new Entry(seq, frame));
        _index[seq] = node;
        return true;
    }
}

public void EvictUpTo(ulong throughSeq)
{
    lock (_gate)
    {
        while (_entries.First is { } first && first.Value.Seq <= throughSeq)
        {
            first.Value.Frame.Owner?.Dispose();
            _entries.RemoveFirst();
            _index.Remove(first.Value.Seq);
        }
    }
}
```

**Lifetime contract — explicit, single rule.**

**Rule:** the per-credential `BotOutboundBuffer` is the sole
owner of every `OutboundFrame.Owner` it accepts via `Append`.
No other call site disposes. Period.

| Path                                       | Who disposes the `Owner`               |
| ------------------------------------------ | -------------------------------------- |
| `Append` returns true (accepted)           | Buffer, on `EvictUpTo` or overflow     |
| `Append` returns false (overflow / closed) | `Append` itself, before returning      |
| `Append` never called (encoder → drop)     | Caller of the encoder, before dropping |
| `TryEnqueue` to live socket (any outcome)  | **Nobody** — buffer still owns         |
| Drain loop send success                    | **Nobody** — buffer still owns         |
| Drain loop send error / connection torn    | **Nobody** — buffer still owns         |
| Retransmit replay                          | Reads from buffer; does not dispose    |
| Buffer overflow (bulk clear)               | Buffer, in `Append` overflow branch    |
| Eviction by acked watermark                | Buffer, in `EvictUpTo`                 |

The single ownership rule eliminates every double-dispose and
use-after-return scenario: the live send path borrows; the
retransmit path borrows; only the buffer disposes, and the
buffer's two disposal points (overflow, evict-up-to) are
mutually exclusive per entry. The contract is enforced by the
type system: `OutboundFrame` is non-`IDisposable` (so devs
can't accidentally `using`-block it mid-pipeline), and `Owner`
is exposed only as `internal get` to the buffer assembly.
Tests assert that `MemoryPool<byte>.Shared` rent count ==
dispose count under sustained load (an integration-level
assertion via a wrapping `MemoryPool<byte>` decorator for
tests — same pattern as the existing `MetricsRegistry` test
doubles).

**Pre-buffer drop path.** The one place where a frame would be
encoded but never reach `Append` is when `Route` decides the
ER doesn't belong to a known bot mapping. The §5.4 pseudocode
handles this correctly by checking the mapping **before**
calling the encoder, so no orphan rent ever occurs.

**Invariants.** Unchanged. The buffer's `GetRange` path
(retransmit) reads into the same memory the original send used;
this is safe because the buffer is the sole owner and only
disposes on `EvictUpTo` or overflow — neither of which can
happen during a retransmit walk that holds the same `_gate`
lock.

### 5.6 F6 — Inbound frame zero-copy (High)

**Today.** `FixpSessionConnection.ExtractFrame`
(`FixpSessionConnection.cs:215`) does
`Payload = frame.Payload.ToArray()` to heap-copy the body so it
can survive across `await`s (the underlying SOFH reader buffer
rotates). One allocation per inbound frame.

**Proposal.**

For each message type the dispatcher handles, decode the SBE
fields synchronously from `frame.Payload` (a `ReadOnlySpan<byte>`)
into a small POCO/struct **before** any `await`. Then pass that
struct downstream — no `byte[]` survives across the await.

```csharp
private async Task<bool> HandleFrameAsync(Stream stream, SofhFrame frame, ...)
{
    switch (frame.TemplateId)
    {
        case NewOrderSingleData.MESSAGE_ID:
        {
            // Synchronous decode into a struct; no allocation.
            var decoded = NewOrderSingleDecoder.Decode(frame.Payload);
            // decoded: struct { ulong ClOrdId, int SecurityId, decimal Px, long Qty, ... }
            return await HandleNewOrderSingleAsync(stream, decoded, ct);
        }
        // ...
    }
}
```

**Decode points that need this** (from
`FixpSessionConnection.HandleFrameAsync` switch):

- `NegotiateData.MESSAGE_ID` — small, infrequent. Lower priority.
- `EstablishData.MESSAGE_ID` — same.
- `TerminateData.MESSAGE_ID` — same.
- `SequenceData.MESSAGE_ID` — **hot** (heartbeat + every batch
  watermark). Must be zero-alloc decode.
- `NewOrderSingleData.MESSAGE_ID` — **hot**. Must be zero-alloc
  decode.
- `OrderCancelRequestData.MESSAGE_ID` — **warm**. Must.
- `RetransmitRequestData.MESSAGE_ID` — infrequent. Lower priority.

For the hot paths, the decoded struct contains only value
types + (optionally) a `ReadOnlyMemory<byte>` slice for fields
copied into a heap buffer **only if needed downstream** (e.g.
the bot's external ClOrdId needs to land in the mapping
registry, which is a managed `string` — that allocation is
unavoidable and its locality is the registry, not this hot
path).

**Invariants.** Unchanged.

### 5.7 F7 — WAL fsync tuning (High)

**Today.** `PersistenceOptions` (lines 37-44):

```csharp
public TimeSpan GroupCommitWindow { get; set; } = TimeSpan.FromMilliseconds(10);
public int GroupCommitMaxRecords { get; set; } = 64;
public bool FsyncOnFlush { get; set; } = true;
```

Plus `SegmentWriter.Flush` (line 91) does `_log.Flush(_fsyncOnFlush)`
**and** `_idx.Flush(_fsyncOnFlush)` unconditionally — even when
no index record was written this batch.

**Proposal.**

1. **Raise `GroupCommitMaxRecords` default to 512.** At
   participant volumes the old 64 was conservative; at 100k
   events/s the writer is filling the batch buffer in <1ms, so
   the cap was hitting before the window. Raising to 512
   amortises fsync over more records without extending p99
   latency past the window cap (`GroupCommitWindow=10ms`).
2. **Skip `_idx.Flush(true)` when no index record was written
   this batch.** `SegmentWriter` already tracks
   `_recordsSinceIndex` and writes index entries every N
   records / M bytes. The flush call should be:

   ```csharp
   public void Flush()
   {
       _log.Flush(_fsyncOnFlush);
       if (_indexDirty) { _idx.Flush(_fsyncOnFlush); _indexDirty = false; }
   }
   ```

   `_indexDirty` is set in `WriteIndexEntry`. Most batches
   write zero index entries (because index is every-64-records
   by default), so this saves one fsync per batch — typically
   ~50% of the WAL syscall cost.
3. **Document the durability/throughput trade-off — accurately.**
   `FileEventStore.Append` returns to its caller as soon as the
   record has been (a) JSON-serialised, (b) seq-assigned, and
   (c) `TryWrite`-ed onto the bounded in-memory channel. The
   actual `_log.Flush(true)` happens later on the writer
   thread. **This means events are acknowledged-applied to
   their originator before they are fsynced to disk** — the
   trade-off the existing design makes for throughput. v0
   does **not** change this contract; it only widens the
   group-commit batch and elides redundant index fsyncs.

   Worst-case crash exposure (records acknowledged but lost
   on a hard kernel crash) is therefore: every record currently
   sitting in the bounded `Channel<PendingRecord>` (capacity
   `ChannelCapacity = 4096`) **plus** every record in the
   writer's in-progress batch buffer (≤ `GroupCommitMaxRecords`,
   raised to 512 by this fix). At steady state the channel
   queue depth is small (events are drained faster than the
   group-commit window), so the practical exposure is dominated
   by the in-progress batch. But the upper bound is
   `ChannelCapacity + GroupCommitMaxRecords`, not
   `GroupCommitMaxRecords` alone.

   Operators who need a stricter "no ack before fsync" guarantee
   need to call `FileEventStore.FlushAsync` before
   acknowledging the originating client — a contract change
   that is **out of scope for v0** (would gate every REST
   submit on a fsync round-trip, killing the throughput target).
   It is called out as a v1 RFC topic ("synchronous-fsync ack
   mode for compliance-sensitive deployments") in §13.

   Operators who want a tighter loss bound today lower
   `GroupCommitMaxRecords` and/or `ChannelCapacity`; operators
   willing to accept a larger loss window for throughput raise
   either knob.

**Invariants.** §4.2's existing contract (ack happens after
`Append` returns + `apply` completes — i.e. after the record
is **enqueued** to the bounded channel, **not** after it is
fsynced) is preserved. v0 does not provide an ack-after-fsync
guarantee; that is called out as a v1 RFC topic in §13. The
fix only changes batch sizing and elides one no-op syscall.
Crash exposure (acknowledged-but-not-fsynced records) remains
`ChannelCapacity + GroupCommitMaxRecords`, which v0 widens by
raising `GroupCommitMaxRecords` from 64 to 512 — a deliberate
trade documented above.

### 5.8 F8 — Snapshot capture allocation (High)

**Today.** `StateSnapshotter.Capture` (line 62-84, called from
`SnapshotService.cs:125` under `WithSnapshotLock`):

```csharp
WorkingOrders = _orders.Snapshot().ToList(),
Positions = _positions.Snapshot().ToList(),
KilledEndClients = _killSwitch.ListKilledEndClients().ToList(),
// ... 13 more .ToList() / .OrderBy() calls
```

Every projection enumerates an underlying dictionary, materialises
a fresh `List<>`, all under the dispatcher lock. With ~50k
working orders + 1k positions + 10 small registries, this is
tens of milliseconds of lock-held work per snapshot interval.

**Proposal.**

Two-phase capture:

1. **Under the dispatcher lock**: each projection returns a
   minimal **array snapshot** of its internal state — a single
   `Array.Copy` from the live storage to a fresh array. No
   `OrderBy`, no `Select`, no projection-shape conversion.
2. **Outside the lock**: build the `PlatformSnapshot` object,
   sort, project to the persisted shape, serialise.

```csharp
// Phase 1, inside lock:
public WorkingOrdersRaw RawSnapshot() // pseudo
{
    var arr = new OrderRecord[_orders.Count];
    _orders.CopyTo(arr); // dictionary CopyTo
    return new WorkingOrdersRaw(arr);
}

// Phase 2, outside lock:
public WorkingOrderSnapshot[] Project(WorkingOrdersRaw raw)
{
    return raw.Records
        .Select(r => new WorkingOrderSnapshot(...))
        .OrderBy(s => s.ClOrdId)
        .ToArray();
}
```

Collections involved (every `.ToList()` / `.OrderBy()` in
`StateSnapshotter.Capture`):

- `_orders.Snapshot()` — working orders (largest)
- `_positions.Snapshot()` — positions (medium)
- `_killSwitch.ListKilledEndClients()`, `.ListKilledFirms()`
- `_symbolHalts.ListHalted()`
- `_sessionPhases.ListOverrides()`
- `_clOrdIds.Snapshot()` — registry watermarks (small, but
  inside a dictionary; copy is fast)
- `_ownership.Snapshot()`
- `_algos.Snapshot()`, `_algoIds.Snapshot()`
- `_cash.Snapshot()`
- `_userBotCredentials?.Snapshot()`, `_userBotSessions?.Snapshot()`,
  `_userBotMappings?.SnapshotOrders()`, `.SnapshotCancels()`

Each underlying registry exposes a new `RawSnapshot()` that
does `CopyTo(array)` over a single concurrent-collection
enumeration — cheap. The projection / sort happens in the
`SnapshotService.TryTakeSnapshot` body, post-`WithSnapshotLock`.

**Invariants.** §4.3 preserved by construction: the lock-held
phase still observes a single point-in-time `seq` and a
consistent set of arrays (no torn reads). Sorting and
projection are pure functions of those arrays, so the resulting
`PlatformSnapshot.Seq = N` still means "state as of seq N".

### 5.9 F9 — TCP NoDelay + buffer sizing (High)

**Today.** `FixpListenerHostedService.cs:131` accepts a TCP
client and immediately hands it to `HandleAcceptedClientAsync`.
Neither here nor in `FixpSessionConnection`'s constructor is
`Socket.NoDelay = true` set. Default Nagle batches small writes
up to 200ms — fatal for a low-latency ER delivery path.

**Proposal.**

```csharp
client = await _listener.AcceptTcpClientAsync(stoppingToken);
client.NoDelay = true;
client.SendBufferSize = _opts.Tcp.SendBufferBytes;       // default 64KiB
client.ReceiveBufferSize = _opts.Tcp.ReceiveBufferBytes; // default 64KiB
_ = Task.Run(() => HandleAcceptedClientAsync(client, stoppingToken), stoppingToken);
```

Add `FixpListenerOptions.Tcp` sub-section:

```csharp
public sealed class FixpTcpOptions
{
    public int SendBufferBytes { get; set; } = 64 * 1024;
    public int ReceiveBufferBytes { get; set; } = 64 * 1024;
    public bool NoDelay { get; set; } = true;
}
```

The buffer sizes are explicit overrides; on Linux defaults are
fine, but on Windows the default 8KiB recv buffer is below the
SOFH frame burst rate at 100k msg/s. Setting it explicitly
makes the platform behave predictably across deployments.

**Invariants.** None affected.

## 6. Cross-cutting

### 6.1 Source-generated JSON for WAL

**Mandatory in v0.** Every `WalEvent` subtype is registered with
a `JsonSerializerContext` partial class:

```csharp
[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(WalEvent))]
[JsonSerializable(typeof(OrderSubmittedEvent))]
// ... one per derived type already on WalEvents.cs
public partial class WalEventJsonContext : JsonSerializerContext { }
```

`FileEventStore` uses `JsonSerializer.SerializeToUtf8Bytes(evt,
WalEventJsonContext.Default.WalEvent)` instead of the reflection-
based call. Polymorphic dispatch is preserved by the existing
`JsonPolymorphic` attribute on `WalEvent`.

**Why mandatory.** The dispatcher lock-narrowing in F1 only pays
off if the serialise step is itself fast. Reflection-based
`JsonSerializer.SerializeToUtf8Bytes` allocates writer state per
call and walks reflection metadata; sourcegen produces a tight
switch. The two changes ship together (one sub-issue) so the
benchmark delta attributes correctly.

Recovery (`ReadFromAsync`) also switches to sourcegen
deserialiser. Round-trip property test (existing
`WalEventsBotMappingTests` + a new one) gates the change.

### 6.2 Allocation discipline — small style guide

Document in the RFC and enforce in code-review:

- **`ArrayPool<byte>` / `MemoryPool<byte>`** is for buffers whose
  ownership transfers cleanly (encoder → buffer → drain → buffer-
  evict; or socket-recv buffer → SOFH framer → discard). The
  rule is: **the type that takes ownership has an explicit
  `Dispose` / `Return` call site, and that call site is on a
  path that runs in every exception case** (i.e. `try/finally`
  or `using` at the receiving end). For F5 the single owner is
  `BotOutboundBuffer`; nobody else disposes (§5.5).
- **`Span<byte>` / `ReadOnlySpan<byte>`** is for synchronous
  decode/encode local to one method body — does not survive an
  `await`. Use for SBE field decode (F6) and SOFH frame
  construction (F5 internals).
- **`ReadOnlyMemory<byte>`** is for buffers passed across
  `await` points, where the buffer is owned by an
  `IMemoryOwner<byte>` further up the stack.
- **Plain `byte[]`** is for the WAL append payload (the
  `FileEventStore` channel writer is the stable owner; the
  payload outlives every other reference). Do not pool here —
  the lifetime spans the channel hop and pooling across
  channel hops is the most common pool-leak pattern.
- **`string` allocation** is unavoidable for `ClOrdId`
  external-side identifiers and reject reasons; do not chase
  these. They are not on the hot path of throughput.

### 6.3 Backpressure policy across the pipeline

One coherent story. For each origin, when downstream is slower
than upstream, pick the documented action:

| Origin                          | Backpressure trigger                          | Action                                                                 |
| ------------------------------- | --------------------------------------------- | ---------------------------------------------------------------------- |
| **REST** `POST /orders`         | `WAL channel full` (bounded `ChannelCapacity`) | HTTP `503` + `Retry-After: 1` header; metric `WalBackpressure{call_site=rest}` |
| **WS** order submit             | Same                                           | Submit promise rejected with `BackpressureError`; client logic retries; metric `WalBackpressure{call_site=ws}` |
| **FIXP** inbound `NewOrderSingle` | Same                                           | `BusinessReject` SBE message with reason `RESOURCE_BUSY`; bot retries; metric `WalBackpressure{call_site=fixp.inbound}` |
| **EntryPoint inbound ER**       | WAL channel full                              | **Apply ER without WAL append** (existing `EntryPointExecutionReportRouter` exception, §4.2); metric `WalBackpressure{call_site=er.router}` |
| **WS hub fan-out sink** (F2)    | Per-sink bounded channel full                 | `DropOldest` + `SubscriptionResetEvent` (existing WS reconnect path)   |
| **`BotErRouter.Route`** (F4)    | Per-credential `BotOutboundBuffer.MaxMessages` cap | Bulk-clear buffer + version-bump signal (existing #173 path). No "drop without per-bot signal" path exists. |
| **Per-FIXP-session outbound channel** (F3) | Channel cap                          | `TryEnqueue` returns false → `Route` calls `_outbound.SignalSessionOverflow(credentialId)` → version-bump |
| **Algo signals sink** (F2)      | Per-sink bounded channel full                 | `DropOldest` + metric `AlgoSignalsDropped` (existing)                  |
| **Snapshot writer** (disk slow) | Snapshot interval slips                       | Skip snapshot, retry next interval, metric `SnapshotsFailed`            |

**Principle:** every drop must produce either (a) a synchronous
error returned to the originator (REST/WS/FIXP inbound) so the
originator's retry logic kicks in, or (b) a per-bot signal (the
version-bump or the WS subscription-reset event) that forces
the affected client to reconnect-and-replay so consistency is
preserved. We never silently lose state mutations; we may
silently lose ER audit logs in the one documented router path.

## 7. Validation strategy

### 7.1 Micro-benchmarks (BenchmarkDotNet)

`BenchmarkDotNet` is **not currently in the repo** (grep
confirms no references in `*.cs` / `*.csproj`). v0 adds it as
a new project `backend/bench/B3.Trading.Benchmarks` referenced
only from `dotnet run -c Release` (no test-runner integration
in CI; benches run on developer hardware + a perf CI job
gated to a labelled branch / dispatch event).

Bench coverage (one `[MemoryDiagnoser]` benchmark class per
fix, plus one composite):

- `EventDispatcher_Dispatch_Bench` — `Dispatch(walEvt, () => {})`
  with a `NullEventStore`; measures pure dispatcher cost.
  Acceptance: F1 + sourcegen drops `AllocatedBytes` by ≥50%
  and `OperationsPerSecond` rises by ≥3×.
- `BotErRouter_RouteOne_Bench` — synthetic `ExecutionEvent`
  through `Route` (post-§5.4) against `M` registered
  credentials, measuring throughput and allocations.
  Acceptance: F5 zero-copy drops allocations by ≥50%; the
  removal of the global multiplexer channel is a wash on
  throughput.
- `OutboundExecutionReportEncoder_Bench` — encode + send to a
  `MemoryStream` (drain stub). Acceptance: F5 pooled buffer
  drops `Gen0` collections by ≥80% under sustained load.
- `WAL_Append_Flush_Bench` — `FileEventStore.Append` + drain
  loop end-to-end against a tmpfs / RAM-disk path (no real
  disk in micro-bench). Acceptance: F1 + F7 raise the
  saturated `Append` rate by ≥4× over baseline.

### 7.2 Integrated load test

A new project `backend/test/B3.Trading.LoadTest` (not in CI by
default; runnable via `dotnet run -- --rate 100000 --bots 50
--duration 60s`) drives:

- Synthetic order submitter posting `OrderSubmissionRequest`s
  at the configured rate via the in-process `ISubmitOrderHandler`
  (skips the HTTP layer to isolate platform throughput from
  Kestrel).
- N bot connections (in-process `B3.EntryPoint.Client` against
  the listener) receiving ERs.
- Measures: sustained order/s accepted, sustained ER/s
  delivered, end-to-end p50/p95/p99 latency from
  `submit-call-start` → `bot-receives-ER`. Histogram emitted
  to console + a CSV for diff against baseline.

This is the **gating measurement** for "did v0 hit the 100k
target". Each fix's sub-issue must include a load-test diff in
the PR description showing the before/after numbers.

### 7.3 Acceptance gates per fix

| Fix | Throughput target (vs. baseline) | Latency budget                          |
| --- | -------------------------------- | --------------------------------------- |
| F1  | ≥3× dispatcher ops/s             | p99 dispatch latency ≤ baseline + 10µs  |
| F2  | ≥2× combined dispatch+publish    | p99 publish ≤ 50µs                      |
| F3  | Outbound send allocations −95%   | p99 enqueue→write ≤ 200µs               |
| F4  | No regression vs. F2             | p99 Route ≤ 5µs                         |
| F5  | Outbound bytes alloc −95%        | n/a (allocation-only fix)               |
| F6  | Inbound bytes alloc −95%         | p99 decode ≤ 1µs                        |
| F7  | WAL ops/s ≥4×                    | p99 group-commit ≤ window cap (10ms)    |
| F8  | Snapshot lock-hold −80%          | p99 lock-hold ≤ 1ms at 50k working orders |
| F9  | n/a (latency-only)               | p99 send→peer-recv ≤ 500µs over LAN     |
| All | **≥100k orders/s sustained, p99 e2e ≤ 5ms** | (composite acceptance)             |

A fix that hits its throughput gate but regresses any other
path's latency by >10% is **not mergeable** without explicit
RFC amendment.

### 7.4 Property-based ordering tests

To enforce §4.1 and §4.3 across the lock-narrowing changes (F1,
F2, F8), a new property test (`FsCheck` or hand-rolled) drives
N concurrent `Dispatch` calls with random small mutations and
asserts:

- WAL `seq` order matches the order in which `apply()`
  callbacks observed each other's effects.
- Snapshot taken at any seq `N` reflects exactly events
  `[1..N]` and zero events `[N+1..]`.
- Subscribers' `Publish` invocations are observed in
  monotonic seq order (post-F2).

This is a regression net for the most fragile invariants.

## 8. Sequencing

Some fixes are prerequisites for others. The dependency graph:

```
                  F1 (dispatcher narrow + sourcegen)
                  /              \
                F2 (ER fan-out)   F7 (fsync tuning)
                /
              F8 (snapshot raw-copy)

              F5 (encoder pool) ──┐
                                  ├──> F3 (per-conn writer loop) ──> F4 (synchronous resolve in Route)
                                  │
                                  └──────────────────────────────────┘

              F6 (inbound zero-copy) — independent, parallelisable

              F9 (NoDelay + bufs)    — independent, trivial
```

**Why this order.**

- **F1 first.** Lock-narrowing is the single largest throughput
  win. Sourcegen JSON is bundled with it because the win only
  materialises when both land; splitting them risks reverting
  the lock change to chase a sourcegen regression.
- **F2 after F1.** F2 changes the `Dispatch` contract to return
  a captured outcome; that's easier to reason about once F1
  has tightened the lock body. Doing F2 before F1 means the
  captured outcome is still computed under a lock that also
  serialises JSON encode — wasted work.
- **F3 before F4.** Per-connection writer loop must exist
  before the synchronous-resolve `Route` is meaningful: F4's
  `sender.TryEnqueue` returns false on per-session channel
  full, and that false-return only exists once F3 has
  introduced the per-session bounded channel. With today's
  `Task.Run`-per-send, "TryEnqueue can fail" is meaningless
  because every send already detaches.
- **F5 before F3.** F3 needs `OutboundFrame` (the pooled-
  buffer wrapper) to exist so the per-session drain loop can
  borrow `frame.Bytes` while ownership/disposal stays
  exclusively with `BotOutboundBuffer`. Doing F3 first means
  churning F3 twice.
- **F4 last in the bot-router spine.** F4 collapses the global
  multiplexer channel, which means the per-sink dispatcher
  drain thread (created by F2) becomes the place that calls
  `Route` directly. F4 cannot land before F2 (no per-sink
  drain thread) or F3 (no per-session channel for `TryEnqueue`
  to write into).
- **F6, F8, F9 are independent.** They can ship in parallel
  with the F1→F2→F3→F4 spine.
- **F7 can ship anytime after F1.** Independent of the
  fan-out work; only depends on F1 because the dispatcher
  benchmark is gated on F1 to attribute the win correctly.

## 9. Sub-issue decomposition

| ID | Title | Depends on | Risk | Effort |
|----|-------|------------|------|--------|
| P1 | Add `BenchmarkDotNet` harness + baseline benches | – | Low | S |
| P2 | Add `WalEventJsonContext` source-gen + property round-trip tests | – | Low | S |
| P3 | F1 — Narrow dispatcher lock; pre-serialise outside; new `Append(evt, payload)` overload | P1, P2 | Med | M |
| P4 | F2 — `Dispatch<T>` outcome-capture overload; per-sink channels written under the dispatcher lock; move ER fan-out out of the dispatch critical section | P3 | Med | M |
| P5 | F7 — Raise `GroupCommitMaxRecords` default; conditional index fsync in `SegmentWriter` | P1 | Low | S |
| P6 | F8 — Two-phase snapshot capture; raw arrays under lock, projection outside | P4 | Med | M |
| P7 | F5 — Pooled `OutboundFrame` wrapper; encoder returns `IMemoryOwner`; buffer is sole owner (single-dispose rule) | P1 | Med | M |
| P8 | F3 — Per-connection outbound channel + drain loop; remove `Task.Run` per send; documented shutdown drain; drain loop never disposes | P7 | High | M |
| P9 | F4 — Synchronous credential resolve in `BotErRouter.Route`; remove global multiplexer channel; per-credential buffer is the sole bounded layer; metrics + alert on buffer overflow rate and per-session enqueue-fail rate | P8 | Med | S |
| P10 | F6 — Zero-copy inbound SBE decode for hot message types (NewOrderSingle, Cancel, Sequence) | – | Low | M |
| P11 | F9 — `Socket.NoDelay = true` + buffer sizing + `FixpTcpOptions` | – | Low | S |
| P12 | Property-based ordering tests for §4.1 / §4.3 | P3, P4, P6 | Low | S |
| P13 | Integrated load test harness (`B3.Trading.LoadTest`) + baseline run | P1 | Low | M |
| P14 | Final composite load-test run; close tracking issue | All above | Low | S |

Effort: S = ≤1 day, M = 2–4 days, L = ≥1 week.

Each row ships as one PR, references this RFC, and posts its
benchmark + load-test diff in the PR body.

## 10. Risks

### 10.1 Lock-scope reduction breaking ordering invariants

The biggest risk in v0. F1 and F2 change the scope of the
single global serialisation point. A subtle mistake — e.g.
publishing under the wrong lock, or capturing the outcome
after the lock released — silently breaks §4.1 and the bug
manifests as a non-reproducible WAL/state divergence months
later.

**Mitigation:**
- Property-based ordering tests (P12) gate every PR that
  touches `EventDispatcher`.
- The recommended F2 implementation enqueues onto per-sink
  bounded channels **while still holding the dispatcher
  lock**, so subscriber drain order matches WAL seq order by
  construction (the channel is FIFO and the writes happened
  under the lock). A second-mutex variant was explicitly
  considered and rejected because OS scheduling can reorder
  the post-lock acquire — the per-sink-channel approach
  does not have that failure mode.
- Code-review of F1+F2 PRs explicitly requires a callout of
  every callsite that calls `Dispatch` to confirm the new
  outcome-capture shape is consistent.

### 10.2 Pooled buffers leaking under exception paths

`IMemoryOwner<byte>` rented from `MemoryPool<byte>.Shared`
that isn't disposed leaks memory until the GC reclaims the
underlying array (which defeats the point of pooling). The
ownership rules in §5.5 are non-trivial and the most
exception-prone paths are: (a) drain loop crashes mid-send,
(b) Establish-replaces-Establish during outbound drain, (c)
encoder rents but mapping lookup never reaches `Append`.

**Mitigation:**
- Single-owner rule (§5.5: buffer owns; everyone else
  borrows; drain loop never disposes; `TryEnqueue` never
  disposes) is enforced by giving `OutboundFrame` no
  `Dispose` method; only `Owner` (which is internal-`get`)
  does.
- §5.4 pseudocode orders the calls so the encoder runs
  **after** the mapping lookup succeeds, eliminating the
  orphan-rent path.
- A test-only `TrackingMemoryPool<byte>` decorator wraps
  `MemoryPool<byte>.Shared` in tests and asserts rent==dispose
  count at end-of-test.
- Load test runs for ≥10 minutes and asserts process RSS is
  flat (within a small drift) — leaks show as RSS drift.

### 10.3 Bounded channels with drop policy losing data

The original v0 plan dropped ERs at the multiplexer's global
channel on overflow; reviewer correctly flagged this as
unrecoverable (the credential isn't resolved at the drop
site, so no version-bump can be emitted). The fix in §5.4 is
to **remove** the global multiplexer channel entirely and
do the credential resolve + per-credential buffer append
synchronously in `Route`. Backpressure then exists only at
the per-credential buffer (where it has a documented version-
bump path) and the per-FIXP-session channel (F3, where the
§5.4 pseudocode explicitly handles its `TryEnqueue` false
return by signalling the version-bump path because the
buffer accepted before the session enqueue). There is no
v0 path that silently loses an ER without a per-bot recovery
signal.

**Mitigation per origin:**
- **REST submit**: never drops; surfaces 503.
- **WS submit**: never drops; surfaces typed error.
- **WS hub fan-out (F2)**: bounded per-sink channel with
  `DropOldest` + `SubscriptionResetEvent`; the WS client's
  reconnect-and-resync path catches the reset.
- **FIXP outbound (F3 + F4)**: per-credential buffer overflow
  → version-bump → bot reconnects with `RetransmitRequest`.
  Per-session channel overflow handled the same way via
  `_outbound.SignalSessionOverflow`. Existing #173 path.
- **EntryPoint inbound ER on WAL backpressure**: applies
  without WAL append (existing documented exception, §4.2).

P9 closes the loop on the F4 redesign and adds metrics for
both the per-buffer overflow rate and the per-session enqueue-
fail rate, plus an alert on either sustained for 30s.

### 10.4 Sourcegen JSON regression under polymorphic deserialise

The sourcegen pipeline for `JsonPolymorphic` types is
relatively new (.NET 8+) and we depend on the discriminator
property staying named `kind`. If sourcegen drops the
polymorphic dispatch, recovery silently rejects every event
as "unknown subtype" and the host won't boot.

**Mitigation:**
- Round-trip test (P2) covers every derived type: serialise
  → deserialise → struct equality. Sourcegen regression fails
  CI on the round-trip.
- Recovery test (existing
  `RecoveryAndSnapshotTests`) ensures end-to-end path is
  exercised.

### 10.5 Bench environment drift

`BenchmarkDotNet` numbers are dev-machine-specific. A bench
delta of "+50%" on one machine may be "+10%" on another. The
acceptance gates in §7.3 are **relative**, but if reviewers
don't run on consistent hardware the relative numbers will
drift session-to-session.

**Mitigation:**
- The dedicated perf CI job runs on a fixed runner spec (one
  shared self-hosted runner, documented in the bench harness
  README). Sub-issue PRs include numbers from that runner,
  not from random dev laptops.
- Each bench prints its `BenchmarkSwitcher` config in the PR
  body so reviewers can spot misconfiguration.

### 10.6 Per-connection drain loop blocking shutdown

The §5.3.2 drain semantics says "block successor session
Establish until previous drain completes or times out". A
hung peer + a long timeout means the credential is
effectively unusable for the timeout duration. Default 1s
keeps that bounded; operators can tune via
`FixpListenerOptions.OutboundDrainTimeout`.

## 11. Open questions

- **`MemoryPool` size class for `OutboundFrame`.** The default
  `MemoryPool<byte>.Shared` rents in power-of-2 size classes,
  which over-allocates for our typical ~80-byte ERs. Should
  v0 ship a custom small-object pool tuned to the SBE message
  size distribution (saves ~50% of pooled memory at the cost
  of one more dependency)? **Default: ship with the shared
  pool; revisit if benches show pool overhead is material.**
- **`GroupCommitMaxRecords` default.** §5.7 proposes 512 as a
  step from today's 64. Should we go further (1024, 2048)?
  Depends on observed batch fill at 100k events/s with the
  10ms window cap; sub-issue P5 picks the final number from
  bench output.
- **Per-credential buffer overflow alert threshold.** Now
  applies to the per-credential buffer overflow rate and the
  per-FIXP-session enqueue-fail rate (post-§5.4 redesign —
  there is no global multiplexer drop counter anymore). "30s
  sustained > 0" is a guess. Probably wants tuning once we
  see real load test variance — sub-issue P9 owns the final
  value.
- **Should F6 cover `OrderCancelRequest` decode in v0 or
  defer to v1?** v0 plan includes it (it's "warm"); if the
  decode work proves non-trivial vs. the throughput gain,
  P10 may scope it down to NewOrderSingle + Sequence only
  and open a follow-up.
- **Is `BenchmarkDotNet` acceptable as a new dependency?**
  It's the standard .NET bench tool, MIT-licensed, no
  runtime cost (separate project). Assuming yes; flag if
  not — alternative is hand-rolled bench harness which is
  a meaningful cost upfront.
- **Snapshot raw-copy for `_orders.Snapshot()` — does the
  underlying registry support `CopyTo`?** If not, P6 needs
  to add it. Quick read of `OrderBook` suggests it stores in
  a `ConcurrentDictionary` which has `ToArray()` but no
  `CopyTo(array)`; either is acceptable, both are O(n) under
  no projection. P6 picks one.
- **Should we land a bounded WAL channel size increase
  alongside F1?** Today `ChannelCapacity = 4096`. At 100k
  events/s and a 10ms commit window the steady-state queue
  depth is ~1000, so 4096 has headroom — but a transient
  fsync stall could fill it. **Default: leave at 4096 for
  v0; document as a knob in P5's RFC notes.**

## 12. Compatibility / migration

- **Wire compatibility**: zero changes to FIXP, REST, or WS
  payloads.
- **WAL on-disk format**: zero changes. The sourcegen JSON
  context produces byte-identical output to the current
  reflection-based serialiser (round-trip test asserts this
  in P2).
- **Snapshot format**: zero changes.
- **Configuration**: new defaults for `GroupCommitMaxRecords`
  (64 → 512) and a new `Trading:EntryPointListener:Tcp`
  section. Both backward-compatible (existing configs remain
  valid; new defaults apply where unset).
- **Recovery from older WAL segments**: unchanged. The
  sourcegen deserialiser handles every existing on-disk
  payload because the JSON shape is unchanged.

## 13. Future RFCs unblocked by v0

- **Synchronous-fsync ack mode (v1).** For deployments that
  cannot tolerate the "ack before fsync" window described in
  §4.2 / §5.7, an opt-in mode that gates `Dispatch` return
  on the next `FlushAsync` completion. Costs throughput; that
  trade-off is the RFC's central question.
- **Sharded multiplexer (v1).** If the bench shows the per-
  sink dispatcher drain thread + synchronous-resolve `Route`
  ceiling, the credential-sharded variant documented in §5.4
  graduates to its own RFC.
- **Binary WAL format (v2).** If sourcegen JSON is still the
  WAL bottleneck after F1 lands, a SBE- or FlatBuffers-based
  binary format becomes the next RFC.
- **Lock-free dispatcher (v2+).** Replacing the dispatcher
  lock with a wait-free single-producer-multi-consumer queue
  is plausible only after the in-memory state structures
  themselves become lock-free, which is a larger undertaking.
- **Multi-pool / multi-firm horizontal scaling.** Today's
  per-process per-firm model assumes one process per firm.
  If a firm needs >1 process worth of throughput, a sharded
  WAL + cross-shard ordering protocol is its own RFC.
