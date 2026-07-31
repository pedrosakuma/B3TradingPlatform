using B3.Trading.Application.Persistence;
using B3.Trading.Domain;

namespace B3.Trading.Application.Tests;

/// <summary>
/// #671/#753 (RFC: admin account reset, PR 3). Unit coverage for
/// <see cref="AccountResetPayloadResolver.Resolve"/> — the pure
/// function that turns "current non-flat positions" + "configured
/// seeds" into the absolute payload persisted on an
/// <see cref="AccountResetEvent"/>. No DI/HTTP surface here; the
/// endpoint- and replay-level wiring is covered by
/// <c>AccountResetAdminEndpointTests</c> (Api.Tests) and
/// <c>AccountResetRecoveryTests</c> (Persistence).
/// </summary>
public class AccountResetPayloadResolverTests
{
    private static readonly EndClientId Alice = new("alice");

    [Fact]
    public void NoCurrentPositions_NoSeeds_ResolvesToZeroCashAndNoPositions()
    {
        var payload = AccountResetPayloadResolver.Resolve(
            "FIRM01", Alice, Array.Empty<Position>(), new CashSeedOptions(), new PositionSeedOptions());

        Assert.Equal(0m, payload.CashAvailable);
        Assert.Empty(payload.Positions);
    }

    [Fact]
    public void CurrentNonFlatPosition_NoSeed_FlattensToZeroZero()
    {
        var current = new[] { MakePosition("PETR4", 500, 28.5m) };

        var payload = AccountResetPayloadResolver.Resolve(
            "FIRM01", Alice, current, new CashSeedOptions(), new PositionSeedOptions());

        var entry = Assert.Single(payload.Positions);
        Assert.Equal("PETR4", entry.Symbol);
        Assert.Equal(0, entry.NetQuantity);
        Assert.Equal(0m, entry.AverageEntryPrice);
    }

    [Fact]
    public void FlatCurrentPosition_IsIgnored_NotIncludedInPayload()
    {
        // A symbol that was previously touched but is already flat
        // (NetQuantity == 0) is a no-op target — including it would
        // bloat the persisted payload for no behavioural difference
        // (mirrors PositionKeeper.Snapshot's own flat-position skip).
        var current = new[] { MakePosition("PETR4", 0, 0m) };

        var payload = AccountResetPayloadResolver.Resolve(
            "FIRM01", Alice, current, new CashSeedOptions(), new PositionSeedOptions());

        Assert.Empty(payload.Positions);
    }

    [Fact]
    public void ConfiguredPositionSeed_OverridesFlattenDefault()
    {
        var current = new[] { MakePosition("PETR4", 500, 28.5m) };
        var seeds = new PositionSeedOptions
        {
            Seeds =
            {
                new PositionSeed { EndClientId = "alice", Firm = "FIRM01", Symbol = "PETR4", Quantity = 100, AverageEntryPrice = 10m },
            },
        };

        var payload = AccountResetPayloadResolver.Resolve(
            "FIRM01", Alice, current, new CashSeedOptions(), seeds);

        var entry = Assert.Single(payload.Positions);
        Assert.Equal(100, entry.NetQuantity);
        Assert.Equal(10m, entry.AverageEntryPrice);
    }

    [Fact]
    public void ConfiguredPositionSeed_ForSymbolNeverHeld_IsAddedAsNewTarget()
    {
        var seeds = new PositionSeedOptions
        {
            Seeds =
            {
                new PositionSeed { EndClientId = "alice", Firm = "FIRM01", Symbol = "VALE3", Quantity = 200, AverageEntryPrice = 60m },
            },
        };

        var payload = AccountResetPayloadResolver.Resolve(
            "FIRM01", Alice, Array.Empty<Position>(), new CashSeedOptions(), seeds);

        var entry = Assert.Single(payload.Positions);
        Assert.Equal("VALE3", entry.Symbol);
        Assert.Equal(200, entry.NetQuantity);
        Assert.Equal(60m, entry.AverageEntryPrice);
    }

    [Fact]
    public void PositionSeed_FirmMismatch_IsIgnored()
    {
        var seeds = new PositionSeedOptions
        {
            Seeds =
            {
                new PositionSeed { EndClientId = "alice", Firm = "FIRM02", Symbol = "VALE3", Quantity = 200, AverageEntryPrice = 60m },
            },
        };

        var payload = AccountResetPayloadResolver.Resolve(
            "FIRM01", Alice, Array.Empty<Position>(), new CashSeedOptions(), seeds);

        Assert.Empty(payload.Positions);
    }

    [Fact]
    public void PositionSeed_EndClientMismatch_IsIgnored()
    {
        var seeds = new PositionSeedOptions
        {
            Seeds =
            {
                new PositionSeed { EndClientId = "bob", Firm = "FIRM01", Symbol = "VALE3", Quantity = 200, AverageEntryPrice = 60m },
            },
        };

        var payload = AccountResetPayloadResolver.Resolve(
            "FIRM01", Alice, Array.Empty<Position>(), new CashSeedOptions(), seeds);

        Assert.Empty(payload.Positions);
    }

    [Fact]
    public void PositionSeed_WithUnsetFirm_DefaultsToPositionKeeperDefaultFirm()
    {
        // Mirrors PositionSeedOptions.Firm's own doc comment: an unset
        // Firm lands in PositionKeeper.DefaultFirmId, invisible to real
        // multi-firm callers.
        var seeds = new PositionSeedOptions
        {
            Seeds =
            {
                new PositionSeed { EndClientId = "alice", Symbol = "VALE3", Quantity = 50, AverageEntryPrice = 12m },
            },
        };

        var defaultFirmPayload = AccountResetPayloadResolver.Resolve(
            PositionKeeper.DefaultFirmId, Alice, Array.Empty<Position>(), new CashSeedOptions(), seeds);
        Assert.Single(defaultFirmPayload.Positions);

        var otherFirmPayload = AccountResetPayloadResolver.Resolve(
            "FIRM01", Alice, Array.Empty<Position>(), new CashSeedOptions(), seeds);
        Assert.Empty(otherFirmPayload.Positions);
    }

    [Fact]
    public void MultipleSymbols_ResultIsSortedOrdinalBySymbol_ForDeterministicPayloadOrdering()
    {
        var current = new[]
        {
            MakePosition("VALE3", 10, 60m),
            MakePosition("PETR4", 20, 30m),
            MakePosition("ITUB4", 30, 25m),
        };

        var payload = AccountResetPayloadResolver.Resolve(
            "FIRM01", Alice, current, new CashSeedOptions(), new PositionSeedOptions());

        Assert.Equal(new[] { "ITUB4", "PETR4", "VALE3" }, payload.Positions.Select(p => p.Symbol));
    }

    [Fact]
    public void CashSeed_MatchingFirmAndEndClient_IsUsed()
    {
        var seeds = new CashSeedOptions
        {
            Seeds = { new CashSeed { FirmId = "FIRM01", EndClientId = "alice", InitialAvailable = 10_000m } },
        };

        var payload = AccountResetPayloadResolver.Resolve(
            "FIRM01", Alice, Array.Empty<Position>(), seeds, new PositionSeedOptions());

        Assert.Equal(10_000m, payload.CashAvailable);
    }

    [Fact]
    public void CashSeed_FirmComparisonIsCaseInsensitive()
    {
        var seeds = new CashSeedOptions
        {
            Seeds = { new CashSeed { FirmId = "firm01", EndClientId = "alice", InitialAvailable = 5_000m } },
        };

        var payload = AccountResetPayloadResolver.Resolve(
            "FIRM01", Alice, Array.Empty<Position>(), seeds, new PositionSeedOptions());

        Assert.Equal(5_000m, payload.CashAvailable);
    }

    [Fact]
    public void CashSeed_EndClientMismatch_FallsBackToZero()
    {
        var seeds = new CashSeedOptions
        {
            Seeds = { new CashSeed { FirmId = "FIRM01", EndClientId = "bob", InitialAvailable = 5_000m } },
        };

        var payload = AccountResetPayloadResolver.Resolve(
            "FIRM01", Alice, Array.Empty<Position>(), seeds, new PositionSeedOptions());

        Assert.Equal(0m, payload.CashAvailable);
    }

    [Fact]
    public void CashSeed_FirmMismatch_FallsBackToZero()
    {
        var seeds = new CashSeedOptions
        {
            Seeds = { new CashSeed { FirmId = "FIRM02", EndClientId = "alice", InitialAvailable = 5_000m } },
        };

        var payload = AccountResetPayloadResolver.Resolve(
            "FIRM01", Alice, Array.Empty<Position>(), seeds, new PositionSeedOptions());

        Assert.Equal(0m, payload.CashAvailable);
    }

    [Fact]
    public void SignupInitialBalance_IsNeverConsulted()
    {
        // CashSeedOptions.SignupInitialBalance is a firm-agnostic global
        // signup default, not a per-end-client seed — must NOT leak
        // into a reset target even when set.
        var seeds = new CashSeedOptions { SignupInitialBalance = 1_000_000m };

        var payload = AccountResetPayloadResolver.Resolve(
            "FIRM01", Alice, Array.Empty<Position>(), seeds, new PositionSeedOptions());

        Assert.Equal(0m, payload.CashAvailable);
    }

    private static Position MakePosition(string symbol, long netQuantity, decimal averageEntryPrice)
    {
        var p = new Position(Alice, symbol);
        if (netQuantity != 0)
            p.ApplyFill(netQuantity > 0 ? OrderSide.Buy : OrderSide.Sell, Math.Abs(netQuantity), averageEntryPrice);
        return p;
    }
}
