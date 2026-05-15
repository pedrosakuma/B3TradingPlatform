using System.Security.Claims;
using System.Text;
using System.Text.Json;
using B3.Trading.Api.Auth;
using B3.Trading.Application;
using B3.Trading.Application.Persistence;
using B3.Trading.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace B3.Trading.Api;

/// <summary>
/// Q2.1 (#268). Trader-scoped historical queries projected on demand
/// from the WAL:
/// <list type="bullet">
///   <item><c>GET /orders/history</c> — one entry per ClOrdID, materialised
///   from the order's submit + ER stream; includes terminals.</item>
///   <item><c>GET /executions/history</c> — one entry per ExecutionReport
///   the platform routed; covers every <see cref="ExecKind"/>.</item>
/// </list>
///
/// <para>
/// Pagination is cursor-based. The cursor is an opaque base64 of a
/// <c>{"seq":...,"ts":...}</c> JSON object; clients must treat it as a
/// black-box token. Default page size is 100 with a hard cap of 500
/// (over-cap requests are clamped, not rejected). Date filter
/// <c>[from,to]</c> is in UTC and applied to the WAL event timestamp;
/// optional <c>symbol</c> matches the exchange ticker exactly.
/// </para>
///
/// <para>
/// <b>Implementation note.</b> The current projection walks the WAL
/// from genesis on every request and materialises in memory before
/// applying the requested page. That is O(N) in the WAL length and is
/// acceptable for participant-side volumes (RFC §4.2 — single-firm,
/// ≤30k events/day) but will need to swap for an indexed read once
/// retention grows. The cursor envelope is intentionally future-proof:
/// <c>{seq,ts}</c> is enough to anchor a binary search via the segment
/// index, so an indexed implementation can ship without a wire-shape
/// change. TODO(history-index): indexed reader keyed by
/// <c>(endclient, ts)</c>, tracked alongside the EOD materialiser.
/// </para>
/// </summary>
public static class HistoryEndpoints
{
    /// <summary>Default page size when the caller omits <c>limit</c>.</summary>
    public const int DefaultLimit = 100;

    /// <summary>Hard cap on page size. Larger values are clamped, not rejected.</summary>
    public const int MaxLimit = 500;

    public static IEndpointRouteBuilder MapHistory(this IEndpointRouteBuilder app)
    {
        app.MapGet("/orders/history", async (
            HttpContext ctx,
            EndClientRegistry registry,
            IEventStore store,
            string? from,
            string? to,
            string? cursor,
            int? limit,
            string? symbol,
            CancellationToken ct) =>
        {
            if (!TryParseRange(from, to, out var fromTs, out var toTs, out var rangeError))
                return Results.BadRequest(new { error = rangeError });
            if (!TryParseCursor(cursor, out var cursorState, out var cursorError))
                return Results.BadRequest(new { error = cursorError });

            var owner = ResolveOwner(ctx, registry);
            var pageSize = ClampLimit(limit);

            // Freeze the read view across the entire pagination walk.
            // Order projections are mutable: an order returned on page 1
            // can have its LastSeq advance (e.g. a new ER) before page 2
            // is fetched, sliding it past the cursor anchor and silently
            // dropping it from the result. Capturing snapshotSeq once on
            // the first request and threading it through every subsequent
            // page guarantees a stable, repeatable pagination — any WAL
            // record with seq > snapshotSeq is invisible to the walk.
            //
            // Executions don't need this because their projection rows
            // are immutable per-seq: a new ER can only land at a higher
            // seq, and ApplyCursorAndPage already excludes seq >= cursor,
            // so newcomers naturally fall outside the in-progress walk.
            long snapshotSeq;
            if (cursorState is { SnapshotSeq: > 0 })
            {
                snapshotSeq = cursorState.SnapshotSeq;
            }
            else
            {
                await store.FlushAsync(ct);
                snapshotSeq = store.CurrentSeq;
            }

            var orders = await ProjectOrdersAsync(store, owner.Value, symbol, fromTs, toTs, snapshotSeq, ct);
            // Sort newest-first by the order's last touching seq (cursor anchor).
            orders.Sort(static (a, b) => b.LastSeq.CompareTo(a.LastSeq));

            var page = ApplyCursorAndPage(orders, cursorState, pageSize, snapshotSeq, static x => (x.LastSeq, x.LastTs));
            var items = new List<OrderHistoryItemDto>(page.Items.Count);
            foreach (var p in page.Items) items.Add(p.ToDto());

            return Results.Ok(new HistoryPageDto<OrderHistoryItemDto>(items, page.NextCursor));
        }).RequireAuthorization();

        app.MapGet("/executions/history", async (
            HttpContext ctx,
            EndClientRegistry registry,
            IEventStore store,
            string? from,
            string? to,
            string? cursor,
            int? limit,
            string? symbol,
            CancellationToken ct) =>
        {
            if (!TryParseRange(from, to, out var fromTs, out var toTs, out var rangeError))
                return Results.BadRequest(new { error = rangeError });
            if (!TryParseCursor(cursor, out var cursorState, out var cursorError))
                return Results.BadRequest(new { error = cursorError });

            var owner = ResolveOwner(ctx, registry);
            var pageSize = ClampLimit(limit);

            var executions = await ProjectExecutionsAsync(store, owner.Value, symbol, fromTs, toTs, ct);
            executions.Sort(static (a, b) => b.Seq.CompareTo(a.Seq));

            var page = ApplyCursorAndPage(executions, cursorState, pageSize, snapshotSeq: 0, static x => (x.Seq, x.TimestampUtc));
            var items = new List<ExecutionHistoryItemDto>(page.Items.Count);
            foreach (var e in page.Items) items.Add(e.ToDto());

            return Results.Ok(new HistoryPageDto<ExecutionHistoryItemDto>(items, page.NextCursor));
        }).RequireAuthorization();

        return app;
    }

    // -----------------------------------------------------------------
    // Projection: orders
    // -----------------------------------------------------------------

    private static async Task<List<OrderProjection>> ProjectOrdersAsync(
        IEventStore store, string owner, string? symbol,
        DateTimeOffset from, DateTimeOffset to, long snapshotSeq, CancellationToken ct)
    {
        // The endpoint already drained the writer + captured snapshotSeq
        // for first-page requests so the read view is frozen for the
        // entire pagination walk. On subsequent pages the snapshotSeq
        // comes from the cursor; the on-disk WAL is at least as fresh as
        // when we captured it, but anything appended past snapshotSeq is
        // explicitly ignored below.
        var byClOrdId = new Dictionary<ulong, OrderProjection>();
        // Side-table: every ClOrdId we have ever seen on the WAL, mapped
        // to its owner + symbol. Needed because ER events do not carry
        // owner/symbol and the cancel-side ClOrdID is invented at cancel
        // time (not submit). Tracking firm-wide is fine — we filter to
        // the requested owner only when materialising the projection.
        var ownerByClOrdId = new Dictionary<ulong, (string Owner, string Symbol)>();
        // Tracks which projections have at least one event in [from,to].
        // We must apply ALL events with ts <= to to project the correct
        // state-at-`to` (a partial fill at 10:00 followed by a full fill
        // at 12:00 with to=11:00 must surface the partial — ignoring
        // post-`to` events keeps the result a slice, not a final snapshot).
        // Pre-`from` events are still applied so the seed state is right;
        // we just don't flag them as in-window.
        var hadEventInWindow = new HashSet<ulong>();

        await foreach (var (seq, evt) in store.ReadFromAsync(0, ct))
        {
            // Snapshot fence — see endpoint comment. snapshotSeq==0 means
            // the caller did not capture one (only the executions path
            // takes that branch and never calls this method).
            if (snapshotSeq > 0 && seq > snapshotSeq) break;
            // Future events relative to the requested window must NOT
            // mutate state — they would over-advance the projection past
            // the slice the caller asked for.
            if (evt.TimestampUtc > to) continue;
            var inWindow = evt.TimestampUtc >= from;

            switch (evt)
            {
                case OrderSubmittedEvent o:
                    ownerByClOrdId[o.ClOrdId] = (o.EndClientId, o.Symbol);
                    if (!OwnerMatches(o.EndClientId, owner)) break;
                    if (symbol is not null && !o.Symbol.Equals(symbol, StringComparison.Ordinal)) break;
                    byClOrdId[o.ClOrdId] = OrderProjection.FromSubmit(seq, o);
                    if (inWindow) hadEventInWindow.Add(o.ClOrdId);
                    break;

                case OrderReplaceRequestedEvent rr:
                    // The replacement is itself a brand-new ClOrdID and
                    // becomes its own row in the history (matches the
                    // live ExecutionReportProcessor.HandleReplaced semantics
                    // where the new ID is a separate Order).
                    ownerByClOrdId[rr.NewClOrdId] = (rr.EndClientId, rr.Symbol);
                    if (!OwnerMatches(rr.EndClientId, owner)) break;
                    if (symbol is not null && !rr.Symbol.Equals(symbol, StringComparison.Ordinal)) break;
                    byClOrdId[rr.NewClOrdId] = OrderProjection.FromReplace(seq, rr);
                    if (inWindow) hadEventInWindow.Add(rr.NewClOrdId);
                    break;

                case ExecutionReportReceivedEvent er:
                    Enum.TryParse<ExecKind>(er.ExecKind, ignoreCase: true, out var kind);
                    if (kind == ExecKind.Replaced && er.OrigClOrdId != 0)
                    {
                        // Mirror ExecutionReportProcessor.ApplyReplaceAccepted
                        // + Order.HydrateReplacement: the original goes
                        // terminal (Replaced) and the new ClOrdID is
                        // hydrated from the ER's leaves/cum baseline so
                        // subsequent fill ERs (which arrive under the
                        // new ID with OrigClOrdId=0) advance from the
                        // correct starting point. Without this the new
                        // row would stay at PendingNew forever — the
                        // venue never issues a separate New ER for the
                        // replacement.
                        if (byClOrdId.TryGetValue(er.OrigClOrdId, out var origProj))
                        {
                            origProj.ApplyReplacedTerminal(seq, er);
                            if (inWindow) hadEventInWindow.Add(er.OrigClOrdId);
                        }
                        if (byClOrdId.TryGetValue(er.ClOrdId, out var newProj))
                        {
                            newProj.HydrateFromReplaceEr(seq, er);
                            if (inWindow) hadEventInWindow.Add(er.ClOrdId);
                        }
                        break;
                    }

                    // Non-Replaced ER: may target either ClOrdId directly
                    // (New, Fill, etc.) or via OrigClOrdId (cancel ack
                    // lands on the cancel-side ID — never carried by an
                    // OrderSubmittedEvent — but mutates the original).
                    var targetId = er.ClOrdId;
                    if (er.OrigClOrdId != 0 && byClOrdId.ContainsKey(er.OrigClOrdId))
                        targetId = er.OrigClOrdId;
                    if (byClOrdId.TryGetValue(targetId, out var proj))
                    {
                        proj.ApplyEr(seq, er);
                        if (inWindow) hadEventInWindow.Add(targetId);
                    }
                    break;

                case OrderStaledEvent os:
                    if (byClOrdId.TryGetValue(os.ClOrdId, out var staleProj))
                    {
                        staleProj.ApplyStaled(seq, os);
                        if (inWindow) hadEventInWindow.Add(os.ClOrdId);
                    }
                    break;

                case OrderStaleClearedEvent osc:
                    if (byClOrdId.TryGetValue(osc.ClOrdId, out var clearProj))
                    {
                        clearProj.ApplyStaleCleared(seq, osc);
                        if (inWindow) hadEventInWindow.Add(osc.ClOrdId);
                    }
                    break;
            }
        }

        // Include an order iff at least one of its events fell inside
        // the requested window. State is projected as of `to` (post-`to`
        // events were skipped above), so the surfaced snapshot is the
        // order's state at the end of the window — even when the order's
        // most recent event predates `from`, as long as something inside
        // [from,to] touched it.
        var result = new List<OrderProjection>(byClOrdId.Count);
        foreach (var p in byClOrdId.Values)
        {
            if (!hadEventInWindow.Contains(p.ClOrdId)) continue;
            result.Add(p);
        }
        return result;
    }

    // -----------------------------------------------------------------
    // Projection: executions
    // -----------------------------------------------------------------

    private static async Task<List<ExecutionProjection>> ProjectExecutionsAsync(
        IEventStore store, string owner, string? symbol, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        // See ProjectOrdersAsync: drain the writer so the read-side picks
        // up everything appended before the request.
        await store.FlushAsync(ct);
        // Same side-table rationale as ProjectOrdersAsync: ER carries no
        // owner/symbol so we backfill from the prior submit/replace.
        var ownerByClOrdId = new Dictionary<ulong, (string Owner, string Symbol, string Side)>();
        var result = new List<ExecutionProjection>();

        await foreach (var (seq, evt) in store.ReadFromAsync(0, ct))
        {
            switch (evt)
            {
                case OrderSubmittedEvent o:
                    ownerByClOrdId[o.ClOrdId] = (o.EndClientId, o.Symbol, o.Side);
                    break;
                case OrderReplaceRequestedEvent rr:
                    ownerByClOrdId[rr.NewClOrdId] = (rr.EndClientId, rr.Symbol, rr.Side);
                    break;
                case ExecutionReportReceivedEvent er:
                    if (er.TimestampUtc < from || er.TimestampUtc > to) break;
                    // Owner resolution falls back to OrigClOrdId for cancel/
                    // replace acks where the cancel-side ID was never carried
                    // by an OrderSubmittedEvent.
                    (string Owner, string Symbol, string Side)? meta = null;
                    if (ownerByClOrdId.TryGetValue(er.ClOrdId, out var m1)) meta = m1;
                    else if (er.OrigClOrdId != 0 && ownerByClOrdId.TryGetValue(er.OrigClOrdId, out var m2)) meta = m2;
                    if (meta is null) break;
                    if (!OwnerMatches(meta.Value.Owner, owner)) break;
                    if (symbol is not null && !meta.Value.Symbol.Equals(symbol, StringComparison.Ordinal)) break;
                    result.Add(new ExecutionProjection(
                        Seq: seq,
                        ClOrdId: er.ClOrdId,
                        Symbol: meta.Value.Symbol,
                        Side: meta.Value.Side,
                        Kind: er.ExecKind,
                        LeavesQuantity: er.LeavesQuantity,
                        CumulativeQuantity: er.CumulativeQuantity,
                        LastQuantity: er.LastQuantity,
                        LastPrice: er.LastPrice,
                        RejectReason: er.RejectReason,
                        TimestampUtc: er.TimestampUtc));
                    break;
            }
        }

        return result;
    }

    // -----------------------------------------------------------------
    // Cursor + paging helpers
    // -----------------------------------------------------------------

    private static PageResult<T> ApplyCursorAndPage<T>(
        List<T> sortedDesc, CursorState? cursor, int pageSize, long snapshotSeq,
        Func<T, (long Seq, DateTimeOffset Ts)> anchor)
    {
        // Items are sorted seq-DESC. The cursor anchors the LAST item we
        // returned previously; the next page strictly precedes it.
        IEnumerable<T> windowed = sortedDesc;
        if (cursor is { } c)
        {
            windowed = sortedDesc.Where(x =>
            {
                var a = anchor(x);
                return a.Seq < c.Seq;
            });
        }

        var taken = new List<T>(pageSize);
        T? last = default;
        var hasMore = false;
        foreach (var x in windowed)
        {
            if (taken.Count == pageSize)
            {
                hasMore = true;
                break;
            }
            taken.Add(x);
            last = x;
        }

        string? next = null;
        if (hasMore && last is not null)
        {
            var a = anchor(last);
            // snapshotSeq travels with the cursor so every page in the
            // walk reads the same frozen view. Executions pass 0 here
            // and ProjectExecutionsAsync ignores the field.
            next = EncodeCursor(new CursorState(a.Seq, a.Ts) { SnapshotSeq = snapshotSeq });
        }
        return new PageResult<T>(taken, next);
    }

    private static int ClampLimit(int? limit)
    {
        if (limit is null || limit <= 0) return DefaultLimit;
        if (limit > MaxLimit) return MaxLimit;
        return limit.Value;
    }

    private static bool TryParseRange(
        string? from, string? to,
        out DateTimeOffset fromTs, out DateTimeOffset toTs, out string? error)
    {
        var nowUtc = DateTimeOffset.UtcNow;
        toTs = nowUtc;
        // Default `from` = today UTC start.
        fromTs = new DateTimeOffset(nowUtc.UtcDateTime.Date, TimeSpan.Zero);
        error = null;

        if (!string.IsNullOrWhiteSpace(from))
        {
            if (!DateTimeOffset.TryParse(from, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                    out var parsed))
            {
                error = $"invalid 'from' timestamp: '{from}' (expected ISO 8601)";
                return false;
            }
            fromTs = parsed;
        }
        if (!string.IsNullOrWhiteSpace(to))
        {
            if (!DateTimeOffset.TryParse(to, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                    out var parsed))
            {
                error = $"invalid 'to' timestamp: '{to}' (expected ISO 8601)";
                return false;
            }
            toTs = parsed;
        }
        if (toTs < fromTs)
        {
            error = "'to' must be greater than or equal to 'from'";
            return false;
        }
        return true;
    }

    private static bool TryParseCursor(string? cursor, out CursorState? state, out string? error)
    {
        state = null;
        error = null;
        if (string.IsNullOrWhiteSpace(cursor)) return true;
        try
        {
            // The cursor wire-format is intentionally URL-safe-tolerant:
            // accept '-_' as well as '+/' so callers do not have to
            // double-encode in query strings.
            var normalised = cursor.Replace('-', '+').Replace('_', '/');
            // Pad to a multiple of 4 — Convert.FromBase64String is strict.
            switch (normalised.Length % 4)
            {
                case 2: normalised += "=="; break;
                case 3: normalised += "="; break;
            }
            var bytes = Convert.FromBase64String(normalised);
            var parsed = JsonSerializer.Deserialize<CursorState>(bytes);
            if (parsed is null || parsed.Seq < 0)
            {
                error = "malformed cursor";
                return false;
            }
            state = parsed;
            return true;
        }
        catch (Exception)
        {
            error = "malformed cursor";
            return false;
        }
    }

    private static string EncodeCursor(CursorState state)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(state);
        return Convert.ToBase64String(bytes);
    }

    private static bool OwnerMatches(string eventOwner, string requestedOwner) =>
        // EndClientRegistry stores ids lowercased; WAL events were appended with the
        // same value, so a case-sensitive ordinal compare is safe and faster.
        string.Equals(eventOwner, requestedOwner, StringComparison.Ordinal);

    private static EndClientId ResolveOwner(HttpContext ctx, EndClientRegistry registry)
    {
        var sub = ctx.User.FindFirstValue(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)
                  ?? throw new InvalidOperationException("Authenticated request missing sub claim.");
        return registry.Register(sub);
    }

    // -----------------------------------------------------------------
    // Internal projection types
    // -----------------------------------------------------------------

    private sealed class OrderProjection
    {
        public ulong ClOrdId;
        public string Symbol = string.Empty;
        public ulong SecurityId;
        public string Side = string.Empty;
        public string Type = string.Empty;
        public long Quantity;
        public decimal? Price;
        public string TimeInForce = nameof(B3.Trading.Domain.TimeInForce.Day);
        public decimal? StopPrice;
        public DateTimeOffset? GoodTillDate;

        public long LeavesQuantity;
        public long CumulativeQuantity;
        public OrderStatus Status = OrderStatus.PendingNew;

        public bool IsStale;
        public string? StaleReason;
        public DateTimeOffset? StaledAtUtc;

        public long FirstSeq;
        public DateTimeOffset CreatedAtUtc;
        public long LastSeq;
        public DateTimeOffset LastTs;

        public static OrderProjection FromSubmit(long seq, OrderSubmittedEvent o) => new()
        {
            ClOrdId = o.ClOrdId,
            Symbol = o.Symbol,
            SecurityId = o.SecurityId,
            Side = o.Side,
            Type = o.Type,
            Quantity = o.Quantity,
            Price = o.Price,
            TimeInForce = o.TimeInForce,
            StopPrice = o.StopPrice,
            GoodTillDate = o.GoodTillDate,
            LeavesQuantity = o.Quantity,
            CumulativeQuantity = 0,
            Status = OrderStatus.PendingNew,
            FirstSeq = seq,
            CreatedAtUtc = o.TimestampUtc,
            LastSeq = seq,
            LastTs = o.TimestampUtc,
        };

        public static OrderProjection FromReplace(long seq, OrderReplaceRequestedEvent rr) => new()
        {
            ClOrdId = rr.NewClOrdId,
            Symbol = rr.Symbol,
            SecurityId = rr.SecurityId,
            Side = rr.Side,
            Type = rr.Type,
            Quantity = rr.NewQuantity,
            Price = rr.NewPrice,
            TimeInForce = rr.RequestedTimeInForce ?? nameof(B3.Trading.Domain.TimeInForce.Day),
            StopPrice = rr.RequestedStopPrice,
            GoodTillDate = rr.RequestedGoodTillDate,
            LeavesQuantity = rr.NewQuantity,
            CumulativeQuantity = 0,
            Status = OrderStatus.PendingNew,
            FirstSeq = seq,
            CreatedAtUtc = rr.TimestampUtc,
            LastSeq = seq,
            LastTs = rr.TimestampUtc,
        };

        public void ApplyEr(long seq, ExecutionReportReceivedEvent er)
        {
            LeavesQuantity = er.LeavesQuantity;
            CumulativeQuantity = er.CumulativeQuantity;
            if (Enum.TryParse<ExecKind>(er.ExecKind, ignoreCase: true, out var kind))
            {
                Status = kind switch
                {
                    ExecKind.New => OrderStatus.Working,
                    ExecKind.PartialFill => OrderStatus.PartiallyFilled,
                    ExecKind.Fill => er.LeavesQuantity == 0 ? OrderStatus.Filled : OrderStatus.PartiallyFilled,
                    ExecKind.Canceled => OrderStatus.Cancelled,
                    ExecKind.Expired => OrderStatus.Cancelled,
                    ExecKind.Rejected => OrderStatus.Rejected,
                    ExecKind.Replaced => OrderStatus.Replaced,
                    _ => Status,
                };
            }
            LastSeq = seq;
            LastTs = er.TimestampUtc;
        }

        /// <summary>
        /// Applied to the ORIGINAL order on a Replaced ER. Mirrors
        /// <c>ExecutionReportProcessor.ApplyReplaceAccepted</c> +
        /// <c>Order.MarkReplaced</c>: terminalize the original at
        /// <see cref="OrderStatus.Replaced"/> without disturbing its
        /// historical leaves/cum (the ER's leaves/cum belong to the
        /// new ClOrdID, not the original — they describe the venue's
        /// post-replacement state, which the original order never owns).
        /// </summary>
        public void ApplyReplacedTerminal(long seq, ExecutionReportReceivedEvent er)
        {
            Status = OrderStatus.Replaced;
            // MarkReplaced clears any advisory stale (slice 1 of #132);
            // mirror that here so the projection matches the runtime.
            IsStale = false;
            StaleReason = null;
            StaledAtUtc = null;
            LastSeq = seq;
            LastTs = er.TimestampUtc;
        }

        /// <summary>
        /// Applied to the NEW ClOrdID's projection on a Replaced ER.
        /// Mirrors <c>Order.HydrateReplacement</c>: copy the venue's
        /// leaves/cum baseline and derive status (Filled when leaves==0,
        /// PartiallyFilled when cum>0, otherwise Working — never PendingNew
        /// because the venue has already accepted the replacement).
        /// Subsequent ERs targeting the new ClOrdID flow through
        /// <see cref="ApplyEr"/> and accumulate from this baseline.
        /// </summary>
        public void HydrateFromReplaceEr(long seq, ExecutionReportReceivedEvent er)
        {
            LeavesQuantity = er.LeavesQuantity;
            CumulativeQuantity = er.CumulativeQuantity;
            Status = er.LeavesQuantity == 0
                ? OrderStatus.Filled
                : (er.CumulativeQuantity > 0 ? OrderStatus.PartiallyFilled : OrderStatus.Working);
            LastSeq = seq;
            LastTs = er.TimestampUtc;
        }

        public void ApplyStaled(long seq, OrderStaledEvent os)
        {
            IsStale = true;
            StaleReason = os.Reason;
            StaledAtUtc = os.StaledAtUtc;
            LastSeq = seq;
            LastTs = os.TimestampUtc;
        }

        public void ApplyStaleCleared(long seq, OrderStaleClearedEvent osc)
        {
            IsStale = false;
            StaleReason = null;
            StaledAtUtc = null;
            LastSeq = seq;
            LastTs = osc.TimestampUtc;
        }

        public OrderHistoryItemDto ToDto() => new(
            ClOrdId.ToString(),
            Symbol,
            SecurityId,
            Side,
            Type,
            Quantity,
            LeavesQuantity,
            CumulativeQuantity,
            Price,
            Status.ToString(),
            TimeInForce,
            StopPrice,
            GoodTillDate,
            IsStale,
            StaleReason,
            StaledAtUtc,
            CreatedAtUtc,
            LastTs);
    }

    private sealed record ExecutionProjection(
        long Seq,
        ulong ClOrdId,
        string Symbol,
        string Side,
        string Kind,
        long LeavesQuantity,
        long CumulativeQuantity,
        long LastQuantity,
        decimal LastPrice,
        string? RejectReason,
        DateTimeOffset TimestampUtc)
    {
        public ExecutionHistoryItemDto ToDto() => new(
            ClOrdId.ToString(),
            Symbol,
            Side,
            Kind,
            LeavesQuantity,
            CumulativeQuantity,
            LastQuantity,
            LastPrice,
            RejectReason,
            TimestampUtc,
            IsNativeStp: Kind.Equals(nameof(ExecKind.Canceled), StringComparison.OrdinalIgnoreCase)
                && NativeStpDetector.IsNativeStpReason(RejectReason));
    }

    private sealed record CursorState(long Seq, DateTimeOffset Ts)
    {
        // STJ requires either a parameterless ctor or matching property names
        // for deserialisation. Using positional record + lowercased JSON
        // keys via [JsonPropertyName] keeps the wire format compact.
        [System.Text.Json.Serialization.JsonPropertyName("seq")] public long Seq { get; init; } = Seq;
        [System.Text.Json.Serialization.JsonPropertyName("ts")] public DateTimeOffset Ts { get; init; } = Ts;
        // Q2.1 (#268). Pagination snapshot anchor for /orders/history.
        // Captured on the first request (no cursor) and threaded through
        // every subsequent page so the walk reads a frozen WAL view.
        // Default 0 means "no snapshot" — old cursors (pre-fix) and the
        // executions endpoint both deserialise/encode that way.
        [System.Text.Json.Serialization.JsonPropertyName("snap")] public long SnapshotSeq { get; init; }
    }

    private sealed record PageResult<T>(IReadOnlyList<T> Items, string? NextCursor);
}

/// <summary>Q2.1 (#268). Generic cursor-paginated history page wire shape.</summary>
public sealed record HistoryPageDto<T>(IReadOnlyList<T> Items, string? NextCursor);

/// <summary>Q2.1 (#268). Wire shape for one row of <c>GET /orders/history</c>.</summary>
public sealed record OrderHistoryItemDto(
    string ClOrdId,
    string Symbol,
    ulong SecurityId,
    string Side,
    string Type,
    long Quantity,
    long LeavesQuantity,
    long CumulativeQuantity,
    decimal? Price,
    string Status,
    string TimeInForce,
    decimal? StopPrice,
    DateTimeOffset? GoodTillDate,
    bool IsStale,
    string? StaleReason,
    DateTimeOffset? StaledAtUtc,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset LastUpdatedAtUtc);

/// <summary>Q2.1 (#268). Wire shape for one row of <c>GET /executions/history</c>.</summary>
public sealed record ExecutionHistoryItemDto(
    string ClOrdId,
    string Symbol,
    string Side,
    string Kind,
    long LeavesQuantity,
    long CumulativeQuantity,
    long LastQuantity,
    decimal LastPrice,
    string? RejectReason,
    DateTimeOffset TimestampUtc,
    bool IsNativeStp);
