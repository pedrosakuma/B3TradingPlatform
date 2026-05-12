# B3.Trading.Benchmarks

`BenchmarkDotNet` harness for the perf-hardening v0 RFC
(`docs/rfcs/perf-hardening-v0.md` §7.1, tracking #194). Each benchmark
class targets one of the hot paths the RFC's per-finding fixes (P3-P11
in #194) will touch.

The harness is **on-demand only**. It is wired into `B3TradingPlatform.slnx`
so `dotnet build` compiles it, but `<IsPackable>false</IsPackable>` keeps
it out of pack/publish, and CI's `dotnet test` skips it because it is not
a test project. There is no perf job in `.github/workflows/`; benches
run on developer hardware (or in a future labelled-dispatch job).

## Registered benchmarks

| Class                                       | Hot path                                                                  | Optimisation gated                | Issues / PRs |
| ------------------------------------------- | ------------------------------------------------------------------------- | --------------------------------- | ------------ |
| `EventDispatcher_Dispatch_Bench`            | `EventDispatcher.Dispatch` against `NullEventStore`                       | F1 — ≥3× ops/s, alloc −50%        | #194, PR #213 |
| `WAL_Append_Flush_Bench`                    | `FileEventStore.Append` + group-commit `FlushAsync`, parametrised by data root (tmpfs **and** real-disk; harness v2) | F7 Append rate ≥4×; **P5 fsync tuning** | #199, PR #214, #228 |
| `OutboundExecutionReportEncoder_Bench`      | `OutboundExecutionReportEncoder.Encode` per `ExecKind`                    | F5 — Gen0 −80%                    | #194, PR #213 |
| `BotErRouter_RouteOne_Bench`                | `BotErMultiplexer.Route` end-to-end with in-process `CountingSender` stub | F4/F5 — alloc −95%, no throughput regression | #194, PR #213, PR #218 |
| `BotErRouter_RouteOne_LiveSocket_Bench`     | Same as above, but every credential gets a real TCP loopback connection + `FixpOutboundChannelWriter` drain loop with `Socket.NoDelay` (harness v2) | **P8 per-conn writer Task.Run removal** (#202, PR #219) and **P11 Socket.NoDelay** | #228 |

`OutboundExecutionReportEncoder.Encode` and `FixpOutboundChannelWriter`
are `internal`; this project is granted access via
`InternalsVisibleTo` on
`backend/src/B3.Trading.EntryPointListener/B3.Trading.EntryPointListener.csproj`
and `backend/src/B3.Trading.Application/B3.Trading.Application.csproj`.

### Harness v2 (#228) — what changed

PRs #214 (P5, fsync tuning) and #219 (P8, per-conn writer
`Task.Run` removal) hit the same wall: the v1 harness measured the
optimisation on a path that bypassed the change.

- `WAL_Append_Flush_Bench` ran exclusively against `/dev/shm`,
  where `fsync` is effectively a no-op, so #199's tuning came out
  inside the noise floor.
- `BotErRouter_RouteOne_Bench` used an in-process `CountingSender`,
  so #202's removal of the per-message `Task.Run` and #211's
  `Socket.NoDelay` flag were never actually exercised.

Harness v2 fixes both:

1. `WAL_Append_Flush_Bench` now has a `[ParamsSource]` over data
   roots. Defaults are `/dev/shm` (no-fsync ceiling) **and**
   `/tmp` (real fsync). `/dev/shm` rows are explicitly the
   no-fsync ceiling and are NOT comparable with disk-backed rows.
2. `BotErRouter_RouteOne_LiveSocket_Bench` is the new live-socket
   variant (one ephemeral TCP loopback connection per credential,
   real `FixpOutboundChannelWriter` drain loop, `Socket.NoDelay`
   set on both ends). Iterations count frames the drain callback
   has actually written to the wire, not enqueue success.

### Overriding the WAL data-root matrix

Operators / CI can extend the matrix without code change:

```sh
# Add a real SSD path alongside the defaults
B3_BENCH_WAL_PATHS=/dev/shm,/tmp,/var/lib/b3 \
  dotnet run -c Release --project backend/bench/B3.Trading.Benchmarks -- \
  --filter '*WAL_Append_Flush_Bench*'
```

Comma-separated, trimmed, evaluated at process start. Paths whose
parent directory does not exist are silently skipped so the bench
class stays portable (Windows CI degrades to
`Path.GetTempPath()`).

### Known limitations

- The live-socket bench is **single-machine loopback** — kernel TCP
  fast path, no NIC / wire latency. Cross-host behaviour (jumbo
  frames, MTU, real switch latency) is out of scope here and
  belongs to the load-test harness (#207) and the §7.3 composite
  scenario in `docs/perf-hardening-v0-results.md`.
- Bench harness is excluded from CI: `IsPackable=false`, no test
  runner integration, no `.github/workflows/` job. It runs on
  developer hardware (or a future labelled-dispatch job).
- `/dev/shm` rows of the WAL bench are the no-fsync ceiling; do
  not compare them with disk-backed rows when sizing fsync
  budgets.

## Running

```sh
# List everything
dotnet run -c Release --project backend/bench/B3.Trading.Benchmarks -- --list flat

# Run one bench
dotnet run -c Release --project backend/bench/B3.Trading.Benchmarks -- \
  --filter '*DispatcherBench*'

# Smoke run (Dry job — single iteration, ~seconds, NOT for measurement)
dotnet run -c Release --project backend/bench/B3.Trading.Benchmarks -- \
  --filter '*' --job Dry

# Full default job (Release, all benches, ~minutes)
dotnet run -c Release --project backend/bench/B3.Trading.Benchmarks -- \
  --filter '*' --memory
```

## Runner spec for comparable numbers

Sub-issue PRs (P3-P11 in #194) record before/after numbers in their PR
bodies. To keep numbers comparable across PRs, run on the same machine
with these settings:

- **Build:** `dotnet build -c Release` against the bench project.
- **Process isolation:** close other CPU-heavy work; pin to performance
  governor on Linux (`cpupower frequency-set -g performance`).
- **WAL bench:** the data-root matrix is parametrised. Defaults
  to `/dev/shm` + `/tmp`; override with
  `B3_BENCH_WAL_PATHS=...` (see "Overriding the WAL data-root
  matrix" above). Compare like with like — `/dev/shm` rows are
  the no-fsync ceiling and must not be benchmarked against
  disk-backed rows.
- **Process count:** the default `Job` already runs benchmarks in a
  child process per benchmark for isolation — leave it alone unless you
  have a specific reason.
- **GC mode:** `<ServerGarbageCollection>true</ServerGarbageCollection>`
  matches the production host config.

## Where to put before/after numbers in PR bodies

Each P3-P11 PR body must contain a "Bench numbers" section with the
relevant `BenchmarkDotNet` summary table copy-pasted (or as a fenced
markdown table) for both `main` baseline and the PR's branch — at
minimum: `Mean`, `Allocated`, `Gen0`, `Gen1`, and any throughput column
the per-fix bench class exposes. Sample shape:

```text
Bench: EventDispatcher_Dispatch_Bench

| Method   | Mean     | Allocated | Gen0  |
| -------- | -------- | --------- | ----- |
| baseline |  XX.X ns |    YY B   | 0.00X |
| this PR  |  XX.X ns |    YY B   | 0.00X |
```

Acceptance gates per fix are listed in RFC §7.3 — a PR that hits its
throughput target but regresses any other path's latency by >10% is not
mergeable without an RFC amendment.
