using B3.Trading.Application;
using B3.Trading.Application.Persistence;

namespace B3.Trading.Application.Tests;

/// <summary>
/// #671/#753 (RFC: admin account reset, PR 3, code-review addendum #4).
/// Focused unit coverage for <see cref="PnlKeeper.CaptureSymbolBasis"/> /
/// <see cref="PnlKeeper.RestoreSymbolBasis"/> in isolation from the
/// admin reset dispatcher plumbing: proves the pair round-trips EXACTLY
/// across the three mutually exclusive basis states a (firm, endClient,
/// symbol) cell can be in — a KNOWN avg-cost basis, an UNKNOWN-basis
/// leftover quantity (<see cref="PnlKeeper.GetUnknownBasisQty"/>), and
/// true absence — and that <see cref="PnlKeeper.RestoreSymbolBasis"/>
/// never leaks across firm, end-client, or symbol boundaries. This is
/// the precise fix for the gap <see cref="PnlKeeperSetAbsoluteAvgCostTests"/>
/// does not cover: <c>SetAbsoluteAvgCost</c> unconditionally clears the
/// unknown-basis leg, so it cannot be used to restore one.
/// </summary>
public class PnlKeeperCaptureRestoreSymbolBasisTests
{
    [Fact]
    public void CaptureSymbolBasis_KnownBasis_RoundTripsExactly()
    {
        var pnl = new PnlKeeper();
        pnl.SetAbsoluteAvgCost("FIRM01", "alice", "PETR4", 200, 25m);

        var snapshot = pnl.CaptureSymbolBasis("FIRM01", "alice", "PETR4");
        Assert.Equal(PnlKeeper.PnlBasisKind.Known, snapshot.Kind);
        Assert.Equal(200, snapshot.NetQuantity);
        Assert.Equal(25m, snapshot.AvgPrice);

        // Mutate away, then restore — must land back on EXACTLY the
        // captured (quantity, price), not just "some known basis".
        pnl.SetAbsoluteAvgCost("FIRM01", "alice", "PETR4", 999, 1m);
        pnl.RestoreSymbolBasis("FIRM01", "alice", "PETR4", snapshot);

        var restored = pnl.GetAvgCost("FIRM01", "alice", "PETR4");
        Assert.NotNull(restored);
        Assert.Equal(200, restored!.NetQuantity);
        Assert.Equal(25m, restored.AvgPrice);
        Assert.Equal(0, pnl.GetUnknownBasisQty("FIRM01", "alice", "PETR4"));
    }

    [Fact]
    public void CaptureSymbolBasis_UnknownBasisQty_RoundTripsExactly_NotCollapsedByRestore()
    {
        var pnl = new PnlKeeper();
        // Seed a legacy unknown-basis leg the way a pre-#271 snapshot
        // restore does: a nonzero quantity, zero average entry price.
        pnl.SeedAvgCostFromLegacyPositions(new[]
        {
            new PositionSnapshot("alice", "ITUB4", 40, 0m, "FIRM01"),
        });
        Assert.Equal(40, pnl.GetUnknownBasisQty("FIRM01", "alice", "ITUB4"));
        Assert.Null(pnl.GetAvgCost("FIRM01", "alice", "ITUB4"));

        var snapshot = pnl.CaptureSymbolBasis("FIRM01", "alice", "ITUB4");
        Assert.Equal(PnlKeeper.PnlBasisKind.UnknownQty, snapshot.Kind);
        Assert.Equal(40, snapshot.NetQuantity);
        Assert.Equal(0m, snapshot.AvgPrice);

        // Simulate the reset's Apply establishing a KNOWN basis for
        // this symbol, then simulate a rollback via RestoreSymbolBasis
        // — this must put the UNKNOWN leg back, not a known basis and
        // not absence. SetAbsoluteAvgCost could never do this: it
        // always clears the unknown-basis dictionary unconditionally.
        pnl.SetAbsoluteAvgCost("FIRM01", "alice", "ITUB4", 10, 5m);
        Assert.NotNull(pnl.GetAvgCost("FIRM01", "alice", "ITUB4"));

        pnl.RestoreSymbolBasis("FIRM01", "alice", "ITUB4", snapshot);

        Assert.Equal(40, pnl.GetUnknownBasisQty("FIRM01", "alice", "ITUB4"));
        Assert.Null(pnl.GetAvgCost("FIRM01", "alice", "ITUB4"));
    }

    [Fact]
    public void CaptureSymbolBasis_TrueAbsence_RoundTripsExactly_NotFabricated()
    {
        var pnl = new PnlKeeper();

        var snapshot = pnl.CaptureSymbolBasis("FIRM01", "alice", "VALE3");
        Assert.Equal(PnlKeeper.PnlBasisKind.Absent, snapshot.Kind);
        Assert.Equal(PnlKeeper.PnlSymbolBasisSnapshot.Absent, snapshot);

        // Simulate the reset's Apply establishing state for a symbol
        // that never had a row, then roll back — must return to TRUE
        // absence in both dictionaries, not a flat/zero known-basis
        // row and not a fabricated unknown-basis leg.
        pnl.SetAbsoluteAvgCost("FIRM01", "alice", "VALE3", 10, 15m);
        Assert.NotNull(pnl.GetAvgCost("FIRM01", "alice", "VALE3"));

        pnl.RestoreSymbolBasis("FIRM01", "alice", "VALE3", snapshot);

        Assert.Null(pnl.GetAvgCost("FIRM01", "alice", "VALE3"));
        Assert.Equal(0, pnl.GetUnknownBasisQty("FIRM01", "alice", "VALE3"));
    }

    [Fact]
    public void RestoreSymbolBasis_IsIsolatedPerFirm()
    {
        var pnl = new PnlKeeper();
        pnl.SetAbsoluteAvgCost("FIRM01", "alice", "PETR4", 200, 25m);
        pnl.SetAbsoluteAvgCost("FIRM02", "alice", "PETR4", 300, 40m);

        var firm1Snapshot = pnl.CaptureSymbolBasis("FIRM01", "alice", "PETR4");
        pnl.RestoreSymbolBasis("FIRM01", "alice", "PETR4", PnlKeeper.PnlSymbolBasisSnapshot.Absent);

        // FIRM01's row is gone; FIRM02's identically-keyed (endClient,
        // symbol) row under a different firm must be untouched.
        Assert.Null(pnl.GetAvgCost("FIRM01", "alice", "PETR4"));
        var firm2After = pnl.GetAvgCost("FIRM02", "alice", "PETR4");
        Assert.NotNull(firm2After);
        Assert.Equal(300, firm2After!.NetQuantity);
        Assert.Equal(40m, firm2After.AvgPrice);

        pnl.RestoreSymbolBasis("FIRM01", "alice", "PETR4", firm1Snapshot);
        var firm1After = pnl.GetAvgCost("FIRM01", "alice", "PETR4");
        Assert.NotNull(firm1After);
        Assert.Equal(200, firm1After!.NetQuantity);
        Assert.Equal(25m, firm1After.AvgPrice);
    }

    [Fact]
    public void RestoreSymbolBasis_IsIsolatedPerEndClient()
    {
        var pnl = new PnlKeeper();
        pnl.SetAbsoluteAvgCost("FIRM01", "alice", "PETR4", 200, 25m);
        pnl.SetAbsoluteAvgCost("FIRM01", "bob", "PETR4", 500, 10m);

        pnl.RestoreSymbolBasis("FIRM01", "alice", "PETR4", PnlKeeper.PnlSymbolBasisSnapshot.Absent);

        Assert.Null(pnl.GetAvgCost("FIRM01", "alice", "PETR4"));
        var bobAfter = pnl.GetAvgCost("FIRM01", "bob", "PETR4");
        Assert.NotNull(bobAfter);
        Assert.Equal(500, bobAfter!.NetQuantity);
        Assert.Equal(10m, bobAfter.AvgPrice);
    }

    [Fact]
    public void RestoreSymbolBasis_IsIsolatedPerSymbol()
    {
        var pnl = new PnlKeeper();
        pnl.SetAbsoluteAvgCost("FIRM01", "alice", "PETR4", 200, 25m);
        pnl.SetAbsoluteAvgCost("FIRM01", "alice", "VALE3", 80, 60m);

        pnl.RestoreSymbolBasis("FIRM01", "alice", "PETR4", PnlKeeper.PnlSymbolBasisSnapshot.Absent);

        Assert.Null(pnl.GetAvgCost("FIRM01", "alice", "PETR4"));
        var vale3After = pnl.GetAvgCost("FIRM01", "alice", "VALE3");
        Assert.NotNull(vale3After);
        Assert.Equal(80, vale3After!.NetQuantity);
        Assert.Equal(60m, vale3After.AvgPrice);
    }
}
