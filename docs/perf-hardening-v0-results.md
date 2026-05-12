# Perf hardening v0 — composite load-test results (P14)

Final composite load-test run for the perf-hardening v0 RFC
([`docs/rfcs/perf-hardening-v0.md`](rfcs/perf-hardening-v0.md)),
post all 13 sub-issues (P1–P13) merged into `main` at
[`6c72880`](https://github.com/pedrosakuma/B3TradingPlatform/commit/6c72880).
Closes the §7.3 acceptance gate review for v0; tracking parent
[#194](https://github.com/pedrosakuma/B3TradingPlatform/issues/194) /
P14 issue [#208](https://github.com/pedrosakuma/B3TradingPlatform/issues/208).

The harness is `backend/bench/B3.Trading.LoadTest` (RFC §7.2,
introduced by P13 in PR #216). Numbers below are **reproducible**
from the commands listed in each section; the harness emits a
machine-readable `--results <file>.json` next to its console summary.
Raw `results.json` files are intentionally **not** committed (see
README "Where to put before/after numbers" — only the summary tables
land in the repo).

## 1. Test environment

| Field           | Value                                                          |
| --------------- | -------------------------------------------------------------- |
| Commit          | `6c72880` (post P12 + P9 merge wave)                           |
| Branch          | `perf/p14-composite-results-208` from `main`                   |
| Host            | WSL2 on Windows, `Linux 6.6.87.2-microsoft-standard-WSL2`      |
| CPU             | AMD EPYC 7763 64-Core (16 vCPUs visible to WSL2)               |
| RAM             | 31 GiB                                                         |
| .NET SDK        | `10.0.201` (runtime `10.0.5`)                                  |
| GC              | `ServerGarbageCollection=true` (matches production host)       |
| WAL directory   | `/dev/shm/b3-loadtest` (tmpfs — required for comparable fsync) |
| Stopwatch       | high-res, ticks-per-second `1_000_000_000`                     |
| Build           | `dotnet build -c Release` clean                                |
| CPU governor    | inherited from host (not pinned — see "Caveats" below)         |

The hardware here is **not** the same machine the P13 baseline in
PR #216 ran on, so absolute throughput is not directly comparable
across the two PRs. Relative gains and the gate-met / gate-not-met
verdict are valid because both runs targeted the same hardware
class (commodity x86-64 + tmpfs WAL). All numbers below were taken
back-to-back on the same machine within the same shell session, so
relative spread within this report is meaningful.

### Caveats

- **Shared dev host.** WSL2 over a non-pinned CPU governor; tail
  latency (p99.9, max) reflects scheduler noise on top of the
  platform's own behaviour. p50 / p95 / p99 are the trustworthy
  signal at the percentiles RFC §7.3 gates on.
- **No real FIXP bot sessions.** Per the harness README, `--bots N`
  is a per-publish counter loop standing in for
  `BotErMultiplexer.Route`'s per-session bookkeeping cost. The
  socket-level F8/F9/F11 wins (per-conn channel, NoDelay, buf
  sizing) are exercised by their own micro-benches in
  `backend/bench/B3.Trading.Benchmarks` and by the FIXP integration
  tests, not by this harness.
- **3 reps per scenario.** Adequate for a relative-gate verdict;
  not a substitute for a long-soak run when one is required (the
  10-minute "RSS flat" / "zero ERs lost" criteria from issue #208
  are covered by the existing FIXP soak test in
  `backend/test/B3.Trading.IntegrationTests`, not by this harness).

## 2. Scenarios + numbers

Each scenario was run **three** times back-to-back with no other
foreground load. Table reports per-run + median + spread (max − min).

### 2.1 Steady-state — `--rate 4000 --concurrency 4 --bots 4`

Models the rate-bounded operating envelope: 4 producers ÷ 4 k msg/s
total = 1 k msg/s/producer, well below the latency knee. This is the
"is the steady-state pipeline boring?" check.

```sh
dotnet run -c Release --project backend/bench/B3.Trading.LoadTest -- \
  --duration 30s --warmup 5s --rate 4000 --concurrency 4 --bots 4 \
  --wal-dir /dev/shm/b3-loadtest --results steady.json
```

| Metric                  | run 1     | run 2     | run 3     | median    | spread   |
| ----------------------- | --------: | --------: | --------: | --------: | -------: |
| submit accepted (msg/s) |     4 000 |     4 000 |     4 000 |     4 000 |        0 |
| ER published (msg/s)    |     4 000 |     4 000 |     4 000 |     4 000 |        0 |
| rejections              |         0 |         0 |         0 |         0 |        0 |
| ER dispatch failures    |         0 |         0 |         0 |         0 |        0 |
| e2e p50                 |   56.4 µs |   95.2 µs |   41.5 µs |   56.4 µs |  53.7 µs |
| e2e p95                 |  160.4 µs |  174.5 µs |  151.5 µs |  160.4 µs |  23.0 µs |
| e2e p99                 |  342.1 µs |  337.8 µs |  290.7 µs |  337.8 µs |  51.4 µs |
| e2e p99.9               |  840.4 µs |  843.1 µs |  929.9 µs |  843.1 µs |  89.5 µs |
| e2e max                 |   50.2 ms |   45.9 ms |   54.5 ms |   50.2 ms |   8.6 ms |

Verdict — **steady-state is well inside the latency budget.** p99 at
4 k msg/s is ~340 µs, ~15× headroom under the 5 ms gate; no
backpressure (zero rejections, zero ER dispatch failures); spread
across reps is a few tens of µs at p99. Max-tail in the tens of ms
is scheduler noise (see "Caveats").

### 2.2 Saturation — `--rate 0 --concurrency 8 --bots 4`

Producers run unbounded; the platform's own backpressure (WAL
channel + per-sink ER channel) sets the ceiling. This is the
"how high can it actually go?" probe.

```sh
dotnet run -c Release --project backend/bench/B3.Trading.LoadTest -- \
  --duration 30s --warmup 5s --rate 0 --concurrency 8 --bots 4 \
  --wal-dir /dev/shm/b3-loadtest --results sat.json
```

| Metric                  | run 1   | run 2   | run 3   | median  | spread  |
| ----------------------- | ------: | ------: | ------: | ------: | ------: |
| submit accepted (msg/s) | 181 717 | 167 541 | 175 212 | 175 212 |  14 176 |
| ER published (msg/s)    | 179 143 | 164 992 | 172 526 | 172 526 |  14 151 |
| rejections (% of attempt) |  1.45 % |  1.52 % |  1.60 % |  1.52 % |  0.15 % |
| ER dispatch failures    |  77 219 |  80 617 |  82 006 |  80 617 |   4 787 |
| e2e p50                 | 120.5 µs |  77.1 µs | 242.4 µs | 120.5 µs | 165.3 µs |
| e2e p95                 |  73.6 ms |  31.5 ms | 189.7 ms |  73.6 ms | 158.2 ms |
| e2e p99                 | 304.5 ms | 182.6 ms | 657.7 ms | 304.5 ms | 475.1 ms |
| e2e p99.9               | 806.7 ms | 457.0 ms | 1 637 ms | 806.7 ms | 1 180 ms |
| e2e max                 |  1.42 s  |  1.35 s  |  1.79 s  |  1.42 s  |  0.45 s  |

Verdict — **sustained saturation throughput median ~175k msg/s**,
1.75× the v0 100k goal and ~1.8× the PR #216 baseline of 96.7k on
its hardware. As expected at saturation, latency is **not** under
budget here — backpressure is engaged and producers are queued
behind the WAL + per-sink channels. This scenario is informative
for the throughput ceiling, not for latency conformance; latency
conformance is the rate-bounded scenarios in §2.1 and §2.3.

### 2.3 Composite probe — `--rate 100000 --concurrency 8 --bots 4`

The harness README documents the formal §7.3 composite-gate command
as `--duration 60s --warmup 5s --rate 100000 --concurrency 8 --bots
50`. On this WSL2 dev box that exact shape (50 bots × 100 k msg/s
≈ 5 M per-publish fan-out ops/s) **does not complete within a 4-minute
runner budget** — the harness wall-clock-stalls in shutdown after the
steady window, and we kill it. Reducing `--bots` to 10 has the same
behaviour; only `--bots 4` finishes cleanly here. The numbers below
are therefore an **exploratory composite probe** at `--bots 4`, not
the strict §7.3 gating measurement; the gating measurement requires
a host that can actually carry the documented shape (see §5).

```sh
dotnet run -c Release --project backend/bench/B3.Trading.LoadTest -- \
  --duration 30s --warmup 5s --rate 100000 --concurrency 8 --bots 4 \
  --wal-dir /dev/shm/b3-loadtest --results composite.json
```

| Metric                  | run 1    | run 2    | run 3    | median   | spread   |
| ----------------------- | -------: | -------: | -------: | -------: | -------: |
| submit driven           | 100 000  | 100 000  | 100 000  | 100 000  |        0 |
| submit accepted (msg/s) |   99 620 |   99 755 |   99 711 |   99 711 |      135 |
| ER published (msg/s)    |   99 318 |   99 544 |   99 478 |   99 478 |      226 |
| rejections (% of attempt) |   0.38 % |  0.24 % |  0.29 % |   0.29 % |   0.14 % |
| ER dispatch failures    |    9 073 |    6 336 |    7 008 |    7 008 |    2 737 |
| e2e p50                 |   9.9 µs |  10.2 µs |   9.2 µs |   9.9 µs |   1.0 µs |
| e2e p95                 |   2.1 ms |   2.6 ms |   5.6 ms |   2.6 ms |   3.5 ms |
| e2e p99                 |  19.1 ms |  60.8 ms | 350.2 ms |  60.8 ms | 331.1 ms |
| e2e p99.9               | 106.0 ms | 239.4 ms | 684.2 ms | 239.4 ms | 578.2 ms |
| e2e max                 |  1.26 s  |  1.11 s  |  1.46 s  |  1.26 s  |  0.35 s  |

Verdict — **driven-rate accepted throughput is 99 711 msg/s median,
narrowly under the strict ≥100 k gate** (the WAL channel rejects on
full at the 100 k driven rate; the harness's own `LoadTestReport`
gate at `LoadTestReport.cs:82` is a strict `>= 100_000` and is
correctly emitting "NOT MET"). **p99 latency ≤5 ms is not met** at
the 100 k driven rate on this hardware: p99 spans 19–350 ms across
the three reps. p50 is ~10 µs across the board (the dispatcher
lock-narrowing and per-sink fan-out from F1+F2 dominate at low fill),
which is why the median is so tight; the tail is dominated by
backpressure-induced queueing as soon as a producer block stretches
beyond a window. Sub-bullet: this is the `--bots 4` shape, **not**
the README's `--bots 50` gating shape — see §5.

### 2.4 Latency-knee sweep (single rep, informational)

To locate the latency-bounded throughput ceiling on this hardware:

| `--rate`      | accepted (msg/s) | rejections | p50      | p95      | p99       | p99.9    |
| ------------: | ---------------: | ---------: | -------: | -------: | --------: | -------: |
|        25 000 |          24 999  |          0 |  14.1 µs | 143.8 µs |  717.9 µs |   2.8 ms |
|        50 000 |          49 991  |        253 |   9.6 µs | 590.8 µs |    2.7 ms |  38.0 ms |
|       100 000 |          99 711  |      8 595 |  10.2 µs |   2.6 ms |   60.8 ms | 239.4 ms |

`--rate 50000` is the highest sustained rate at which p99 stays under
the 5 ms gate on this hardware. Above ~50 k the WAL group-commit
window starts coalescing producers and the p99 tail extends.

## 3. Comparison vs. PR #216 baseline

PR #216 captured the pre-perf-work numbers on its own dev hardware.
Absolute msg/s differs by hardware; the comparison below is intended
to show direction and order of magnitude, not a strict A/B.

| Scenario                       | PR #216 baseline (pre-Wave-1) | This run (post P1–P13) | Direction          |
| ------------------------------ | ----------------------------: | ---------------------: | ------------------ |
| Saturation submit msg/s        |                       96 700 |                175 212 | +81 % (1.81×)      |
| Saturation p99 e2e             |                        702 ms |                 305 ms | −57 % (better)     |
| Saturation p99.9 e2e           |                        1.50 s |                 807 ms | −46 %              |
| Saturation max e2e             |                        1.59 s |                 1.42 s | −11 %              |
| Steady-state p99 @ 4 k msg/s   |                489 µs – 7.8 ms |               338 µs   | tighter, no spread |
| Steady-state rejections @ 4 k  |                            0 |                       0 | unchanged          |

The Wave-1 perf fixes (P3 lock narrowing, P4 per-sink fan-out, P5
WAL group-commit, P6 two-phase snapshot, P7 pooled outbound frame,
P8 per-conn writer channel, P9 sync resolve, P10 zero-copy decode,
P11 NoDelay) collectively raise the saturation ceiling by ~80 % and
cut the saturation p99 in half. Steady-state p99 spread (489 µs to
7.8 ms in the baseline) collapses to a tight ~340 µs.

## 4. RFC §7.3 acceptance-gate verdict

The harness encodes the strict §7.3 composite gate at
`backend/bench/B3.Trading.LoadTest/LoadTestReport.cs:82` as
`SubmitsPerSecond >= 100_000 && p99Ns <= 5_000_000`. Both halves are
evaluated against the **driven, rate-bounded** scenario (i.e. §2.3
above), not against a saturation probe. On this dev hardware:

| Gate (RFC §7.3 row "All")                | Target                    | This run (P14, dev host)                                                                                                            | Verdict                                                                                                  |
| ---------------------------------------- | ------------------------- | ----------------------------------------------------------------------------------------------------------------------------------- | -------------------------------------------------------------------------------------------------------- |
| Sustained accepted throughput            | ≥ 100 000 orders/s         | driven 100 k accepts **99 711** median (–0.29 %); saturation probe shows the platform's capacity ceiling at **175 k**                | **NOT MET** at the strict ≥100 k driven-rate threshold (saturation capacity comfortably above the goal)   |
| End-to-end p99 latency at composite rate | ≤ 5 ms                     | rate-bounded p99 ≤ 5 ms holds **up to ~50 k msg/s** on this hardware; at driven 100 k, p99 = 19–350 ms                                 | **NOT MET** on this hardware at the 100 k composite point.                                                |

Per-fix sub-gates (F1 …F9 rows of §7.3) are signed off by the
individual sub-issue PRs (#211 … #225) using the bench harness
deltas in their respective PR bodies. P14's job is the composite
"All" row only, and on this dev host neither half clears the strict
threshold.

The README's gating shape (`--rate 100000 --concurrency 8 --bots 50
--duration 60s`) does not complete on this 16-vCPU WSL2 host within
a 4-minute runner budget — see §2.3 — so the strict gate as written
cannot be evaluated here at all; the §2.3 numbers are at `--bots 4`
(the fan-out cost the dev host can carry) and are only an
exploratory probe. Whether the gate is met against the README's
documented shape is an open question pending a re-run on a host
that can carry it.

## 5. Sign-off

- **Capacity / saturation: comfortably above the v0 goal.** The
  saturation probe (rate=0, conc=8, bots=4) sustains a median
  175 k accepted msg/s on this dev host — 1.75× the 100 k goal
  and ~1.81× the PR #216 pre-Wave-1 baseline of 96.7 k on its
  own hardware. The platform's raw capacity ceiling is no longer
  the bottleneck.
- **Strict §7.3 composite gate: not signed off here.** The
  driven 100 k probe accepted 99 711 msg/s median (–0.29 % vs
  the strict ≥100 k threshold), and the p99-≤-5 ms half is held
  only up to ~50 k msg/s on this hardware at all (per the §2.4
  knee sweep). The harness's own gate predicate at
  `LoadTestReport.cs:82` correctly emits "NOT MET" for the §2.3
  runs. P14 therefore **cannot** declare the strict composite
  gate signed off from these numbers.
- **What is signed off.** The relative direction is unambiguously
  positive — saturation throughput from 96.7 k → 175 k (+81 %),
  saturation p99 from 702 ms → 305 ms (−57 %), steady-state p99
  spread (489 µs – 7.8 ms in the baseline) collapses to ~340 µs.
  Per-fix sub-gates (F1 … F9) are signed off by the individual
  sub-issue PRs (#211 … #225) against their bench harness deltas.
- **No production code changes** were made in P14; this is a
  pure measurement-and-documentation pass. All ~1 050 backend
  tests still pass on the underlying merge (`6c72880`).

### What to watch in prod

The metric / log / config surface introduced by the v0 fixes
(WS hub fan-out drops, FIXP outbound drain shutdown,
`GroupCommitMaxRecords`, `OutboundDrainShutdownTimeout`) is
documented in [`RUNBOOK.md`](RUNBOOK.md) §1, with concrete
Prometheus + log-derived alert rules in
[`ops/perf-v0-alerts.md`](ops/perf-v0-alerts.md). Operators
deploying a build that includes the perf-hardening v0 wave
(P1–P14) should wire those alerts before promoting to prod.

### Recommended next step before declaring v0 fully signed off

Re-run the full §7.3 composite scenario on a host that can carry
the harness README's documented gating shape end-to-end:
`--duration 60s --warmup 5s --rate 100000 --concurrency 8 --bots 50
--wal-dir /dev/shm/b3-loadtest`, on a quiet bare-metal Linux box
with the CPU governor pinned to `performance` per the harness
README's "Runner spec for comparable numbers". Append the result
to this document under a "§6 production-host re-run" section and
flip the §4 verdict accordingly. Until that happens the v0
composite-gate sign-off remains **provisional**: capacity demonstrated,
strict driven-rate gate pending production-host evaluation.

## Appendix — re-bench with harness v2 (#228)

Bench-harness v2 closes the v1 gaps that PR #214 (P5 fsync) and PR #219 (P8 per-conn writer) flagged. Runner: AMD EPYC 7763, .NET 10.0.5 Server GC, `--job Short` (3 warmup / 3 iterations).

**P5 — `WAL_Append_Flush_Bench`, real-disk row (`/tmp`)**
- `BatchSize=1   /dev/shm` 12.52 ms · `/tmp` 14.42 ms (+15 %)
- `BatchSize=64  /dev/shm` 12.13 ms · `/tmp` 15.86 ms (+31 %)
- The disk-backed row is the one P5 (#199) is allowed to gate against; tmpfs remains the no-fsync ceiling.

**P8 — `BotErRouter_RouteOne_LiveSocket_Bench` (real TCP loopback, `Socket.NoDelay`)**
- `BatchSize=64  Creds=1`  2.40 ms · `Creds=16` 2.09 ms
- `BatchSize=1024 Creds=1` 11.61 ms · `Creds=16` 2.91 ms

ShortRun confidence intervals are wide (BDN warns) — these numbers exist to validate the harness, not to re-open the §4 verdict. Full-Default-job comparisons against `main` belong in per-fix PRs that touch the relevant hot path.
