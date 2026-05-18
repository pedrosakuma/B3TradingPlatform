using System.Diagnostics.Metrics;
using B3.Trading.Api;
using B3.Trading.Application;
using B3.Trading.Application.Persistence;
using B3.Trading.Domain;
using Microsoft.Extensions.Logging.Abstractions;

namespace B3.Trading.Api.Tests;

/// <summary>
/// PR #316 P2 (review). Master-row avg-cost fallback for the daily
/// statement projection. The pre-review code fell back to the
/// (potentially polluted) aggregate avg when the per-bucket basis
/// store was missing or qty-mismatched — that read the master row's
/// avg cost as <c>aggregate.Avg</c> even though sub-bucket fills had
/// already pushed the aggregate basis around. The fixed code emits
/// <c>AvgPrice = 0m</c> (fail-closed) and bumps
/// <c>statement.master_avg_basis_degraded_total</c>, surfacing the
/// invariant violation post-P1 backfill instead of silently shipping
/// a polluted avg downstream.
/// </summary>
public class StatementMasterAvgFallbackTests
{
    private const string Firm = "FIRM01";
    private const string OwnerStr = "alice";

    [Fact]
    public void BuildMasterBucketRows_BucketBasisMissing_EmitsZeroAvgAndBumpsCounter()
    {
        StatementEndpoints.ResetMasterAvgDegradedWarnDedupeForTesting();
        var owner = new EndClientId(OwnerStr);
        var aggregate = new Dictionary<string, (long Qty, decimal Avg)>(StringComparer.Ordinal)
        {
            // Pollution baseline: the aggregate basis includes
            // sub-bucket fills, so its avg is NOT a faithful master
            // avg. If the projection fell back to it, this is the
            // number that would leak.
            ["PETR4"] = (Qty: 100, Avg: 27.5m),
        };
        var subSum = new Dictionary<string, long>(StringComparer.Ordinal)
        {
            ["PETR4"] = 40, // anySub == true, masterQty = 100 - 40 = 60.
        };
        // Bucket basis intentionally absent — the degraded branch.
        var subAccountPnl = new SubAccountPnlKeeper();

        using var collector = new CounterCollector("trading.statement.master_avg_basis_degraded_total");
        var rows = StatementEndpoints.BuildMasterBucketRows(
            Firm, owner, new[] { "PETR4" }, aggregate, subSum, subAccountPnl, NullLoggerFactory.Instance);

        var row = Assert.Single(rows);
        Assert.Equal("PETR4", row.Symbol);
        Assert.Equal(60, row.NetQty);
        Assert.Equal(0m, row.AvgPrice); // fail-closed, NOT 27.5m.
        Assert.Equal(1, collector.Total);
    }

    [Fact]
    public void BuildMasterBucketRows_BucketBasisQtyMismatched_EmitsZeroAvgAndBumpsCounter()
    {
        StatementEndpoints.ResetMasterAvgDegradedWarnDedupeForTesting();
        var owner = new EndClientId(OwnerStr);
        var aggregate = new Dictionary<string, (long Qty, decimal Avg)>(StringComparer.Ordinal)
        {
            ["VALE3"] = (Qty: 200, Avg: 50m),
        };
        var subSum = new Dictionary<string, long>(StringComparer.Ordinal)
        {
            ["VALE3"] = 80, // masterQty = 200 - 80 = 120.
        };
        var subAccountPnl = new SubAccountPnlKeeper();
        // Seed the master bucket with a DIFFERENT qty so the
        // basis-vs-master invariant fails the equality check.
        subAccountPnl.SeedMasterBucketBasisIfAbsent(Firm, OwnerStr, "VALE3", signedQuantity: 90, avgPrice: 48m);

        using var collector = new CounterCollector("trading.statement.master_avg_basis_degraded_total");
        var rows = StatementEndpoints.BuildMasterBucketRows(
            Firm, owner, new[] { "VALE3" }, aggregate, subSum, subAccountPnl, NullLoggerFactory.Instance);

        var row = Assert.Single(rows);
        Assert.Equal(120, row.NetQty);
        Assert.Equal(0m, row.AvgPrice);
        Assert.Equal(1, collector.Total);
    }

    [Fact]
    public void BuildMasterBucketRows_BucketBasisPresent_UsesBucketAvgAndNoCounter()
    {
        StatementEndpoints.ResetMasterAvgDegradedWarnDedupeForTesting();
        var owner = new EndClientId(OwnerStr);
        var aggregate = new Dictionary<string, (long Qty, decimal Avg)>(StringComparer.Ordinal)
        {
            ["PETR4"] = (Qty: 100, Avg: 27.5m), // polluted aggregate.
        };
        var subSum = new Dictionary<string, long>(StringComparer.Ordinal)
        {
            ["PETR4"] = 40,
        };
        var subAccountPnl = new SubAccountPnlKeeper();
        // Real master basis: 60 @ 30.
        subAccountPnl.SeedMasterBucketBasisIfAbsent(Firm, OwnerStr, "PETR4", signedQuantity: 60, avgPrice: 30m);

        using var collector = new CounterCollector("trading.statement.master_avg_basis_degraded_total");
        var rows = StatementEndpoints.BuildMasterBucketRows(
            Firm, owner, new[] { "PETR4" }, aggregate, subSum, subAccountPnl, NullLoggerFactory.Instance);

        var row = Assert.Single(rows);
        Assert.Equal(60, row.NetQty);
        Assert.Equal(30m, row.AvgPrice); // bucket basis, not aggregate.
        Assert.Equal(0, collector.Total);
    }

    /// <summary>
    /// Lightweight MeterListener wrapper that subscribes to a single
    /// instrument by name and accumulates the long values it sees.
    /// Disposed automatically per-test so listeners don't pile up.
    /// </summary>
    private sealed class CounterCollector : IDisposable
    {
        private readonly MeterListener _listener;
        public long Total;

        public CounterCollector(string instrumentName)
        {
            _listener = new MeterListener();
            _listener.InstrumentPublished = (instr, listener) =>
            {
                if (instr.Name == instrumentName) listener.EnableMeasurementEvents(instr);
            };
            _listener.SetMeasurementEventCallback<long>((_, m, _, _) => Interlocked.Add(ref Total, m));
            _listener.Start();
        }

        public void Dispose() => _listener.Dispose();
    }
}
