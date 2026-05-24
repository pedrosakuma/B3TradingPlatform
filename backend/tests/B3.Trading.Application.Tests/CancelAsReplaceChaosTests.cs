using System;
using System.Collections.Generic;
using System.Linq;
using B3.Trading.Application.Persistence;
using B3.Trading.Application.Risk;
using B3.Trading.Application.UserBots;
using B3.Trading.Domain;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

#pragma warning disable CS0618 // legacy Margin.Initial used to seed capacity in tests

namespace B3.Trading.Application.Tests;

/// <summary>
/// Issue #430 — defensive chaos sweep over every documented venue ER ordering
/// in the Cancel-as-Replace family (B3MatchingPlatform priority-lost path).
///
/// Background: issue #241 fixed the original silent-fill-loss bug (PR #242),
/// and #247/#248 closed the margin reservation leak. Targeted unit tests exist
/// for each scenario in <see cref="OrderModifyMarginAndProcessorTests"/> and
/// <c>HistoryEndpointTests</c>. This file complements them with a 1000-iteration
/// deterministic sweep that picks one of the six documented scenario shapes at
/// random per iteration (seeded by iteration index for reproducibility) and
/// asserts the same correctness invariants after each run.
///
/// The intent is a regression net: any future refactor of
/// <see cref="ExecutionReportProcessor.Apply"/>, the
/// <see cref="PendingReplacementRegistry"/>, or the
/// <see cref="ReserveOnSubmitMarginProvider"/> coordinator path that breaks
/// one of the documented orderings will trip an invariant with a reproducible
/// seed.
///
/// Scope (and intentional exclusions) are documented in
/// <c>docs/audits/cancel-as-replace-audit.md</c>.
/// </summary>
public class CancelAsReplaceChaosTests
{
    private const int Iterations = 1000;

    [Fact]
    public void Chaos_AllDocumentedScenarios_InvariantsHoldAcrossThousandIterations()
    {
        var failures = new List<string>();
        var scenarioCounts = new int[Enum.GetValues<Scenario>().Length];

        for (var i = 0; i < Iterations; i++)
        {
            var seed = i;
            var rng = new Random(seed);
            var scenario = (Scenario)rng.Next(scenarioCounts.Length);
            scenarioCounts[(int)scenario]++;

            try
            {
                RunScenario(scenario, rng);
            }
            catch (Exception ex)
            {
                failures.Add($"iter={i} seed={seed} scenario={scenario}: {ex.Message}");
                if (failures.Count >= 10)
                {
                    break;
                }
            }
        }

        // Cover every scenario at least once across 1000 iterations.
        for (var s = 0; s < scenarioCounts.Length; s++)
        {
            Assert.True(scenarioCounts[s] > 0,
                $"scenario {(Scenario)s} never exercised; rebalance RNG distribution.");
        }

        Assert.True(failures.Count == 0,
            $"{failures.Count} chaos iterations failed:\n  " + string.Join("\n  ", failures));
    }

    private enum Scenario
    {
        /// <summary>Cancel(new,orig) → Fill(new,0) — happy priority-lost flow.</summary>
        PriorityLostHappy,
        /// <summary>Cancel(new,orig) → Fill(new,0) → Cancel(new,orig) replay (FIXP retransmit).</summary>
        PriorityLostHappy_WithCancelReplay,
        /// <summary>Cancel(new,orig) → real-Cancel(new) — no fill.</summary>
        PriorityLostHappy_FollowedByRealCancel,
        /// <summary>Reject(new) — venue refused the replace; original keeps Working.</summary>
        ReplaceRejectedByVenue,
        /// <summary>Replaced(new,orig) — priority-kept variant.</summary>
        PriorityKept,
        /// <summary>PartialFill on original BEFORE Cancel-as-Replace lands, then Fill on new.</summary>
        OriginalPartiallyFilled_ThenCancelAsReplace,
    }

    private static readonly EndClientId Bob = new("bob");
    private const string Firm = "FIRM";
    private const string Symbol = "PETR4";
    private const ulong SecId = 4321UL;

    private void RunScenario(Scenario scenario, Random rng)
    {
        // Slight randomisation of qty/price to stress decimal/long maths
        // without breaking the scenario shape.
        var qty = 100 + rng.Next(0, 5) * 10;            // 100..140 step 10
        var origPx = 32.40m + rng.Next(0, 10) * 0.01m;  // 32.40..32.49
        var newPx = origPx + 0.01m;                     // crosses up

        var harness = BuildHarness();
        var origId = 777UL;
        var newId = 778UL;

        // Original sits Working.
        var orig = new Order(origId, Bob, Symbol, SecId, OrderSide.Buy, OrderType.Limit, qty, origPx, Firm);
        harness.Book.TryAdd(orig);
        orig.MarkWorking();
        harness.Ownership.Register(origId, Bob);

        // Pre-replace partial-fill scenario.
        long preFill = 0;
        if (scenario == Scenario.OriginalPartiallyFilled_ThenCancelAsReplace)
        {
            preFill = rng.Next(1, (int)(qty / 2)); // never full
            harness.Proc.Apply(origId, ExecKind.PartialFill,
                leaves: qty - preFill, cumQty: preFill, lastQty: preFill, lastPx: origPx,
                rejectReason: null, origClOrdId: 0);
            // Sanity: position credited for the partial.
            Assert.Equal(preFill, harness.Positions.GetOrCreate(Firm, Bob, Symbol).NetQuantity);
        }

        // Modify: register link + intent.
        harness.Ownership.RegisterReplaceLink(origId, newId);
        var intent = new OrderReplacementIntent(
            OriginalClOrdId: origId,
            NewClOrdId: newId,
            Owner: Bob,
            Symbol: Symbol,
            SecurityId: SecId,
            Side: OrderSide.Buy,
            Type: OrderType.Limit,
            NewQuantity: qty,
            NewPrice: newPx,
            FirmId: Firm,
            ParentAlgoId: null,
            AlgoSliceSeq: null);
        Assert.True(harness.Registry.TryAdd(intent));

        // Dispatch the venue ER sequence for the chosen scenario.
        switch (scenario)
        {
            case Scenario.PriorityLostHappy:
                harness.Proc.Apply(newId, ExecKind.Canceled, 0, 0, 0, 0m, null, origClOrdId: origId);
                harness.Proc.Apply(newId, ExecKind.Fill, 0, qty, qty, newPx, null, origClOrdId: 0);
                break;

            case Scenario.PriorityLostHappy_WithCancelReplay:
                harness.Proc.Apply(newId, ExecKind.Canceled, 0, 0, 0, 0m, null, origClOrdId: origId);
                harness.Proc.Apply(newId, ExecKind.Fill, 0, qty, qty, newPx, null, origClOrdId: 0);
                // Replay of the Canceled ER (FIXP retransmit). Order is already
                // Filled (terminal) so MarkCancelled is a no-op; no margin churn.
                harness.Proc.Apply(newId, ExecKind.Canceled, 0, qty, 0, 0m, null, origClOrdId: origId);
                break;

            case Scenario.PriorityLostHappy_FollowedByRealCancel:
                harness.Proc.Apply(newId, ExecKind.Canceled, 0, 0, 0, 0m, null, origClOrdId: origId);
                // After the intent is consumed, a real subsequent Cancel of the
                // new (Working) order is a normal cancel: registry returns
                // false, falls through to the standard cancel branch.
                harness.Proc.Apply(newId, ExecKind.Canceled, 0, 0, 0, 0m, null, origClOrdId: origId);
                break;

            case Scenario.ReplaceRejectedByVenue:
                harness.Proc.Apply(newId, ExecKind.Rejected, 0, 0, 0, 0m,
                    rejectReason: "VENUE_REJECT_TEST", origClOrdId: origId);
                break;

            case Scenario.PriorityKept:
                harness.Proc.Apply(newId, ExecKind.Replaced, qty, 0, 0, 0m, null, origClOrdId: origId);
                harness.Proc.Apply(newId, ExecKind.Fill, 0, qty, qty, newPx, null, origClOrdId: 0);
                break;

            case Scenario.OriginalPartiallyFilled_ThenCancelAsReplace:
                harness.Proc.Apply(newId, ExecKind.Canceled, 0, 0, 0, 0m, null, origClOrdId: origId);
                // Cancel-as-Replace carries erCum=0 (B3 venue's stale shape);
                // ExecutionReportProcessor's #299 P1 clamp hydrates the new
                // order with cum=preFill, leaves=qty-preFill so we don't
                // re-book the partial fill. The next Fill ER then carries the
                // CUMULATIVE post-fill total (=qty) with lastQty being the
                // delta actually executed against the new aggressor (leftover).
                var leftover = qty - preFill;
                harness.Proc.Apply(newId, ExecKind.Fill, 0, qty, leftover, newPx, null, origClOrdId: 0);
                break;

            default:
                throw new InvalidOperationException($"Unhandled scenario {scenario}");
        }

        // ---- Invariants ----
        AssertInvariants(harness, scenario, origId, newId, qty, newPx, preFill);
    }

    private static void AssertInvariants(
        Harness h, Scenario scenario, ulong origId, ulong newId, long qty, decimal newPx, long preFill)
    {
        // 1) Margin coordinator: exactly one resolution per registered intent.
        var totalResolutions = h.ReplaceCoord.Commits.Count + h.ReplaceCoord.Aborts.Count;
        Assert.True(totalResolutions == 1,
            $"expected exactly 1 commit-or-abort per intent; got commits={h.ReplaceCoord.Commits.Count} aborts={h.ReplaceCoord.Aborts.Count}");

        // 2) Registry is one-shot: nothing left to consume for newId.
        Assert.False(h.Registry.TryConsume(newId, out _),
            "PendingReplacementRegistry should not still hold the intent");

        // 3) Original order invariants.
        Assert.True(h.Book.TryGet(origId, out var origAfter));
        Assert.NotNull(origAfter);
        var origOk = scenario switch
        {
            Scenario.ReplaceRejectedByVenue
                => origAfter!.Status is OrderStatus.Working or OrderStatus.PartiallyFilled,
            _ => origAfter!.Status is OrderStatus.Replaced or OrderStatus.Filled,
        };
        Assert.True(origOk, $"orig status {origAfter!.Status} unexpected for {scenario}");
        AssertOrderArithmetic(origAfter);

        // 4) New order invariants — present iff scenario hydrated it.
        var hasNew = h.Book.TryGet(newId, out var newAfter) && newAfter is not null;
        switch (scenario)
        {
            case Scenario.ReplaceRejectedByVenue:
                Assert.False(hasNew, "rejected replace must not create new order in book");
                break;
            default:
                Assert.True(hasNew, $"{scenario} should have hydrated new order");
                AssertOrderArithmetic(newAfter!);
                break;
        }

        // 5) PositionKeeper: NetQuantity must equal the sum of fills booked.
        // For OriginalPartiallyFilled scenario, preFill on orig + qty on new = preFill + qty.
        // For RejectedByVenue, no fills happened.
        var expectedNet = scenario switch
        {
            Scenario.ReplaceRejectedByVenue => 0L,
            Scenario.PriorityLostHappy_FollowedByRealCancel => 0L,
            Scenario.OriginalPartiallyFilled_ThenCancelAsReplace => qty, // preFill on orig + (qty-preFill) on new = qty
            _ => qty,
        };
        var pos = h.Positions.GetOrCreate(Firm, Bob, Symbol);
        Assert.True(pos.NetQuantity == expectedNet,
            $"NetQuantity={pos.NetQuantity} expected={expectedNet} for {scenario} (qty={qty}, preFill={preFill}, newPx={newPx})");

        // 6) Owner resolution stays consistent.
        Assert.True(h.Ownership.TryResolve(origId, out var ownerOrig) && ownerOrig == Bob);
        if (hasNew)
        {
            Assert.True(h.Ownership.TryResolve(newId, out var ownerNew) && ownerNew == Bob);
        }

        // 7) Defensive: if commit happened, it must be for our (orig,new) pair.
        if (h.ReplaceCoord.Commits.Count == 1)
        {
            Assert.Equal(origId, h.ReplaceCoord.Commits[0].Orig);
            Assert.Equal(newId, h.ReplaceCoord.Commits[0].New);
        }
        if (h.ReplaceCoord.Aborts.Count == 1)
        {
            Assert.Equal(newId, h.ReplaceCoord.Aborts[0]);
        }
    }

    private static void AssertOrderArithmetic(Order o)
    {
        // Leaves + Cum must equal Quantity at every observable state.
        Assert.True(o.LeavesQuantity + o.CumulativeQuantity == o.Quantity,
            $"order {o.ClOrdId}: leaves({o.LeavesQuantity}) + cum({o.CumulativeQuantity}) != qty({o.Quantity})");

        // Terminal status invariants.
        if (o.Status == OrderStatus.Filled)
        {
            Assert.Equal(0, o.LeavesQuantity);
            Assert.Equal(o.Quantity, o.CumulativeQuantity);
        }
    }

    // ---- harness ----

    private sealed class Harness
    {
        public required OrderOwnershipMap Ownership { get; init; }
        public required WorkingOrderBook Book { get; init; }
        public required PositionKeeper Positions { get; init; }
        public required CashLedger Cash { get; init; }
        public required PendingReplacementRegistry Registry { get; init; }
        public required RecordingReplaceCoordinator ReplaceCoord { get; init; }
        public required ExecutionReportProcessor Proc { get; init; }
    }

    private sealed class RecordingReplaceCoordinator : IReplaceMarginCoordinator
    {
        public List<(ulong Orig, ulong New, decimal Notional)> Commits { get; } = new();
        public List<ulong> Aborts { get; } = new();

        public System.Threading.Tasks.Task<RiskDecision> PrepareReplaceAsync(
            ulong originalClOrdId, ulong newClOrdId, EndClientId owner,
            decimal newRemainingNotional, System.Threading.CancellationToken ct)
            => System.Threading.Tasks.Task.FromResult(RiskDecision.Approve);

        public void CommitReplace(ulong originalClOrdId, ulong newClOrdId, decimal confirmedRemainingNotional)
            => Commits.Add((originalClOrdId, newClOrdId, confirmedRemainingNotional));

        public void AbortReplace(ulong newClOrdId)
            => Aborts.Add(newClOrdId);
    }

    private sealed class NullSink : IExecutionEventSink
    {
        public void Publish(ExecutionEvent ev) { }
    }

    private static Harness BuildHarness()
    {
        var ownership = new OrderOwnershipMap();
        var book = new WorkingOrderBook();
        var positions = new PositionKeeper();
        var cash = new CashLedger();
        cash.SeedIfAbsent(Bob, 1_000_000m);
        var opts = new RiskOptions();
        opts.Margin.Enabled = true;
        opts.Margin.Initial["bob"] = 1_000_000m;
        var monitor = new StaticOptionsMonitor<RiskOptions>(opts);
        var marginProvider = new ReserveOnSubmitMarginProvider(monitor, NullLogger<ReserveOnSubmitMarginProvider>.Instance);
        var replaceCoord = new RecordingReplaceCoordinator();
        var reg = new PendingReplacementRegistry();

        var proc = new ExecutionReportProcessor(
            ownership, book, positions, new NullSink(), marginProvider,
            NullLogger<ExecutionReportProcessor>.Instance,
            algoSignals: null,
            cash: cash,
            replacements: reg,
            replaceMargin: replaceCoord,
            botErRouter: null);

        return new Harness
        {
            Ownership = ownership,
            Book = book,
            Positions = positions,
            Cash = cash,
            Registry = reg,
            ReplaceCoord = replaceCoord,
            Proc = proc,
        };
    }
}
