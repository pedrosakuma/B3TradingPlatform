using BenchmarkDotNet.Attributes;

using B3.Trading.Application;
using B3.Trading.Application.UserBots;
using B3.Trading.Domain;
using B3.Trading.EntryPointListener.Hosting;

namespace B3.Trading.Benchmarks.Benches;

/// <summary>
/// RFC §7.1 — baseline for the SBE outbound encode that F5 targets.
/// Encodes one <see cref="ExecutionEvent"/> per invocation per ExecKind
/// to keep coverage symmetric with the production path that switches on
/// <see cref="ExecKind"/>. Post P7 / F5 (issue #201) the encoder rents
/// from <see cref="System.Buffers.MemoryPool{T}.Shared"/> and returns
/// an <see cref="OutboundFrame"/>; this bench releases the pooled
/// owner per iteration to keep memory bounded and to reflect the
/// production path where <c>BotOutboundBuffer</c> would dispose on the
/// acked-watermark eviction.
///
/// <para>Encoder type is <c>internal</c>; this project is granted access
/// via <c>InternalsVisibleTo</c> on
/// <c>B3.Trading.EntryPointListener.csproj</c> and
/// <c>B3.Trading.Application.csproj</c> (the latter exposes the
/// <c>OutboundFrame.DisposeOwner</c> hook used here to drain the
/// rented owner — production code never calls it).</para>
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
    public int Encode()
    {
        var frame = OutboundExecutionReportEncoder.Encode(_ev, ExternalClOrdId, ExternalOrigClOrdId);
        var len = frame.Bytes.Length;
        // Production path: BotOutboundBuffer.Append takes ownership and
        // disposes on EvictUpTo. Here we mirror that lifecycle so the
        // pool actually sees a return on every iteration — otherwise
        // BenchmarkDotNet would observe pool growth as "allocations"
        // and the F5 acceptance gate (Gen0 −80% / alloc −95%) would
        // be drowned in pool warmup noise.
        frame.DisposeOwner();
        return len;
    }
}
