namespace B3.Trading.Application;

/// <summary>
/// Detects whether an ExecutionReport's <c>RejectReason</c> string
/// corresponds to a server-side Self-Trade Prevention restatement
/// emitted by the B3 EntryPoint matching engine.
///
/// <para>
/// Background: <c>B3.EntryPoint.Client.Models.ExecRestatementReason</c>
/// 0.14.3 defines two STP-related codes — <c>SelfTradingPrevention =
/// 103</c> and <c>CancelRestingOrderOnSelfTrade = 107</c>. The gateway
/// (<c>B3EntryPointClientGateway</c>) forwards the enum's
/// <c>ToString()</c> as the <c>RejectReason</c> on cancel envelopes.
/// We text-match here instead of taking a hard SDK type dependency to
/// keep the Application layer free of the wire SDK; if the SDK renames
/// the enum the gateway will drift first and a focused failure here
/// makes the regression obvious.
/// </para>
///
/// <para>
/// Spike + design rationale in #103 / #117: the client SDK does not
/// expose any per-order STP toggle, so the only way to act on
/// server-driven STP is to recognise these reasons after the fact and
/// surface them differently from generic cancels (UI badge,
/// structured logs).
/// </para>
/// </summary>
public static class NativeStpDetector
{
    // String literals match B3.EntryPoint.Client.Models.ExecRestatementReason
    // member names. Comparison is ordinal case-insensitive — defensive
    // against any caller that might Title-Case the reason on its way
    // through the pipeline. Whitespace is trimmed to absorb log/format
    // noise (the gateway emits the raw enum name today, no spaces, but
    // future envelopes that wrap it shouldn't break detection).
    private const string SelfTradingPrevention = "SelfTradingPrevention";
    private const string CancelRestingOrderOnSelfTrade = "CancelRestingOrderOnSelfTrade";

    public static bool IsNativeStpReason(string? rejectReason)
    {
        if (string.IsNullOrWhiteSpace(rejectReason)) return false;
        var trimmed = rejectReason.Trim();
        return string.Equals(trimmed, SelfTradingPrevention, StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmed, CancelRestingOrderOnSelfTrade, StringComparison.OrdinalIgnoreCase);
    }
}
