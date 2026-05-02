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
}
