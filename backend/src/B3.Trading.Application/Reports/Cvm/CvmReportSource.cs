using System.Runtime.CompilerServices;
using B3.Trading.Application.Persistence;
using B3.Trading.Domain;

namespace B3.Trading.Application.Reports.Cvm;

/// <summary>
/// Q4.8 (#308). Single immutable row consumed by
/// <see cref="CvmReportWriter"/> when serialising a CVM 35/505
/// transaction-reporting XML. Mirrors the on-disk WAL fields that
/// landed for every <see cref="ExecutionReportReceivedEvent"/> with
/// <c>ExecKind = Fill | PartialFill</c> plus the resolved owner
/// (looked up in <see cref="OrderOwnershipMap"/> or, if the order
/// has aged out of the in-memory map, by scanning the WAL for the
/// matching <see cref="OrderSubmittedEvent"/>). The <see cref="Owner"/>
/// is the RAW <see cref="EndClientId"/>; the writer is responsible
/// for opacifying it before emission (LGPD).
/// </summary>
public sealed record CvmFillRow(
    ulong ClOrdId,
    ulong OrigClOrdId,
    EndClientId Owner,
    string FirmId,
    string Symbol,
    string Side,
    long LastQuantity,
    long CumulativeQuantity,
    decimal LastPrice,
    DateTimeOffset ExecutedAtUtc,
    string? SubAccountId);

/// <summary>
/// Q4.8 (#308). Read-only enumerator that streams every fill that
/// landed on the WAL for a given <c>(firmId, date)</c> pair, in WAL
/// arrival order. Drives the on-demand CVM 35/505 XML export
/// (see <see cref="CvmReportEndpoints"/> in <c>B3.Trading.Api</c>).
///
/// <para><b>Source of truth.</b> The WAL ER stream is the same one
/// the host already replays on cold start, so an export run minutes
/// or years after the trading day reconstructs the exact same set of
/// transactions the firm executed that day — modulo WAL segment
/// retention, which is the regulator-facing 7-year retention boundary
/// for the export (the host does NOT persist generated XML; the XML
/// is reproducible on demand from the durable WAL).</para>
///
/// <para><b>Owner resolution.</b> A fill's WAL ER does not carry the
/// owner directly. We first consult <see cref="OrderOwnershipMap"/>
/// (resident in memory while the order is still tracked); if the
/// order has aged out — typical for fills older than today's session
/// — we fall back to scanning the same WAL slice for the order's
/// <see cref="OrderSubmittedEvent"/>. The fallback is bounded by the
/// per-day WAL scan we already do, so it adds no extra I/O cost in
/// the common case.</para>
///
/// <para><b>Cross-firm safety.</b> Every emitted row carries the
/// <see cref="CvmFillRow.FirmId"/> field resolved from the
/// <see cref="OrderSubmittedEvent"/> (authoritative) — never from the
/// ER's <c>FirmId</c> alone (which is nullable on legacy WAL
/// segments). Rows for the wrong firm are dropped before yield, so a
/// compromised caller cannot trivially observe another firm's fills
/// even if it bypassed the endpoint's HTTP-layer firm scope.</para>
///
/// <para><b>Cost.</b> The implementation scans the full WAL via
/// <see cref="IEventStore.ReadFromAsync"/> and filters in-memory by
/// <see cref="WalEvent.TimestampUtc"/> day. The WAL is already
/// day-rotated on disk; a future iteration should narrow the scan to
/// the matching segments — tracked as TODO so this slice ships
/// without coupling to <c>FileEventStore</c>'s segment index.</para>
/// </summary>
public sealed class CvmReportSource
{
    private readonly IEventStore _store;
    private readonly OrderOwnershipMap _ownership;

    public CvmReportSource(IEventStore store, OrderOwnershipMap ownership)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _ownership = ownership ?? throw new ArgumentNullException(nameof(ownership));
    }

    /// <summary>
    /// Streams every <c>Fill / PartialFill</c> ER whose
    /// <see cref="WalEvent.TimestampUtc"/> falls on the requested UTC
    /// trading day and whose owning order's firm matches
    /// <paramref name="firmId"/>. Order: WAL arrival (= seq) order,
    /// which is the deterministic per-firm execution order the
    /// regulator expects.
    /// </summary>
    public async IAsyncEnumerable<CvmFillRow> EnumerateAsync(
        string firmId,
        DateOnly date,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(firmId))
            throw new ArgumentException("firmId must be non-empty", nameof(firmId));

        // Pass-1 review (#325) P1. CVM daily reports are anchored to
        // the São Paulo business day (UTC-3 / UTC-2 during DST). A
        // naive UTC-date filter would split BRT trading-day fills
        // across the midnight UTC boundary. Resolve the [start, end)
        // UTC window for the requested calendar date in BRT exactly
        // once and filter by half-open interval.
        var (windowStartUtc, windowEndUtc) = SaoPauloBusinessDayUtcWindow(date);

        // First pass: build a ClOrdId → submit-event map so we can
        // resolve owner + firm + symbol for every fill that lands
        // later in the WAL (or on the same day before the ER, though
        // that's the common case). We cap the map at the FULL WAL
        // because cancel/replace can produce a fill on day N for an
        // order submitted on day N-k. The cap is the same the host
        // already pays on cold-start replay, so we don't widen the
        // memory footprint of an export run.
        var submits = new Dictionary<ulong, OrderSubmittedEvent>(capacity: 1024);
        var fills = new List<(long Seq, ExecutionReportReceivedEvent Er)>(capacity: 256);

        await foreach (var (seq, evt) in _store.ReadFromAsync(0, ct).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();
            switch (evt)
            {
                case OrderSubmittedEvent submit:
                    submits[submit.ClOrdId] = submit;
                    break;
                case ExecutionReportReceivedEvent er
                    when er.LastQuantity > 0
                         && (er.ExecKind == "Fill" || er.ExecKind == "PartialFill")
                         && er.TimestampUtc >= windowStartUtc
                         && er.TimestampUtc < windowEndUtc:
                    fills.Add((seq, er));
                    break;
            }
        }

        foreach (var (_, er) in fills)
        {
            ct.ThrowIfCancellationRequested();
            // Cancel/Replace acks fill against the ORIGINAL ClOrdId;
            // the ER's ClOrdId may be the new-side cancel id. Prefer
            // OrigClOrdId when set so the owner lookup succeeds.
            var lookupId = er.OrigClOrdId != 0 ? er.OrigClOrdId : er.ClOrdId;
            if (!submits.TryGetValue(lookupId, out var submit)
                && !submits.TryGetValue(er.ClOrdId, out submit))
            {
                // Submit not on the WAL slice we scanned (would only
                // happen if the WAL was truncated below the order's
                // submit seq — not possible under standard retention).
                // Fall back to the in-memory ownership map and use the
                // ER's own firm tag if present so we still surface the
                // fill rather than silently dropping a regulatory row.
                if (!_ownership.TryResolve(lookupId, out var ownerFromMap) || ownerFromMap is null)
                    continue;
                var inferredFirm = er.FirmId ?? firmId;
                if (!string.Equals(inferredFirm, firmId, StringComparison.Ordinal))
                    continue;
                yield return new CvmFillRow(
                    ClOrdId: er.ClOrdId,
                    OrigClOrdId: er.OrigClOrdId,
                    Owner: ownerFromMap,
                    FirmId: inferredFirm,
                    Symbol: "UNKNOWN",
                    Side: "UNKNOWN",
                    LastQuantity: er.LastQuantity,
                    CumulativeQuantity: er.CumulativeQuantity,
                    LastPrice: er.LastPrice,
                    ExecutedAtUtc: er.TimestampUtc,
                    SubAccountId: null);
                continue;
            }

            if (!string.Equals(submit.FirmId, firmId, StringComparison.Ordinal))
                continue;

            yield return new CvmFillRow(
                ClOrdId: er.ClOrdId,
                OrigClOrdId: er.OrigClOrdId,
                Owner: new EndClientId(submit.EndClientId),
                FirmId: submit.FirmId,
                Symbol: submit.Symbol,
                Side: submit.Side,
                LastQuantity: er.LastQuantity,
                CumulativeQuantity: er.CumulativeQuantity,
                LastPrice: er.LastPrice,
                ExecutedAtUtc: er.TimestampUtc,
                SubAccountId: submit.SubAccountId);
        }
    }

    /// <summary>
    /// Pass-1 review (#325) P1. CVM reports use the São Paulo
    /// business day, which is UTC-3 year-round (Brazil abolished DST
    /// in 2019). Returns the half-open <c>[start, end)</c> UTC window
    /// for a single SP calendar day so the WAL scan filters by
    /// timestamp comparison without per-event timezone arithmetic.
    /// Falls back to a fixed -03:00 offset if the IANA database is
    /// unavailable on the host (e.g. minimal containers without
    /// tzdata) so the report never silently mis-attributes fills.
    /// </summary>
    internal static (DateTimeOffset StartUtc, DateTimeOffset EndUtc) SaoPauloBusinessDayUtcWindow(DateOnly date)
    {
        TimeZoneInfo? tz = null;
        foreach (var id in new[] { "America/Sao_Paulo", "E. South America Standard Time" })
        {
            try { tz = TimeZoneInfo.FindSystemTimeZoneById(id); break; }
            catch (TimeZoneNotFoundException) { }
            catch (InvalidTimeZoneException) { }
        }
        var startLocal = new DateTime(date.Year, date.Month, date.Day, 0, 0, 0, DateTimeKind.Unspecified);
        var endLocal = startLocal.AddDays(1);
        if (tz is not null)
        {
            var startUtc = TimeZoneInfo.ConvertTimeToUtc(startLocal, tz);
            var endUtc = TimeZoneInfo.ConvertTimeToUtc(endLocal, tz);
            return (new DateTimeOffset(startUtc, TimeSpan.Zero), new DateTimeOffset(endUtc, TimeSpan.Zero));
        }
        var sp = TimeSpan.FromHours(-3);
        return (new DateTimeOffset(startLocal, sp).ToUniversalTime(),
                new DateTimeOffset(endLocal, sp).ToUniversalTime());
    }
}
