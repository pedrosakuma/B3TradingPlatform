namespace B3.Trading.Application.Risk.Checks;

/// <summary>
/// Pre-trade gate that rejects any submission for a symbol marked
/// halted via <see cref="SymbolHaltService"/>. Pipeline order=10
/// (right after the kill-switch) so a halted-symbol submission fails
/// before any of the per-instrument or per-end-client work runs —
/// the cheapest possible reject for a control that's binary.
///
/// <para>Defaults are conservative: the halted set is empty on a
/// fresh process; halts survive process restart via WAL +
/// snapshot. Resume requires an explicit admin call.</para>
/// </summary>
public sealed class SymbolHaltedCheck : IRiskCheck
{
    private readonly SymbolHaltService _halts;
    public SymbolHaltedCheck(SymbolHaltService halts) => _halts = halts;

    public int Order => 10;
    public string Name => "symbol_halted";

    public RiskDecision Check(RiskContext ctx)
    {
        if (_halts.IsHalted(ctx.Symbol))
            return RiskDecision.Reject($"symbol '{ctx.Symbol}' trading halted");
        return RiskDecision.Approve;
    }
}
