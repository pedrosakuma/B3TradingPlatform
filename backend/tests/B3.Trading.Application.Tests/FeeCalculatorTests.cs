using B3.Trading.Domain;

namespace B3.Trading.Application.Tests;

/// <summary>
/// Q2.3 (#270). Pure-math coverage for <see cref="BpsFeeCalculator"/>.
/// End-to-end ER + WAL plumbing is covered separately
/// (<see cref="ExecutionReportProcessorFeeTests"/>).
/// </summary>
public class FeeCalculatorTests
{
    private static BpsFeeCalculator Build(FeeOptions opts) =>
        new(new StaticOptionsMonitor<FeeOptions>(opts));

    [Fact]
    public void Defaults_NotionalTimesBps()
    {
        // 100 lots @ R$1000 → notional R$100k.
        // brokerage 5 bps = 50; emol 3.25 bps = 32.50; liq 2.75 bps = 27.50.
        var calc = Build(new FeeOptions
        {
            BrokerageBps = 5m,
            BrokerageMin = 0m,
            EmolumentosBps = 3.25m,
            LiquidacaoBps = 2.75m,
        });

        var fb = calc.Compute("PETR4", OrderSide.Buy, 100, 1_000m);

        Assert.Equal(50m, fb.Brokerage);
        Assert.Equal(32.50m, fb.Emolumentos);
        Assert.Equal(27.50m, fb.Liquidacao);
        Assert.Equal(110m, fb.Total);
    }

    [Fact]
    public void BrokerageMin_WinsOverBpsFloor()
    {
        // notional R$100; 5 bps would be R$0.05 which is below the
        // R$2 floor → brokerage clamped at R$2.
        var calc = Build(new FeeOptions
        {
            BrokerageBps = 5m,
            BrokerageMin = 2m,
            EmolumentosBps = 0m,
            LiquidacaoBps = 0m,
        });

        var fb = calc.Compute("PETR4", OrderSide.Buy, 10, 10m);

        Assert.Equal(2m, fb.Brokerage);
        Assert.Equal(0m, fb.Emolumentos);
        Assert.Equal(0m, fb.Liquidacao);
        Assert.Equal(2m, fb.Total);
    }

    [Fact]
    public void PerSymbolOverride_Applies_DefaultsElsewhere()
    {
        var opts = new FeeOptions
        {
            BrokerageBps = 5m,
            BrokerageMin = 0m,
            EmolumentosBps = 3.25m,
            LiquidacaoBps = 2.75m,
            Overrides = new()
            {
                new FeeSymbolOverride
                {
                    Symbol = "PETR4",
                    BrokerageBps = 10m, // double the default
                },
            },
        };
        var calc = Build(opts);

        // PETR4: 100k notional @ 10bps brokerage = 100; emol/liq inherit defaults.
        var petr = calc.Compute("PETR4", OrderSide.Buy, 100, 1_000m);
        Assert.Equal(100m, petr.Brokerage);
        Assert.Equal(32.50m, petr.Emolumentos);
        Assert.Equal(27.50m, petr.Liquidacao);

        // VALE3: no override → defaults used (5 bps brokerage = 50).
        var vale = calc.Compute("VALE3", OrderSide.Buy, 100, 1_000m);
        Assert.Equal(50m, vale.Brokerage);
    }

    [Fact]
    public void Rounding_Is_TwoDecimals_AwayFromZero()
    {
        // notional 33; brokerage 1 bps = 0.0033 → rounds AwayFromZero to 0.00 (still below floor 0).
        // Use a more interesting case: notional 12345.67, 7.5 bps brokerage = 9.259252...
        // rounded AwayFromZero to 2dp = 9.26.
        var calc = Build(new FeeOptions
        {
            BrokerageBps = 7.5m,
            BrokerageMin = 0m,
            EmolumentosBps = 0m,
            LiquidacaoBps = 0m,
        });

        var fb = calc.Compute("PETR4", OrderSide.Buy, 1, 12_345.67m);

        // 12345.67 * 7.5 / 10000 = 9.2592525 → AwayFromZero to 9.26.
        Assert.Equal(9.26m, fb.Brokerage);
    }

    [Fact]
    public void HotReload_UsesLiveOptions()
    {
        var monitor = new StaticOptionsMonitor<FeeOptions>(new FeeOptions
        {
            BrokerageBps = 5m,
            BrokerageMin = 0m,
            EmolumentosBps = 0m,
            LiquidacaoBps = 0m,
        });
        var calc = new BpsFeeCalculator(monitor);

        Assert.Equal(50m, calc.Compute("X", OrderSide.Buy, 100, 1_000m).Brokerage);

        monitor.Set(new FeeOptions
        {
            BrokerageBps = 20m,
            BrokerageMin = 0m,
            EmolumentosBps = 0m,
            LiquidacaoBps = 0m,
        });

        Assert.Equal(200m, calc.Compute("X", OrderSide.Buy, 100, 1_000m).Brokerage);
    }
}
