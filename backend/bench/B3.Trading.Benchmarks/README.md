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

| Class                                  | Hot path                                                       | Acceptance gate (RFC §7.3) |
| -------------------------------------- | -------------------------------------------------------------- | -------------------------- |
| `EventDispatcher_Dispatch_Bench`       | `EventDispatcher.Dispatch` against `NullEventStore`            | F1 — ≥3× ops/s, alloc −50% |
| `WAL_Append_Flush_Bench`               | `FileEventStore.Append` + group-commit `FlushAsync` (tmpfs)    | F7 — Append rate ≥4×       |
| `OutboundExecutionReportEncoder_Bench` | `OutboundExecutionReportEncoder.Encode` per `ExecKind`         | F5 — Gen0 −80%             |
| `BotErRouter_RouteOne_Bench`           | `BotErMultiplexer.Route` end-to-end (drain + encode + send)    | F4/F5 — alloc −95%, no throughput regression |

`OutboundExecutionReportEncoder.Encode` is `internal`; this project is
granted access via `InternalsVisibleTo` on
`backend/src/B3.Trading.EntryPointListener/B3.Trading.EntryPointListener.csproj`.

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
- **WAL bench:** uses `/dev/shm` when present (Linux). On platforms
  without a RAM-disk it falls back to `Path.GetTempPath()`; numbers from
  those runs are NOT comparable with tmpfs runs and must be flagged.
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
