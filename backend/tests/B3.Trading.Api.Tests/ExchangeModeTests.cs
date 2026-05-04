using B3.Trading.Infrastructure;

namespace B3.Trading.Api.Tests;

public class ExchangeOptionsResolveModeTests
{
    [Fact]
    public void Mode_Wins_Over_Legacy_Flags()
    {
        var opts = new ExchangeOptions
        {
            Mode = ExchangeMode.Unavailable,
            UseStubGateway = true,
            UseRealEntryPointClient = true,
        };
        Assert.Equal(ExchangeMode.Unavailable, opts.ResolveMode());
    }

    [Fact]
    public void Default_Is_Mock_When_No_Flags_Set()
    {
        Assert.Equal(ExchangeMode.Mock, new ExchangeOptions().ResolveMode());
    }

    [Fact]
    public void Legacy_UseStubGateway_Maps_To_Stub()
    {
        Assert.Equal(ExchangeMode.Stub, new ExchangeOptions { UseStubGateway = true }.ResolveMode());
    }

    [Fact]
    public void Legacy_UseRealEntryPointClient_Maps_To_Real()
    {
        Assert.Equal(ExchangeMode.Real, new ExchangeOptions { UseRealEntryPointClient = true }.ResolveMode());
    }

    [Fact]
    public void Stub_Wins_Over_Real_In_Legacy_Flags()
    {
        // Mirrors pre-existing behavior in Program.cs: useStub was checked first.
        var opts = new ExchangeOptions { UseStubGateway = true, UseRealEntryPointClient = true };
        Assert.Equal(ExchangeMode.Stub, opts.ResolveMode());
    }
}

public class UnavailableExchangeGatewayTests
{
    private static B3.Trading.Domain.Order MakeOrder() =>
        new(1UL, new B3.Trading.Domain.EndClientId("e1"), "PETR4", 12345UL,
            B3.Trading.Domain.OrderSide.Buy, B3.Trading.Domain.OrderType.Limit, 100, 30m, "default");

    [Fact]
    public async Task Submit_Throws_With_Reason()
    {
        var gw = new UnavailableExchangeGateway();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            gw.SubmitAsync(MakeOrder(), default));
        Assert.Contains("unavailable", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Cancel_Throws()
    {
        var gw = new UnavailableExchangeGateway();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            gw.CancelAsync(MakeOrder(), 2UL, default));
    }

    [Fact]
    public async Task CancelReplace_Throws()
    {
        var gw = new UnavailableExchangeGateway();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            gw.CancelReplaceAsync(MakeOrder(), 2UL, 200, 31m, default));
    }
}

public class ExchangeStatusTests
{
    [Theory]
    [InlineData(ExchangeMode.Stub, true)]
    [InlineData(ExchangeMode.Mock, true)]
    [InlineData(ExchangeMode.Real, true)]
    [InlineData(ExchangeMode.Unavailable, false)]
    [InlineData(ExchangeMode.Simulator, true)]
    public void ReadyForOrders_Reflects_Mode(ExchangeMode mode, bool expected)
    {
        var status = new ExchangeStatus(mode, firmCount: 0);
        Assert.Equal(expected, status.ReadyForOrders);
    }

    [Fact]
    public void FromOptions_Unavailable_Reports_Zero_Firms_Even_With_Configured_Slot()
    {
        // Reproduces the IConfiguration array-merge gotcha: a base
        // appsettings.json carries a default firm and a Docker overlay
        // can't clear it via Firms:[]. With Mode=Unavailable, the
        // operational firm count must still be 0.
        var opts = new ExchangeOptions
        {
            Mode = ExchangeMode.Unavailable,
            Firms = { new FirmConfig { FirmId = "DEFAULT", Endpoint = "localhost:9000" } },
        };
        var status = ExchangeStatus.FromOptions(opts);
        Assert.Equal(ExchangeMode.Unavailable, status.Mode);
        Assert.Equal(0, status.FirmCount);
        Assert.False(status.ReadyForOrders);
    }

    [Fact]
    public void FromOptions_Filters_Empty_FirmIds()
    {
        var opts = new ExchangeOptions
        {
            Mode = ExchangeMode.Real,
            Firms =
            {
                new FirmConfig { FirmId = "REAL", Endpoint = "h:1" },
                new FirmConfig { FirmId = "  ", Endpoint = "h:2" },
                new FirmConfig { FirmId = "", Endpoint = "h:3" },
            },
        };
        var status = ExchangeStatus.FromOptions(opts);
        Assert.Equal(1, status.FirmCount);
    }

    [Fact]
    public void FromOptions_Counts_Configured_Firms_When_Wired()
    {
        var opts = new ExchangeOptions
        {
            Mode = ExchangeMode.Mock,
            Firms =
            {
                new FirmConfig { FirmId = "A", Endpoint = "h:1" },
                new FirmConfig { FirmId = "B", Endpoint = "h:2" },
            },
        };
        var status = ExchangeStatus.FromOptions(opts);
        Assert.Equal(2, status.FirmCount);
        Assert.True(status.ReadyForOrders);
    }
}
