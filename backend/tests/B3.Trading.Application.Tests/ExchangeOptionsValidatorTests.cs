using B3.Trading.Infrastructure;

namespace B3.Trading.Application.Tests;

public class ExchangeOptionsValidatorTests
{
    private static FirmConfig ValidFirm(string firmId = "FIRM_A") => new()
    {
        FirmId = firmId,
        Endpoint = "broker.example.com:9000",
        SessionId = 100,
        SessionVerId = 1,
        EnteringFirm = 200,
        AccessKey = "secret",
        SenderLocation = "BR-SP",
        EnteringTrader = "TR1",
        KeepAliveIntervalMs = 1000,
    };

    private static ExchangeOptions Real(params FirmConfig[] firms) => new()
    {
        Mode = ExchangeMode.Real,
        Firms = firms.ToList(),
    };

    private static ExchangeOptionsValidator Sut() => new();

    [Fact]
    public void NonRealMode_TolaratesPartialFirmConfig()
    {
        var opts = new ExchangeOptions
        {
            Mode = ExchangeMode.Mock,
            Firms = { new FirmConfig { FirmId = "TEST" } }, // missing everything else
        };

        Assert.True(Sut().Validate(null, opts).Succeeded);
    }

    [Fact]
    public void Real_NoFirms_Fails()
    {
        var result = Sut().Validate(null, new ExchangeOptions { Mode = ExchangeMode.Real });
        Assert.False(result.Succeeded);
        Assert.Contains("no Firms[]", string.Join(";", result.Failures!));
    }

    [Fact]
    public void Real_InvalidConnectionTimeouts_FailStartupValidation()
    {
        var firm = ValidFirm();
        firm.InitialReconnectDelay = TimeSpan.Zero;
        firm.MaxReconnectDelay = TimeSpan.FromMilliseconds(-1);
        firm.DnsResolutionTimeout = TimeSpan.Zero;
        firm.GracefulTerminateTimeout = TimeSpan.Zero;

        var result = Sut().Validate(null, Real(firm));

        Assert.False(result.Succeeded);
        var failures = string.Join(";", result.Failures!);
        Assert.Contains("InitialReconnectDelay", failures);
        Assert.Contains("MaxReconnectDelay", failures);
        Assert.Contains("DnsResolutionTimeout", failures);
        Assert.Contains("GracefulTerminateTimeout", failures);
    }

    [Fact]
    public void Real_SingleValidFirm_Succeeds()
    {
        Assert.True(Sut().Validate(null, Real(ValidFirm())).Succeeded);
    }

    [Fact]
    public void Real_DuplicateFirmId_Fails()
    {
        var result = Sut().Validate(null, Real(ValidFirm("DUP"), ValidFirm("DUP")));
        Assert.False(result.Succeeded);
        Assert.Contains("duplicated", string.Join(";", result.Failures!));
    }

    [Fact]
    public void Real_MissingFirmId_Fails()
    {
        var f = ValidFirm();
        f.FirmId = "";
        var r = Sut().Validate(null, Real(f));
        Assert.False(r.Succeeded);
        Assert.Contains("FirmId is required", string.Join(";", r.Failures!));
    }

    [Theory]
    [InlineData("")]
    [InlineData("nohostport")]
    [InlineData("host:notaport")]
    [InlineData("host:0")]
    [InlineData("host:99999")]
    [InlineData(":9000")]
    public void Real_InvalidEndpoint_Fails(string endpoint)
    {
        var f = ValidFirm();
        f.Endpoint = endpoint;
        Assert.False(Sut().Validate(null, Real(f)).Succeeded);
    }

    [Fact]
    public void Real_ZeroSessionVerId_Fails()
    {
        var f = ValidFirm();
        f.SessionVerId = 0;
        var r = Sut().Validate(null, Real(f));
        Assert.False(r.Succeeded);
        Assert.Contains("SessionVerId", string.Join(";", r.Failures!));
    }

    [Fact]
    public void Real_ZeroSessionId_Fails()
    {
        var f = ValidFirm();
        f.SessionId = 0;
        var r = Sut().Validate(null, Real(f));
        Assert.False(r.Succeeded);
        Assert.Contains("SessionId", string.Join(";", r.Failures!));
    }

    [Fact]
    public void Real_ZeroEnteringFirm_Fails()
    {
        var f = ValidFirm();
        f.EnteringFirm = 0;
        var r = Sut().Validate(null, Real(f));
        Assert.False(r.Succeeded);
        Assert.Contains("EnteringFirm", string.Join(";", r.Failures!));
    }

    [Fact]
    public void Real_MissingAccessKey_Fails()
    {
        var f = ValidFirm();
        f.AccessKey = "";
        var r = Sut().Validate(null, Real(f));
        Assert.False(r.Succeeded);
        Assert.Contains("AccessKey", string.Join(";", r.Failures!));
    }

    [Theory]
    [InlineData("")]
    [InlineData("12345678901")] // 11 chars
    public void Real_InvalidSenderLocation_Fails(string sl)
    {
        var f = ValidFirm();
        f.SenderLocation = sl;
        Assert.False(Sut().Validate(null, Real(f)).Succeeded);
    }

    [Theory]
    [InlineData("")]
    [InlineData("ABCDEF")] // 6 chars
    public void Real_InvalidEnteringTrader_Fails(string et)
    {
        var f = ValidFirm();
        f.EnteringTrader = et;
        Assert.False(Sut().Validate(null, Real(f)).Succeeded);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(50u)]
    [InlineData(60_001u)]
    public void Real_KeepAliveOutOfRange_Fails(uint ka)
    {
        var f = ValidFirm();
        f.KeepAliveIntervalMs = ka;
        Assert.False(Sut().Validate(null, Real(f)).Succeeded);
    }

    [Fact]
    public void Real_AllFailuresAggregated()
    {
        var f = new FirmConfig(); // entirely empty
        var r = Sut().Validate(null, Real(f));
        Assert.False(r.Succeeded);
        // Should report multiple distinct failures, not just the first one.
        Assert.True(r.Failures!.Count() >= 5,
            $"Expected aggregate of multiple failures, got: {string.Join(" | ", r.Failures!)}");
    }

    // #163: AllowErInjection only valid alongside Mock — the SimulatorEndpoint
    // depends on MockEntryPointClient which is not registered in any other
    // mode, so silently mapping the route while DI can't resolve the impl
    // would lead to a confusing 500-on-first-request instead of a fail-fast
    // boot. Validator catches the misconfig at startup.

    [Fact]
    public void AllowErInjection_True_With_Mock_Succeeds()
    {
        var opts = new ExchangeOptions { Mode = ExchangeMode.Mock, AllowErInjection = true };
        Assert.True(Sut().Validate(null, opts).Succeeded);
    }

    [Theory]
    [InlineData(ExchangeMode.Real)]
    [InlineData(ExchangeMode.Stub)]
    [InlineData(ExchangeMode.Unavailable)]
    public void AllowErInjection_True_With_NonMock_Fails(ExchangeMode mode)
    {
        var opts = new ExchangeOptions { Mode = mode, AllowErInjection = true };
        var r = Sut().Validate(null, opts);
        Assert.False(r.Succeeded);
        Assert.Contains(r.Failures!, f => f.Contains("AllowErInjection", StringComparison.Ordinal));
    }

    [Fact]
    public void AllowErInjectionInProduction_True_Without_AllowErInjection_Fails()
    {
        var opts = new ExchangeOptions
        {
            Mode = ExchangeMode.Mock,
            AllowErInjection = false,
            AllowErInjectionInProduction = true,
        };
        var r = Sut().Validate(null, opts);
        Assert.False(r.Succeeded);
        Assert.Contains(r.Failures!, f => f.Contains("meaningless", StringComparison.OrdinalIgnoreCase));
    }
}
