using B3.Trading.Application.Risk;
using B3.Trading.Domain;
using Microsoft.Extensions.Logging.Abstractions;

namespace B3.Trading.Application.Tests;

public class ReserveOnSubmitMarginProviderTests
{
    private static (ReserveOnSubmitMarginProvider provider, StaticOptionsMonitor<RiskOptions> monitor) Build(
        decimal initial = 100_000m, string owner = "alice")
    {
        var opts = new RiskOptions();
        opts.Margin.Enabled = true;
        opts.Margin.Initial[owner] = initial;
        var monitor = new StaticOptionsMonitor<RiskOptions>(opts);
        var provider = new ReserveOnSubmitMarginProvider(monitor, NullLogger<ReserveOnSubmitMarginProvider>.Instance);
        return (provider, monitor);
    }

    private static RiskContext Buy(string owner, decimal price, long qty, OrderType type = OrderType.Limit) =>
        new(new EndClientId(owner), "FIRM", "PETR4", OrderSide.Buy, type, qty, type == OrderType.Market ? null : price);

    [Fact]
    public async Task Approves_until_balance_depleted_then_rejects()
    {
        var (p, _) = Build(initial: 1_000m);
        Assert.True((await p.TryReserveAsync(1, Buy("alice", 10m, 50), CancellationToken.None)).Approved);
        Assert.True((await p.TryReserveAsync(2, Buy("alice", 10m, 50), CancellationToken.None)).Approved);
        var d3 = await p.TryReserveAsync(3, Buy("alice", 10m, 1), CancellationToken.None);
        Assert.False(d3.Approved);
        Assert.Contains("insufficient margin", d3.Reason);
    }

    [Fact]
    public async Task Cancel_releases_reservation()
    {
        var (p, _) = Build(initial: 1_000m);
        Assert.True((await p.TryReserveAsync(1, Buy("alice", 10m, 100), CancellationToken.None)).Approved);
        Assert.False((await p.TryReserveAsync(2, Buy("alice", 10m, 1), CancellationToken.None)).Approved);
        p.OnExecution(1, ExecKind.Canceled, 0);
        Assert.True((await p.TryReserveAsync(3, Buy("alice", 10m, 100), CancellationToken.None)).Approved);
    }

    [Fact]
    public async Task Reject_releases_reservation()
    {
        var (p, _) = Build(initial: 1_000m);
        Assert.True((await p.TryReserveAsync(1, Buy("alice", 10m, 100), CancellationToken.None)).Approved);
        p.OnExecution(1, ExecKind.Rejected, 0);
        Assert.Equal(1_000m, p.AvailableForTesting("alice"));
    }

    [Fact]
    public async Task PartialFill_releases_proportionally()
    {
        var (p, _) = Build(initial: 1_000m);
        Assert.True((await p.TryReserveAsync(1, Buy("alice", 10m, 100), CancellationToken.None)).Approved);
        Assert.Equal(0m, p.AvailableForTesting("alice"));
        p.OnExecution(1, ExecKind.PartialFill, 30);
        Assert.Equal(300m, p.AvailableForTesting("alice"));
        p.OnExecution(1, ExecKind.PartialFill, 20);
        Assert.Equal(500m, p.AvailableForTesting("alice"));
    }

    [Fact]
    public async Task Terminal_fill_releases_remaining()
    {
        var (p, _) = Build(initial: 1_000m);
        Assert.True((await p.TryReserveAsync(1, Buy("alice", 10m, 100), CancellationToken.None)).Approved);
        p.OnExecution(1, ExecKind.PartialFill, 30);
        p.OnExecution(1, ExecKind.Fill, 70);
        Assert.Equal(1_000m, p.AvailableForTesting("alice"));
    }

    [Fact]
    public async Task Sells_skip_reservation()
    {
        var (p, _) = Build(initial: 100m);
        var ctx = new RiskContext(new EndClientId("alice"), "F", "PETR4", OrderSide.Sell, OrderType.Limit, 10_000, 99m);
        var d = await p.TryReserveAsync(1, ctx, CancellationToken.None);
        Assert.True(d.Approved);
        Assert.Equal(0m, p.ReservedForTesting("alice"));
    }

    [Fact]
    public async Task Markets_skip_reservation()
    {
        var (p, _) = Build(initial: 100m);
        var d = await p.TryReserveAsync(1, Buy("alice", 0m, 10_000, OrderType.Market), CancellationToken.None);
        Assert.True(d.Approved);
        Assert.Equal(0m, p.ReservedForTesting("alice"));
    }

    [Fact]
    public async Task Unknown_owner_has_zero_balance_and_is_rejected()
    {
        var (p, _) = Build(initial: 1_000m, owner: "alice");
        var d = await p.TryReserveAsync(1, Buy("bob", 1m, 1), CancellationToken.None);
        Assert.False(d.Approved);
    }

    [Fact]
    public async Task ReleaseReservation_is_idempotent()
    {
        var (p, _) = Build(initial: 1_000m);
        await p.TryReserveAsync(1, Buy("alice", 10m, 100), CancellationToken.None);
        p.ReleaseReservation(1);
        p.ReleaseReservation(1);
        Assert.Equal(1_000m, p.AvailableForTesting("alice"));
    }

    [Fact]
    public void OnExecution_for_unknown_clordid_is_noop()
    {
        var (p, _) = Build();
        p.OnExecution(999, ExecKind.Fill, 100);
        Assert.Equal(100_000m, p.AvailableForTesting("alice"));
    }

    [Fact]
    public async Task Hot_reload_of_initial_balance_is_observed()
    {
        var (p, monitor) = Build(initial: 100m);
        Assert.False((await p.TryReserveAsync(1, Buy("alice", 10m, 50), CancellationToken.None)).Approved);
        var newOpts = new RiskOptions();
        newOpts.Margin.Enabled = true;
        newOpts.Margin.Initial["alice"] = 1_000m;
        monitor.Set(newOpts);
        Assert.True((await p.TryReserveAsync(2, Buy("alice", 10m, 50), CancellationToken.None)).Approved);
    }

    [Fact]
    public async Task NoOpProvider_always_approves()
    {
        IMarginProvider noop = new NoOpMarginProvider();
        var d = await noop.TryReserveAsync(1, Buy("alice", 999_999m, 999_999), CancellationToken.None);
        Assert.True(d.Approved);
        noop.OnExecution(1, ExecKind.Canceled, 0);
        noop.ReleaseReservation(1);
    }
}
