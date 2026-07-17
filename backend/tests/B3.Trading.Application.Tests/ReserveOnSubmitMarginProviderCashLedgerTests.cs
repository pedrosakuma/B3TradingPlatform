using B3.Trading.Application.Risk;
using B3.Trading.Application.Persistence;
using B3.Trading.Domain;
using Microsoft.Extensions.Logging.Abstractions;

// Slice 4 of #107 deprecates Margin.Initial; the configInitial branch
// here exercises the legacy fallback on purpose to prove ledger
// presence wins over config and that fallback still works when no
// ledger entry exists. Suppress at file scope; will be migrated when
// Margin.Initial is removed.
#pragma warning disable CS0618 // Type or member is obsolete

namespace B3.Trading.Application.Tests;

/// <summary>
/// Slice 2 of #107: when a CashLedger is wired in, it overrides the
/// per-owner allowance from <c>RiskOptions.Margin.Initial</c> and the
/// reservation ledger debits against settled cash instead of a static
/// config number.
/// </summary>
public class ReserveOnSubmitMarginProviderCashLedgerTests
{
    private static (ReserveOnSubmitMarginProvider provider, CashLedger ledger) Build(
        decimal? configInitial = null, string owner = "alice")
    {
        var opts = new RiskOptions();
        opts.Margin.Enabled = true;
        if (configInitial.HasValue)
            opts.Margin.Initial[owner] = configInitial.Value;
        var monitor = new StaticOptionsMonitor<RiskOptions>(opts);
        var ledger = new CashLedger();
        var provider = new ReserveOnSubmitMarginProvider(monitor, NullLogger<ReserveOnSubmitMarginProvider>.Instance, ledger);
        return (provider, ledger);
    }

    private static RiskContext Buy(string owner, decimal price, long qty) =>
        new(new EndClientId(owner), "FIRM", "PETR4", OrderSide.Buy, OrderType.Limit, qty, price);

    [Fact]
    public async Task LedgerSeed_TakesPrecedenceOverConfig()
    {
        // Config says 1M but ledger seeded at 500: ledger wins.
        var (p, ledger) = Build(configInitial: 1_000_000m);
        ledger.SeedIfAbsent("FIRM", new EndClientId("alice"), 500m);

        Assert.True((await p.TryReserveAsync(1, Buy("alice", 10m, 50), default)).Approved);
        var rejected = await p.TryReserveAsync(2, Buy("alice", 10m, 1), default);
        Assert.False(rejected.Approved);
    }

    [Fact]
    public async Task LedgerAbsent_FallsBackToConfig()
    {
        // No ledger entry for alice → config Margin.Initial wins.
        var (p, _) = Build(configInitial: 100m);

        Assert.True((await p.TryReserveAsync(1, Buy("alice", 10m, 10), default)).Approved);
        Assert.False((await p.TryReserveAsync(2, Buy("alice", 10m, 1), default)).Approved);
    }

    [Fact]
    public async Task LedgerNoEntryAndNoConfig_RejectsAtFirstBuy()
    {
        var (p, _) = Build();

        var rejected = await p.TryReserveAsync(1, Buy("ghost", 1m, 1), default);
        Assert.False(rejected.Approved);
    }

    [Fact]
    public async Task LedgerDebitedByFill_ReducesAvailableForNextOrder()
    {
        // The whole point of slice 2: a Buy that just filled must
        // reduce the ledger, so the NEXT Buy sees less capacity.
        var (p, ledger) = Build();
        var alice = new EndClientId("alice");
        ledger.SeedIfAbsent("FIRM", alice, 1_000m);

        // First Buy reserved, then filled — production wires
        // CashLedger.ApplyFill via ER processor; we simulate it here.
        Assert.True((await p.TryReserveAsync(10, Buy("alice", 10m, 100), default)).Approved);
        ledger.ApplyFill("FIRM", alice, OrderSide.Buy, 100, 10m);   // -1000 → balance 0
        p.OnExecution(10, ExecKind.Fill, lastQty: 100);     // releases the reservation

        // Next Buy must see zero available, even though the original
        // 1000 seed remains in config-land.
        var next = await p.TryReserveAsync(11, Buy("alice", 10m, 1), default);
        Assert.False(next.Approved);
    }

    [Fact]
    public async Task SellFill_CreditsLedger_GrowsAvailable()
    {
        var (p, ledger) = Build();
        var alice = new EndClientId("alice");
        ledger.SeedIfAbsent("FIRM", alice, 0m);

        // Selling inventory we already own credits cash, lifting the
        // ceiling for subsequent Buys.
        ledger.ApplyFill("FIRM", alice, OrderSide.Sell, 10, 50m);   // +500

        Assert.True((await p.TryReserveAsync(20, Buy("alice", 10m, 50), default)).Approved);
        Assert.False((await p.TryReserveAsync(21, Buy("alice", 10m, 1), default)).Approved);
    }

    [Fact]
    public async Task NegativeLedgerBalance_BlocksAllBuys()
    {
        // Defensive: if cash settled negative (margin call edge case),
        // every Buy must reject — we don't want the config fallback to
        // mask a real overdraft.
        var (p, ledger) = Build(configInitial: 1_000_000m);
        ledger.SeedIfAbsent("FIRM", new EndClientId("alice"), -10m);

        Assert.False((await p.TryReserveAsync(1, Buy("alice", 1m, 1), default)).Approved);
    }

    [Fact]
    public async Task SameOwner_CannotConsumeAnotherFirmsCashOrReservations()
    {
        var (p, ledger) = Build();
        var owner = new EndClientId("alice");
        ledger.SeedIfAbsent("FIRM01", owner, 1_000m);
        ledger.SeedIfAbsent("FIRM02", owner, 100m);

        var f1 = Buy("alice", 10m, 100) with { FirmId = "FIRM01" };
        var f2 = Buy("alice", 10m, 11) with { FirmId = "FIRM02" };

        Assert.True((await p.TryReserveAsync(1, f1, default)).Approved);
        Assert.False((await p.TryReserveAsync(2, f2, default)).Approved);
        Assert.Equal(1_000m, p.ReservedForTesting("FIRM01", "alice"));
        Assert.Equal(0m, p.ReservedForTesting("FIRM02", "alice"));
    }

    [Fact]
    public void Recovery_RebuildsReservationsPerFirmForSameOwner()
    {
        var (p, ledger) = Build();
        var owner = new EndClientId("alice");
        ledger.SeedIfAbsent("FIRM01", owner, 1_000m);
        ledger.SeedIfAbsent("FIRM02", owner, 500m);
        var orders = new[]
        {
            new OrderSnapshot(
                1, "alice", "PETR4", 1234, "Buy", "Limit",
                100, 10m, 80, 20, "PartiallyFilled", "FIRM01"),
            new OrderSnapshot(
                2, "alice", "PETR4", 1234, "Buy", "Limit",
                50, 10m, 40, 10, "PartiallyFilled", "FIRM02"),
        };

        p.RestoreRecoveryState(orders);

        Assert.Equal(800m, p.ReservedForTesting("FIRM01", "alice"));
        Assert.Equal(400m, p.ReservedForTesting("FIRM02", "alice"));
        Assert.Equal(200m, p.AvailableForTesting("FIRM01", "alice"));
        Assert.Equal(100m, p.AvailableForTesting("FIRM02", "alice"));
    }
}
