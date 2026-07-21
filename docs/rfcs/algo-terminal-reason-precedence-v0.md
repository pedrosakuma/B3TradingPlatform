# RFC: Algo terminal-reason precedence when independent signals race for the same parent

| Field    | Value                                                                |
| -------- | --------------------------------------------------------------------|
| Status   | Proposed                                                             |
| Tracking | [#674](https://github.com/pedrosakuma/B3TradingPlatform/issues/674)  |
| Related  | #347 (known CI flake predecessor), #673 (RetryFact stopgap)          |
| Replaces | n/a                                                                  |

## 1. Context

`AlgoEngine` is an explicitly single-consumer `BackgroundService` (RFC
algo-orders-v0 §4.3: "one IHostedService, bounded Channel, single consumer in
v0, per-parent serialisation via SemaphoreSlim"). Because there is exactly one
consumer thread, reactor invocations for `AlgoSignal`s are already serialised —
there is no data race in the traditional sense (no lock is needed, none is
taken beyond the implicit ones in `ConcurrentDictionary`).

`RecordTerminalAsync` (`AlgoEngine.cs:2131`, exact line numbers may drift)
guards every terminal transition with a first-writer-wins check:

```csharp
private async Task RecordTerminalAsync(Algo algo, AlgoParentRuntime rt, AlgoStatus status, AlgoTerminalReason reason)
{
    if (algo.IsTerminal) return;
    ...
}
```

Multiple **independent producers** can each enqueue an `AlgoSignal` for the
*same* parent algo close together in time:

- The ER pipeline (`ExecutionReportProcessor`) — e.g. a `Rejected` child ER
  with `RiskRejected` reason (`AlgoEngine.cs:1123`, `OrderStatus.Rejected`
  case).
- The next-slice submission attempt (scheduler tick / repeg cycle) — can
  independently enqueue a signal that, once dequeued, hits `SubmitAsync`'s
  failure paths: `GatewayUnavailable` on a submit exception
  (`AlgoEngine.cs:1731`), or `ReconciliationRequired` on `GatewayFailed`/
  `WalBackpressure` (`AlgoEngine.cs:1818-1832`).
- Window-expiry checks (TWAP/VWAP/POV) that pre-empt several of the above
  branches when they fire first (`AlgoEngine.cs:1077-1093`,
  `1108-1121`) — these are already given explicit precedence over
  `VenueCancelled`/`RiskRejected` by existing `if/else if` ordering, so
  window-expiry precedence is **already decided**; this RFC is scoped to the
  remaining ambiguity below.

Because `RecordTerminalAsync` is first-writer-wins, **whichever signal is
dequeued and processed first** determines the final terminal reason — not
"whichever code path wins a race" in the concurrency sense, but whichever
producer's signal happens to reach the front of the queue first. Under light
load the ER-driven signal for a designed test/production scenario is reliably
processed first; under CI CPU contention (or, in principle, real gateway
degradation coinciding with an ER arrival), a scheduler-driven signal can be
dequeued first instead, non-deterministically overwriting what an operator
would expect to see as the terminal reason.

### 1.1 Observed symptom

`PeggedAlgoEndpointTests.Pegged_DroppedNormalRejectedSignal_SuspendsRiskRejected`
(and `Modify_QuantityBelowFilled_Rejected`) intermittently assert the wrong
terminal reason under CI parallelism — 3 distinct wrong values observed across
reruns of the same PR: `ServiceUnavailable` (HTTP-level), `GatewayUnavailable`,
`ReconciliationRequired`, vs. the test's intended `RiskRejected`. `[RetryFact]`
(#673) reduces the frequency but cannot resolve the ambiguity — a retried
attempt can lose the same race again, as observed in CI.

## 2. Trade-offs

| Option                                                                 | Correctness signal fidelity | Implementation cost | Risk |
| ----------------------------------------------------------------------| -----------------------------| ---------------------| ------|
| A. Do nothing (status quo — first-dequeued-wins)                      | Non-deterministic under load  | None                 | Operators occasionally see a misleading terminal reason (e.g. "gateway unavailable" when the real cause was a risk rejection) |
| B. Priority-ordered per-parent signal queue (ER-driven reasons rank above scheduler-driven ones) | Deterministic, ER always wins | Medium — needs a priority queue or two-lane buffering per parent | Reordering signals could delay scheduler-driven submission attempts behind a burst of ER signals; needs care to avoid starving slice submission |
| C. `RecordTerminalAsync` takes an explicit reason-priority table instead of first-writer-wins (last-highest-priority-wins within the same processing tick, or a short grace-buffer that lets a slightly-later ER supersede an already-recorded gateway-side reason) | Deterministic, tunable | Medium-high — changes the "terminal is final" invariant into "terminal is final unless a higher-priority reason arrives within a fence window", larger surface for regressions | Highest — this is the kind of change most likely to introduce a *new* class of race if the fence window is chosen wrong |
| D. Keep first-writer-wins, but have the slice-submission path check for a **pending** ER signal for the same parent already queued and defer/skip its own terminal write if one exists | Deterministic when a same-tick ER signal exists | Low-medium — a queue-peek/defer check localized to the submission failure branches | Requires the queue to support a cheap "does a signal for parent X already exist" check; if the queue doesn't support this cheaply, cost rises |

## 3. Options

### Option B — priority lanes

Split `AlgoSignalQueue` into two logical lanes per parent (or a priority field
on `AlgoSignal`): ER-driven signals (explicit venue/risk outcomes) get
processed ahead of scheduler-driven signals (slice submission attempts,
repeg cycles) for the *same* parent, when both are pending. This directly
encodes the intuition "an explicit rejection is more informative than an
incidental delivery hiccup" without touching `RecordTerminalAsync`'s
first-writer-wins semantics.

### Option D — defer-if-pending-ER

Cheaper, more surgical: before the slice-submission failure branches call
`RecordTerminalAsync` with `GatewayUnavailable`/`ReconciliationRequired`,
peek whether an ER-driven signal for the same parent is already enqueued
(or arrives within a short bounded wait, e.g. one consumer-loop iteration)
and if so, let that one process first. Smaller blast radius than Option B,
but only closes the specific window this RFC was filed for — does not
generalize to future new signal sources without another look.

## 4. Recommendation

Prefer **Option D** as the minimal fix that resolves the specific symptom
(#674) with the smallest change to `AlgoEngine`'s existing single-consumer
model, deferring Option B (general priority lanes) unless a second,
different producer pair is found to exhibit the same ambiguity — over-
engineering a general priority system for a single observed collision isn't
justified yet.

## 5. Decision (pending sign-off)

Not yet decided — awaiting review before implementation. Do not start coding
against this RFC until Status flips to Accepted and the tracking issue (#674)
is updated to reflect the chosen option.

## 6. Out of scope

- Window-expiry precedence (TWAP/VWAP/POV vs. VenueCancelled/RiskRejected) —
  already decided by existing code ordering, not touched by this RFC.
- General overhaul of `AlgoSignalQueue`'s delivery guarantees — this RFC only
  addresses terminal-*reason* precedence for the same parent, not signal
  ordering across different parents or algo types.
- The CI-resource-contention amplification itself (runner sizing, assembly
  parallelism) — tracked separately if pursued; this RFC treats the ambiguity
  as real regardless of environment, since it can in principle occur in
  production too (see §1).
