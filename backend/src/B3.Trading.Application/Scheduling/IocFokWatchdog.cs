using System.Collections.Concurrent;
using B3.Trading.Application.Observability;
using B3.Trading.Application.Persistence;
using B3.Trading.Application.Risk;
using B3.Trading.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace B3.Trading.Application.Scheduling;

/// <summary>
/// #351 — Defensive watchdog for <see cref="TimeInForce.IOC"/> and
/// <see cref="TimeInForce.FOK"/> orders. Upstream matching can silently
/// drop an IOC/FOK aggressor that finds no liquidity (no
/// <c>ExecutionReport</c> ever returns; see
/// <c>B3MatchingPlatform#357</c>). If that happens the order sits in
/// <see cref="WorkingOrderBook"/> forever, the trader-UI shows it as
/// live, and the margin reservation is never released.
///
/// <para>
/// This watchdog arms a one-shot timer on every IOC/FOK submit. If a
/// terminal <c>ExecutionReport</c> arrives before the timer fires the
/// entry is canceled and the order follows the normal lifecycle. If the
/// timer fires first, the watchdog dispatches a synthetic
/// <see cref="ExecKind.Canceled"/> event with a deterministic reject
/// reason so the order is retired from the book, the margin reservation
/// is released, and downstream sinks (WS, drop-copy, executions log)
/// see a terminal frame. Both branches are idempotent.
/// </para>
///
/// <para>
/// The watchdog is purely defensive. Once upstream emits a proper
/// terminal ER on no-liquidity IOC the timer simply never fires (the
/// terminal ER eviction wins) and the metric stays at zero. The metric
/// is therefore also a regression detector — any non-zero rate after a
/// matching-image bump points at a recurrence of the upstream bug.
/// </para>
/// </summary>
public sealed class IocFokWatchdog : IDisposable
{
    public const string SyntheticCancelReason = "gateway_no_response_ioc";

    private readonly WorkingOrderBook _book;
    private readonly EventDispatcher _dispatcher;
    private readonly IExecutionEventSink _sink;
    private readonly IMarginProvider _margin;
    private readonly TimeProvider _clock;
    private readonly IOptionsMonitor<IocFokWatchdogOptions> _options;
    private readonly ILogger<IocFokWatchdog>? _logger;

    private readonly ConcurrentDictionary<ulong, ITimer> _active = new();
    private int _disposed;

    public IocFokWatchdog(
        WorkingOrderBook book,
        EventDispatcher dispatcher,
        IExecutionEventSink sink,
        IMarginProvider margin,
        IOptionsMonitor<IocFokWatchdogOptions> options,
        TimeProvider? clock = null,
        ILogger<IocFokWatchdog>? logger = null)
    {
        _book = book ?? throw new ArgumentNullException(nameof(book));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _sink = sink ?? new NoOpExecutionEventSink();
        _margin = margin ?? throw new ArgumentNullException(nameof(margin));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _clock = clock ?? TimeProvider.System;
        _logger = logger;
    }

    /// <summary>
    /// Diagnostic surface for tests.
    /// </summary>
    public int TrackedCount => _active.Count;

    /// <summary>
    /// Called from <c>OrderSubmissionService</c> after the gateway
    /// submit returns. No-op for non IOC/FOK orders, for terminal
    /// orders (e.g. synchronously rejected), and when the watchdog is
    /// disabled via configuration so callers don't have to gate.
    /// </summary>
    public void Register(Order order)
    {
        ArgumentNullException.ThrowIfNull(order);
        if (Volatile.Read(ref _disposed) != 0) return;
        if (order.TimeInForce is not (TimeInForce.IOC or TimeInForce.FOK)) return;
        if (IsTerminal(order.Status)) return;

        var opts = _options.CurrentValue;
        if (!opts.Enabled) return;
        var timeout = TimeSpan.FromMilliseconds(Math.Max(1, opts.TimeoutMs));

        var clOrdId = order.ClOrdId;
        // One-shot timer. We intentionally allocate a fresh timer per
        // order rather than sharing a min-heap: IOC/FOK terminates
        // within a few ms in the happy path so the live set is
        // typically tiny, and a per-order timer keeps the Fire path
        // lock-free.
        var timer = _clock.CreateTimer(
            static state => ((Entry)state!).Self.Fire(((Entry)state!).ClOrdId),
            new Entry(clOrdId, this),
            timeout,
            Timeout.InfiniteTimeSpan);

        // Replace-and-dispose pattern: a re-register for the same
        // clOrdId (currently impossible — ClOrdIDs are unique — but
        // defensive against future replays) supersedes the previous
        // timer so we never leak.
        if (_active.TryGetValue(clOrdId, out var existing))
        {
            _active[clOrdId] = timer;
            existing.Dispose();
        }
        else
        {
            _active[clOrdId] = timer;
        }
    }

    /// <summary>
    /// Called from <c>ExecutionReportProcessor</c> whenever an order
    /// reaches a terminal status. Disposes the pending watchdog timer
    /// (if any). Cheap no-op when nothing is tracked for the id.
    /// </summary>
    public void OnOrderTerminal(ulong clOrdId)
    {
        if (_active.TryRemove(clOrdId, out var timer))
        {
            timer.Dispose();
        }
    }

    private void Fire(ulong clOrdId)
    {
        // Atomic claim: if a concurrent OnOrderTerminal already won we
        // observe the entry as gone and bail. Without the claim the
        // synthetic Cancel could race the real terminal ER and emit a
        // spurious event after the order already terminalised.
        if (!_active.TryRemove(clOrdId, out var timer))
        {
            return;
        }
        timer.Dispose();

        if (!_book.TryGet(clOrdId, out var orderRef) || orderRef is null)
        {
            // Order vanished before we could act — nothing to do. Late
            // ER eviction or test teardown.
            return;
        }
        var order = orderRef;
        if (IsTerminal(order.Status))
        {
            // Terminal ER arrived in the gap between the timer firing
            // and our claim being processed — happy outcome.
            return;
        }

        var firmTag = new KeyValuePair<string, object?>("firmId", order.FirmId);
        var symbolTag = new KeyValuePair<string, object?>("symbol", order.Symbol);
        var tifTag = new KeyValuePair<string, object?>("tif", order.TimeInForce.ToString());
        MetricsRegistry.OrdersIocNoResponse.Add(1, firmTag, symbolTag, tifTag);

        _logger?.LogWarning(
            "event=ioc.watchdog.fired clOrdId={ClOrdId} firmId={FirmId} symbol={Symbol} tif={Tif} " +
            "owner={Owner} reason={Reason}; synthesising terminal Cancel.",
            clOrdId, order.FirmId, order.Symbol, order.TimeInForce, order.Owner.Value, SyntheticCancelReason);

        try
        {
            _dispatcher.Dispatch(
                new ExecutionReportReceivedEvent
                {
                    ClOrdId = clOrdId,
                    ExecKind = ExecKind.Canceled.ToString(),
                    LeavesQuantity = order.LeavesQuantity,
                    CumulativeQuantity = order.CumulativeQuantity,
                    LastQuantity = 0,
                    LastPrice = 0m,
                    RejectReason = SyntheticCancelReason,
                    Synthetic = true,
                },
                () => ApplySyntheticCancel(order));
        }
        catch (WalBackpressureException)
        {
            // WAL is jammed — still mark the order terminal locally so
            // the trader-UI and margin reservation don't hang. The
            // event is lost from the audit trail but the in-memory
            // state recovers; mirrors the PublishSyntheticRejection
            // fallback in OrderSubmissionService.
            ApplySyntheticCancel(order);
        }
    }

    private void ApplySyntheticCancel(Order order)
    {
        order.MarkCancelled();
        _margin.OnExecution(order.ClOrdId, ExecKind.Canceled, 0);
        _sink.Publish(new ExecutionEvent(
            order.Owner, order.ClOrdId, order.Symbol, order.Side, order.Status,
            ExecKind.Canceled,
            order.LeavesQuantity, order.CumulativeQuantity, 0, 0m,
            SyntheticCancelReason, _clock.GetUtcNow(),
            IsNativeStp: false, FirmId: order.FirmId));
    }

    private static bool IsTerminal(OrderStatus status) =>
        status is OrderStatus.Filled or OrderStatus.Cancelled
            or OrderStatus.Rejected or OrderStatus.Replaced;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        foreach (var kv in _active)
        {
            if (_active.TryRemove(kv.Key, out var t)) t.Dispose();
        }
    }

    private sealed record Entry(ulong ClOrdId, IocFokWatchdog Self);
}

/// <summary>
/// Tuning surface for the IOC/FOK watchdog (#351).
/// </summary>
public sealed class IocFokWatchdogOptions
{
    public const string SectionName = "Trading:IocFokWatchdog";

    /// <summary>
    /// Master switch. Defaults to <c>true</c> so the safeguard is
    /// active by default; can be disabled (e.g. in single-process
    /// tests that don't exercise the gateway) without recompiling.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Watchdog timeout in milliseconds. Should be set well above
    /// normal venue RTT (sub-millisecond on the in-process matching,
    /// single-digit ms across the real matching) but tight enough
    /// that traders notice the failure within one human reaction.
    /// Default <c>500</c> ms.
    /// </summary>
    public int TimeoutMs { get; set; } = 500;
}
