# B3.Trading.LoadTest

Macro / load-test harness for the perf-hardening v0 RFC
(`docs/rfcs/perf-hardening-v0.md` §7.2, tracking #194, issue #207). The
companion to `B3.Trading.Benchmarks` (PR #213) — that project is the
**micro** harness (BenchmarkDotNet), this one is the **macro** harness
that drives the full submit → WAL durable → bot ER receive pipeline at
a sustained rate.

The harness is **on-demand only**. It is wired into `B3TradingPlatform.slnx`
so `dotnet build` compiles it, but `<IsPackable>false</IsPackable>` keeps
it out of pack/publish, and CI's `dotnet test` skips it because it is not
a test project. There is no perf job in `.github/workflows/`; runs happen
on developer hardware (or in a future labelled-dispatch job).

## What it measures

Per RFC §7.2, the harness drives the in-process platform pipeline at a
configurable rate × concurrency × duration and captures:

| Metric | Source |
| --- | --- |
| **Submit throughput (msg/s)** | accepted submits ÷ steady-state elapsed |
| **ER throughput (msg/s)** | observed publishes ÷ steady-state elapsed |
| **End-to-end latency p50 / p95 / p99 / p99.9 / max** | `Stopwatch.GetTimestamp` deltas from submit-call-start to publish-observed, recorded per accepted order |
| **WAL backpressure** | submit rejections + ER dispatch failures |

End-to-end latency covers exactly the path RFC §7.3 gates on:
`OrderSubmissionService.SubmitAsync` → `EventDispatcher.Dispatch`
(WAL append + apply) → `IExchangeGateway.SubmitAsync` → loopback ER →
`EventDispatcher.Dispatch` (WAL append + apply) →
`ExecutionReportProcessor.Apply` → `IExecutionEventSink.Publish`.

The HTTP layer is deliberately **out** of the timing path — RFC §7.2's
charter is "isolate platform throughput from Kestrel". Sub-issues that
specifically need to characterise the API surface should add an
`--http` mode in a follow-up.

## Architecture

```
┌────────────┐  rate-limited  ┌────────────────────────┐
│  N producer │───────────────▶│ OrderSubmissionService │──┐
│  Tasks      │                └──────────┬─────────────┘  │
└────────────┘                            ▼                │
                                ┌─────────────────┐        │
                                │ EventDispatcher │        │
                                │ (WAL append +   │        │
                                │  apply)         │        │
                                └────────┬────────┘        │
                                         ▼                 │
                              ┌──────────────────────┐     │
                              │ LoopbackFillGateway  │     │ t0 captured
                              │ (Task.Run schedules  │     │ here
                              │  Filled ER)          │     │
                              └──────────┬───────────┘     │
                                         ▼                 │
                                ┌─────────────────┐        │
                                │ EventDispatcher │        │
                                │ (WAL ER append) │        │
                                └────────┬────────┘        │
                                         ▼                 │
                            ┌─────────────────────────┐    │
                            │ ExecutionReportProcessor│    │
                            └────────────┬────────────┘    │
                                         ▼                 │
                              ┌──────────────────────┐     │
                              │ LatencyCapturingSink │  t1 captured
                              │ (× --bots fan-out)   │  here
                              └──────────────────────┘
```

`LoopbackFillGateway` substitutes the real `IExchangeGateway` so we
don't depend on an EntryPoint matching simulator. The Filled ER is
dispatched on a thread-pool task — same shape as the production
gateway, where ERs arrive on the EntryPoint client's reader thread —
so the producer's submit call returns before its matching ER fires
through `Publish`.

`LatencyCapturingSink` records the publish-observed tick in a flat
pre-sized `Sample[]` keyed by ClOrdId counter. The producer separately
records `t0` (submit-call-start tick) into the same slot. Whichever
side observes both sides populated atomically claims the slot and
appends `(t1 − t0)` into a flat `long[]` results buffer that is sorted
once at end-of-run for percentiles. This pre-sizing is the only reason
the harness can run at 100k+ msg/s without polluting the SUT's GC
profile with sample bookkeeping.

`--bots N` drives an additional per-Publish counter loop of length `N`
to represent `BotErMultiplexer.Route`'s per-session bookkeeping cost.
The harness does **not** spin up real FIXP bot sessions — that was an
RFC §7.2 stretch goal that the §7.3 gates do not depend on.

## Running

```sh
# 30s smoke at moderate rate — sanity check.
dotnet run -c Release --project backend/bench/B3.Trading.LoadTest -- \
    --duration 30s --warmup 2s --rate 20000 --concurrency 4 --bots 1

# Sustained baseline against the 100k target. Run on quiet hardware,
# performance governor pinned. Capture results.json for the gating PRs.
dotnet run -c Release --project backend/bench/B3.Trading.LoadTest -- \
    --duration 60s --warmup 5s --rate 100000 --concurrency 8 --bots 50 \
    --results baseline.json

# Unbounded — drive the producers as fast as the platform will accept.
# Useful for finding the saturation point; not directly comparable
# across machines.
dotnet run -c Release --project backend/bench/B3.Trading.LoadTest -- \
    --duration 30s --rate 0 --concurrency 16
```

`--help` lists the full flag set.

## Runner spec for comparable numbers

Same conventions as the bench harness (`backend/bench/B3.Trading.Benchmarks/README.md`):

- **Build:** `dotnet build -c Release` against the load-test project.
- **Process isolation:** close other CPU-heavy work; pin the CPU governor
  to `performance` on Linux (`cpupower frequency-set -g performance`).
- **WAL directory:** the harness defaults to a unique per-run temp dir
  it deletes on exit. For comparable WAL fsync numbers, set
  `--wal-dir /dev/shm/b3-loadtest` on Linux so the WAL lives on tmpfs;
  numbers from spinning disk / EBS are NOT comparable with tmpfs runs
  and must be flagged in the PR body.
- **GC mode:** `<ServerGarbageCollection>true</ServerGarbageCollection>`
  matches the production host config.

## Where to put before/after numbers in PR bodies

Each P3-P11 PR that targets a gate in RFC §7.3 must include the
`results.json` deltas in its PR body. Suggested template:

```text
Bench: B3.Trading.LoadTest, --duration 60s --warmup 5s --rate 100000
       --concurrency 8 --bots 50

| Metric                | main (baseline) | this PR |
| --------------------- | --------------- | ------- |
| accepted msg/s        |          XX,XXX |  XX,XXX |
| ER msg/s              |          XX,XXX |  XX,XXX |
| e2e p50               |        XXX µs   |  XXX µs |
| e2e p99               |        XXX µs   |  XXX µs |
| e2e p99.9             |        XXX ms   |  XXX ms |
| WAL backpressure %    |          X.XX % |  X.XX % |
```

Acceptance gates per fix are listed in RFC §7.3 — a PR that hits its
throughput target but regresses any other path's latency by >10% is not
mergeable without an RFC amendment. The composite gate
(≥100k msg/s sustained, p99 e2e ≤5ms) is the cumulative target P3–P11
together must hit; no individual sub-issue is expected to clear it
alone.

The harness prints "RFC §7.3 composite gate : MET / NOT MET" at the
bottom of each run as a quick visual check; the underlying numbers in
`results.json` are the source of truth.
