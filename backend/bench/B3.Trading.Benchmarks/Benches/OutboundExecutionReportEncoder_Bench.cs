using BenchmarkDotNet.Attributes;

using B3.Trading.Application;
using B3.Trading.Domain;
using B3.Trading.EntryPointListener.Hosting;

namespace B3.Trading.Benchmarks.Benches;

/// <summary>
/// RFC §7.1 — baseline for the SBE outbound encode that F5 targets.
/// Encodes one <see cref="ExecutionEvent"/> per invocation per ExecKind
/// to keep coverage symmetric with the production path that switches on
/// <see cref="ExecKind"/>. The encoder is currently a static helper that
/// returns a heap-allocated byte[]; the post-fix variant is expected to
/// pool buffers and shrink Gen0 collections by ≥80%.
///
/// <para>Encoder type is <c>internal</c>; this project is granted access
/// via <c>InternalsVisibleTo</c> on
/// <c>B3.Trading.EntryPointListener.csproj</c>.</para>
/// </summary>
[MemoryDiagnoser]
public class OutboundExecutionReportEncoder_Bench
{
    private const ulong ExternalClOrdId = 4242UL;
    private const ulong ExternalOrigClOrdId = 4241UL;

    [Params(ExecKind.New, ExecKind.PartialFill, ExecKind.Canceled, ExecKind.Rejected)]
    public ExecKind Kind { get; set; }

    private ExecutionEvent _ev = null!;

    [GlobalSetup]
    public void Setup()
    {
        _ev = new ExecutionEvent(
            Owner: new EndClientId("u1"),
            ClOrdId: 100UL,
            Symbol: "PETR4",
            Side: OrderSide.Buy,
            Status: Kind == ExecKind.Rejected ? OrderStatus.Rejected : OrderStatus.Working,
            Kind: Kind,
            LeavesQuantity: 100,
            CumulativeQuantity: Kind == ExecKind.PartialFill ? 50 : 0,
            LastQuantity: Kind == ExecKind.PartialFill ? 50 : 0,
            LastPrice: Kind == ExecKind.PartialFill ? 30.50m : 0m,
            RejectReason: Kind == ExecKind.Rejected ? "risk" : null,
            TimestampUtc: DateTimeOffset.UtcNow);
    }

    [Benchmark]
    public byte[] Encode()
        => OutboundExecutionReportEncoder.Encode(_ev, ExternalClOrdId, ExternalOrigClOrdId);
}
