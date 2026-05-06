using B3.Trading.Application;

namespace B3.Trading.Application.Tests;

/// <summary>
/// Coverage for #117: detection of server-side Self-Trade Prevention
/// reasons emitted by the B3 EntryPoint matching engine.
/// </summary>
public class NativeStpDetectorTests
{
    [Theory]
    [InlineData("SelfTradingPrevention")]
    [InlineData("CancelRestingOrderOnSelfTrade")]
    public void Recognises_KnownStpReasons(string reason)
    {
        Assert.True(NativeStpDetector.IsNativeStpReason(reason));
    }

    [Theory]
    [InlineData("selftradingprevention")]
    [InlineData("CANCELRESTINGORDERONSELFTRADE")]
    [InlineData("  SelfTradingPrevention  ")]
    public void Recognises_KnownStpReasons_CaseAndWhitespaceInsensitive(string reason)
    {
        Assert.True(NativeStpDetector.IsNativeStpReason(reason));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("RiskManagementCancellation")]
    [InlineData("CancelOnDisconnect")]
    [InlineData("self_trade_prevention: would cross own working Sell 100@30")]
    public void Rejects_OtherReasons(string? reason)
    {
        Assert.False(NativeStpDetector.IsNativeStpReason(reason));
    }
}
