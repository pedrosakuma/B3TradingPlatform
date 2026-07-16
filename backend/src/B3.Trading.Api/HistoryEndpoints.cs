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
/// Design agreed in <c>docs/rfcs/history-index-v0.md</c> (#453,
/// Option C — resolved per-day index emitted by the EOD pass);
/// implementation deferred until the retention/latency trigger in
/// that RFC's §7 fires.
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
                // Capture the boundary BEFORE the flush — IEventStore.CurrentSeq
                // includes appends that have not yet hit disk, so reading it after
                // FlushAsync allows a concurrent AppendCore (between the flush
                // returning and CurrentSeq being read) to inflate snapshotSeq with
                // a record that page 1 cannot see. That record's (LastSeq, ClOrdId)
                // would sort above page 1's cursor anchor and be filtered out of
                // every subsequent page forever. Capturing first and flushing after
                // gives a stable frozen view: late appends get a seq > snapshotSeq
                // and are intentionally invisible to this walk.
                snapshotSeq = store.CurrentSeq;
                await store.FlushAsync(ct);
            }

            var orders = await ProjectOrdersAsync(store, owner.Value, symbol, fromTs, toTs, snapshotSeq, ct);
            // Sort newest-first by the composite key (LastSeq, ClOrdId).
            // The ClOrdId tie-breaker is required because a Replaced ER
            // updates BOTH the original and the replacement projection
            // with the same LastSeq; pagination by LastSeq alone would
            // drop one sibling whenever the page boundary fell between
            // them. See ApplyCursorAndPage for the matching cursor
            // filter.
            orders.Sort(static (a, b) =>
            {
                var c = b.LastSeq.CompareTo(a.LastSeq);
                return c != 0 ? c : b.ClOrdId.CompareTo(a.ClOrdId);
            });

            var page = ApplyCursorAndPage(orders, cursorState, pageSize, snapshotSeq, static x => (x.LastSeq, x.ClOrdId, x.LastTs));
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

            var page = ApplyCursorAndPage(executions, cursorState, pageSize, snapshotSeq: 0, static x => (x.Seq, 0UL, x.TimestampUtc));
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
        // Cancel/replace link maps. Mirror the in-memory
        // OrderOwnershipMap.RegisterCancelLink / RegisterReplaceLink the
        // runtime maintains on dispatch (see OrderCancelService and
        // OrderModifyService). When a venue ER drops OrigClOrdId on a
        // cancel-ack or Replaced — some EntryPoint SDK versions do —
        // ExecutionReportProcessor.Apply falls back to that ownership
        // map (TryResolveOrig) to find the original order. The history
        // projector must mirror that fallback or it silently strands
        // every cancel/replace ack whose OrigClOrdId field was missing.
        var cancelLinks = new Dictionary<ulong, ulong>();           // cancelClOrdId -> originalClOrdId
        var replaceLinks = new Dictionary<ulong, ReplaceIntent>();  // newClOrdId    -> (originalClOrdId, requested NewQuantity)
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
                    // Mirror OrderOwnershipMap.RegisterReplaceLink — needed
                    // when the venue's Replaced ER omits OrigClOrdId.
                    replaceLinks[rr.NewClOrdId] = new ReplaceIntent(rr.OriginalClOrdId, rr.NewQuantity);
                    if (!OwnerMatches(rr.EndClientId, owner)) break;
                    if (symbol is not null && !rr.Symbol.Equals(symbol, StringComparison.Ordinal)) break;
                    byClOrdId[rr.NewClOrdId] = OrderProjection.FromReplace(
                        seq,
                        rr,
                        // Inherit TIF/StopPrice/GoodTillDate from the
                        // original projection when the modify request
                        // omits them — mirrors Order.HydrateReplacement
                        // / Order.MergeReplacementOptionals so a quantity-
                        // only replace of a GTD or stop order doesn't
                        // silently drop those fields in the history view.
                        byClOrdId.TryGetValue(rr.OriginalClOrdId, out var origReplaceFrom) ? origReplaceFrom : null);
                    if (inWindow) hadEventInWindow.Add(rr.NewClOrdId);
                    break;

                case OrderReplacePreSendFailedEvent rpf:
                    replaceLinks.Remove(rpf.NewClOrdId);
                    if (byClOrdId.TryGetValue(rpf.NewClOrdId, out var failedReplace))
                    {
                        failedReplace.ApplyEr(seq, new ExecutionReportReceivedEvent
                        {
                            ClOrdId = rpf.NewClOrdId,
                            ExecKind = nameof(ExecKind.Rejected),
                            LeavesQuantity = 0,
                            CumulativeQuantity = 0,
                            LastQuantity = 0,
                            LastPrice = 0m,
                            RejectReason = rpf.Reason,
                            Synthetic = true,
                            OrigClOrdId = rpf.OriginalClOrdId,
                            TimestampUtc = rpf.TimestampUtc,
                        });
                        if (inWindow) hadEventInWindow.Add(rpf.NewClOrdId);
                    }
                    break;

                case OrderCancelRequestedEvent cr:
                    // No projection row for cancel-side ClOrdIDs (they
                    // never become an Order in the runtime book either).
                    // We only need the link so a cancel-ack ER without
                    // OrigClOrdId still resolves to the original order.
                    // Mirrors OrderOwnershipMap.RegisterCancelLink.
                    cancelLinks[cr.CancelClOrdId] = cr.OriginalClOrdId;
                    break;

                case OrderCancelPreSendFailedEvent cff:
                    cancelLinks.Remove(cff.CancelClOrdId);
                    break;

                case ExecutionReportReceivedEvent er:
                    Enum.TryParse<ExecKind>(er.ExecKind, ignoreCase: true, out var kind);
                    // Resolve OrigClOrdId from the link maps when the venue
                    // dropped it on the wire. Mirrors the runtime
                    // ExecutionReportProcessor.Apply OrigClOrdId fallback
                    // (TryResolveOrig) — without this, history strands
                    // cancel-acks and Replaced ERs whose OrigClOrdId was 0.
                    var resolvedOrig = er.OrigClOrdId;
                    if (resolvedOrig == 0)
                    {
                        // Two distinct link maps mirror the runtime
                        // OrderOwnershipMap fallback: a cancel-side ack
                        // resolves only via cancelLinks (so a Cancel ER
                        // for a ClOrdID that ALSO happens to be the
                        // "new" side of an in-flight replace is treated
                        // as a direct cancel of that order, not a cancel
                        // of its predecessor — matches runtime, which
                        // routes the priority-lost cancel-as-replace
                        // through PendingReplacementRegistry instead of
                        // the OrigClOrdId fallback). Replaced ERs
                        // resolve only via replaceLinks.
                        if (kind == ExecKind.Replaced && replaceLinks.TryGetValue(er.ClOrdId, out var rIntent))
                            resolvedOrig = rIntent.OriginalClOrdId;
                        else if ((kind is ExecKind.Canceled or ExecKind.Rejected or ExecKind.Expired)
                            && cancelLinks.TryGetValue(er.ClOrdId, out var cOrig))
                            resolvedOrig = cOrig;
                    }

                    if (kind == ExecKind.Replaced && resolvedOrig != 0)
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
                        if (byClOrdId.TryGetValue(resolvedOrig, out var origProj))
                        {
                            origProj.ApplyReplacedTerminal(seq, er);
                            if (inWindow) hadEventInWindow.Add(resolvedOrig);
                        }
                        if (byClOrdId.TryGetValue(er.ClOrdId, out var newProj))
                        {
                            newProj.HydrateFromReplaceEr(seq, er);
                            if (inWindow) hadEventInWindow.Add(er.ClOrdId);
                        }
                        // Consume the replace link — mirrors
                        // PendingReplacementRegistry.TryConsume's
                        // remove-on-success contract on the runtime's
                        // Replaced branch. Without this a later cancel of
                        // er.ClOrdId (which is now Working in its own
                        // right and may be modified/cancelled directly)
                        // would be re-intercepted by the cancel-as-replace
                        // branch below as if the link were still in
                        // flight.
                        replaceLinks.Remove(er.ClOrdId);
                        break;
                    }

                    // Issue #241 mirror: B3MatchingPlatform's "priority-lost"
                    // cancel-as-replace path. The venue implements an effective
                    // modify by emitting Cancel(orig) + Trade/New(new) under
                    // the replacement's NEW ClOrdID — never an ExecType=Replaced.
                    // Runtime intercepts in ExecutionReportProcessor.Apply via
                    // PendingReplacementRegistry.TryConsume on the Canceled
                    // branch and funnels through ApplyReplaceAccepted. The
                    // projector mirrors that contract: a Canceled ER whose
                    // ClOrdId matches an unresolved replace link (i.e. the new
                    // side of an in-flight replace where no Replaced ER has
                    // landed yet) terminalises the original at Replaced
                    // (subject to terminal-preservation in ApplyReplacedTerminal)
                    // and hydrates the new ClOrdID from the ER's leaves/cum,
                    // then CONSUMES the link so a subsequent true cancel ack
                    // on the same ClOrdID is processed as a normal cancel of
                    // the new order rather than re-intercepted. The check
                    // uses er.ClOrdId directly (the registry is keyed by
                    // newClOrdId, same as replaceLinks) and runs BEFORE any
                    // OrigClOrdId-fallback resolution above wired via cancelLinks
                    // (the new-side ClOrdID is never in cancelLinks).
                    if (kind == ExecKind.Canceled
                        && replaceLinks.TryGetValue(er.ClOrdId, out var carIntent))
                    {
                        // Mirror ApplyReplaceAccepted: prefer the ER's
                        // OrigClOrdId when the venue did supply one,
                        // otherwise fall back to the intent's original
                        // (here: the replaceLinks entry).
                        var origId = er.OrigClOrdId != 0 ? er.OrigClOrdId : carIntent.OriginalClOrdId;
                        if (byClOrdId.TryGetValue(origId, out var carOrigProj))
                        {
                            carOrigProj.ApplyReplacedTerminal(seq, er);
                            if (inWindow) hadEventInWindow.Add(origId);
                        }
                        if (byClOrdId.TryGetValue(er.ClOrdId, out var carNewProj))
                        {
                            // Mirror ExecutionReportProcessor.Apply for the
                            // Canceled cancel-as-replace branch: runtime calls
                            // ApplyReplaceAccepted(erLeaves: intent.NewQuantity,
                            // erCum: 0) — the venue's cancel-side ER reports
                            // LeavesQuantity=0 (it's a cancel ack), so we MUST
                            // hydrate from the originating
                            // OrderReplaceRequestedEvent's NewQuantity, not
                            // from the ER's leaves/cum (which would mark the
                            // replacement as Filled).
                            carNewProj.HydrateFromCancelAsReplaceIntent(seq, er, carIntent.NewQuantity);
                            if (inWindow) hadEventInWindow.Add(er.ClOrdId);
                        }
                        // Consume the link — mirrors PendingReplacementRegistry
                        // .TryConsume's remove-on-success contract so the next
                        // Canceled ER on this ClOrdID is treated as a real
                        // cancel of the new order, not re-intercepted.
                        replaceLinks.Remove(er.ClOrdId);
                        break;
                    }

                    // Non-Replaced ER: may target either ClOrdId directly
                    // (New, Fill, etc.) or via OrigClOrdId (cancel ack
                    // lands on the cancel-side ID — never carried by an
                    // OrderSubmittedEvent — but mutates the original).
                    var targetId = er.ClOrdId;
                    if (resolvedOrig != 0 && byClOrdId.ContainsKey(resolvedOrig))
                        targetId = resolvedOrig;
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
        // Mirrors OrderOwnershipMap.RegisterCancelLink/RegisterReplaceLink
        // so cancel/replace ack ERs that the venue emitted with
        // OrigClOrdId=0 still resolve back to the original order's
        // owner/firm — without this fallback the executions endpoint
        // silently drops every such ack for the firm-isolation filter.
        var cancelLinks = new Dictionary<ulong, ulong>();
        var replaceLinks = new Dictionary<ulong, ReplaceIntent>();
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
                    replaceLinks[rr.NewClOrdId] = new ReplaceIntent(rr.OriginalClOrdId, rr.NewQuantity);
                    break;
                case OrderReplacePreSendFailedEvent rpf:
                    replaceLinks.Remove(rpf.NewClOrdId);
                    if (rpf.TimestampUtc < from || rpf.TimestampUtc > to) break;
                    if (!ownerByClOrdId.TryGetValue(rpf.NewClOrdId, out var failedMeta)) break;
                    if (!OwnerMatches(failedMeta.Owner, owner)) break;
                    if (symbol is not null && !failedMeta.Symbol.Equals(symbol, StringComparison.Ordinal)) break;
                    result.Add(new ExecutionProjection(
                        Seq: seq,
                        ClOrdId: rpf.NewClOrdId,
                        Symbol: failedMeta.Symbol,
                        Side: failedMeta.Side,
                        Kind: nameof(ExecKind.Rejected),
                        LeavesQuantity: 0,
                        CumulativeQuantity: 0,
                        LastQuantity: 0,
                        LastPrice: 0m,
                        RejectReason: rpf.Reason,
                        TimestampUtc: rpf.TimestampUtc));
                    break;
                case OrderReplaceRejectedEvent rrj:
                    // #337 — surface the rejected modify so it shows up
                    // in the trader's executions log alongside the
                    // submit-side rejects. The original order's
                    // owner/symbol/side are already in the side-table
                    // (registered by the matching OrderSubmittedEvent
                    // when the original was created); look them up so
                    // the firm-isolation + owner filters apply
                    // correctly. If we can't resolve the original
                    // (truncated WAL window) we drop the row — same
                    // posture as the ER branch below.
                    if (rrj.TimestampUtc < from || rrj.TimestampUtc > to) break;
                    if (!ownerByClOrdId.TryGetValue(rrj.OriginalClOrdId, out var origMeta)) break;
                    if (!OwnerMatches(origMeta.Owner, owner)) break;
                    if (symbol is not null && !origMeta.Symbol.Equals(symbol, StringComparison.Ordinal)) break;
                    result.Add(new ExecutionProjection(
                        Seq: seq,
                        // Surface the burned NewClOrdId so the row is
                        // unique against any other reject on the same
                        // original (multiple modify attempts) — the
                        // FE blotter already keys executions by
                        // ClOrdId+Seq.
                        ClOrdId: rrj.NewClOrdId,
                        Symbol: origMeta.Symbol,
                        Side: origMeta.Side,
                        Kind: nameof(ExecKind.Rejected),
                        LeavesQuantity: 0,
                        CumulativeQuantity: 0,
                        LastQuantity: 0,
                        LastPrice: 0m,
                        RejectReason: rrj.Reason,
                        TimestampUtc: rrj.TimestampUtc));
                    break;
                case OrderCancelRequestedEvent cr:
                    cancelLinks[cr.CancelClOrdId] = cr.OriginalClOrdId;
                    break;
                case OrderCancelPreSendFailedEvent cff:
                    cancelLinks.Remove(cff.CancelClOrdId);
                    break;
                case ExecutionReportReceivedEvent er:
                    if (er.TimestampUtc < from || er.TimestampUtc > to) break;
                    // Owner resolution: try the ER's own ClOrdId first,
                    // then OrigClOrdId, then the link maps (mirroring the
                    // runtime ExecutionReportProcessor.Apply fallback).
                    (string Owner, string Symbol, string Side)? meta = null;
                    if (ownerByClOrdId.TryGetValue(er.ClOrdId, out var m1)) meta = m1;
                    else if (er.OrigClOrdId != 0 && ownerByClOrdId.TryGetValue(er.OrigClOrdId, out var m2)) meta = m2;
                    else
                    {
                        // OrigClOrdId fallback: same separation as
                        // ProjectOrdersAsync — replaceLinks is consulted
                        // only for Replaced ERs, cancelLinks only for
                        // cancel-side acks (Canceled / Rejected /
                        // Expired). Mirrors runtime
                        // OrderOwnershipMap.TryResolveOrig + the
                        // cancel-as-replace intercept that owns the
                        // "Cancel ER for the new side of an in-flight
                        // replace" case via PendingReplacementRegistry,
                        // not via the OrigClOrdId fallback.
                        Enum.TryParse<ExecKind>(er.ExecKind, ignoreCase: true, out var execKind);
                        if (execKind == ExecKind.Replaced
                            && replaceLinks.TryGetValue(er.ClOrdId, out var rIntent)
                            && ownerByClOrdId.TryGetValue(rIntent.OriginalClOrdId, out var m3)) meta = m3;
                        else if (execKind is ExecKind.Canceled or ExecKind.Rejected or ExecKind.Expired
                            && cancelLinks.TryGetValue(er.ClOrdId, out var cOrig)
                            && ownerByClOrdId.TryGetValue(cOrig, out var m4)) meta = m4;
                    }
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
        Func<T, (long Seq, ulong TieBreaker, DateTimeOffset Ts)> anchor)
    {
        // Items are sorted (Seq DESC, TieBreaker DESC). The cursor anchors
        // the LAST item we returned previously; the next page strictly
        // precedes it under the same composite ordering. The TieBreaker
        // is the order's ClOrdId for /orders/history (a Replaced ER
        // updates both the original and the replacement with the same
        // LastSeq, so paging by Seq alone would drop the sibling at the
        // boundary). For /executions/history each ER occupies a unique
        // WAL Seq so the tie-breaker is unused (passed as 0).
        IEnumerable<T> windowed = sortedDesc;
        if (cursor is { } c)
        {
            windowed = sortedDesc.Where(x =>
            {
                var a = anchor(x);
                if (a.Seq < c.Seq) return true;
                if (a.Seq > c.Seq) return false;
                return a.TieBreaker < c.ClOrdId;
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
            next = EncodeCursor(new CursorState(a.Seq, a.Ts) { ClOrdId = a.TieBreaker, SnapshotSeq = snapshotSeq });
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

    /// <summary>
    /// Replace-link entry recorded from <see cref="OrderReplaceRequestedEvent"/>:
    /// the original ClOrdID being replaced and the requested
    /// <c>NewQuantity</c>. The latter is needed by the cancel-as-replace
    /// branch to mirror <c>ExecutionReportProcessor.Apply</c>'s
    /// <c>ApplyReplaceAccepted(erLeaves: intent.NewQuantity, erCum: 0)</c>
    /// for <c>ExecKind.Canceled</c> — the cancel-side ER's
    /// <c>LeavesQuantity</c> is 0 and cannot be used to hydrate the
    /// replacement.
    /// </summary>
    private readonly record struct ReplaceIntent(ulong OriginalClOrdId, long NewQuantity);

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

        // Q3.4 (#284) — native iceberg / reserve display fields, mirrored
        // onto the projection so /orders/history is not blind to the
        // distinction between iceberg and full-disclosure orders. Null on
        // legacy WAL rows that pre-date the additive fields on
        // OrderSubmittedEvent — matches the no-reserve semantics those
        // submissions actually carried (forward-compat with replay).
        public long? DisplayQty;
        public string? DisplayResetPolicy;

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
            DisplayQty = o.DisplayQty,
            DisplayResetPolicy = o.DisplayResetPolicy,
            LeavesQuantity = o.Quantity,
            CumulativeQuantity = 0,
            Status = OrderStatus.PendingNew,
            FirstSeq = seq,
            CreatedAtUtc = o.TimestampUtc,
            LastSeq = seq,
            LastTs = o.TimestampUtc,
        };

        public static OrderProjection FromReplace(long seq, OrderReplaceRequestedEvent rr, OrderProjection? original)
        {
            // Q1.1 (#253) parity with Order.HydrateReplacement /
            // Order.MergeReplacementOptionals: when the modify request
            // omits TIF / StopPrice / GoodTillDate, the runtime
            // INHERITS them from the original order. Treating the
            // requested fields as final values would silently demote a
            // GTD or stop order on a quantity-only replace (history
            // would show TIF=Day + null stop/gtd while the live book
            // kept the original semantics).
            //
            // Mirror MergeReplacementOptionals exactly so the projection
            // matches HydrateReplacement's effective fields:
            //   • effTif  = requested ?? original
            //   • effStop = requested ?? original
            //   • effGtd  = requested ?? original IF effTif == GTD;
            //               otherwise auto-cleared (same trick the
            //               domain uses to let callers shed an inherited
            //               expiry just by switching TIF away from GTD).
            //
            // If the original projection isn't available (e.g. owner
            // filter dropped it, or the WAL slice begins after the
            // original) we fall back to the request as-is — there is
            // no inheritance source.
            var effTif = rr.RequestedTimeInForce
                ?? original?.TimeInForce
                ?? nameof(B3.Trading.Domain.TimeInForce.Day);
            var effStop = rr.RequestedStopPrice ?? original?.StopPrice;
            var isGtd = string.Equals(effTif, nameof(B3.Trading.Domain.TimeInForce.GTD), StringComparison.Ordinal);
            var effGtd = isGtd
                ? (rr.RequestedGoodTillDate ?? original?.GoodTillDate)
                : null;

            // Q3.4 (#284). Iceberg display fields are inherited from the
            // ORIGINAL projection on cancel-replace — the modify pipeline
            // does not (yet) expose a way to alter DisplayQty / policy,
            // so OrderReplaceRequestedEvent currently carries no explicit
            // override. Mirror Order.HydrateReplacement's clamp semantics
            // so the projection reflects what the venue actually sees:
            // if the operator shrinks the order quantity below the
            // visible portion, clamp DisplayQty down to NewQuantity.
            // (When a future modify-pipeline slice adds explicit overrides
            // on OrderReplaceRequestedEvent, prefer those here; today
            // there is no source other than the original projection.)
            long? effDisplayQty = original?.DisplayQty;
            string? effDisplayPolicy = original?.DisplayResetPolicy;
            if (effDisplayQty.HasValue && effDisplayQty.Value > rr.NewQuantity)
                effDisplayQty = rr.NewQuantity;

            return new OrderProjection
            {
                ClOrdId = rr.NewClOrdId,
                Symbol = rr.Symbol,
                SecurityId = rr.SecurityId,
                Side = rr.Side,
                Type = rr.Type,
                Quantity = rr.NewQuantity,
                Price = rr.NewPrice,
                TimeInForce = effTif,
                StopPrice = effStop,
                GoodTillDate = effGtd,
                DisplayQty = effDisplayQty,
                DisplayResetPolicy = effDisplayPolicy,
                LeavesQuantity = rr.NewQuantity,
                CumulativeQuantity = 0,
                Status = OrderStatus.PendingNew,
                FirstSeq = seq,
                CreatedAtUtc = rr.TimestampUtc,
                LastSeq = seq,
                LastTs = rr.TimestampUtc,
            };
        }

        /// <summary>
        /// Applies an ER to this projection. Mirrors the runtime
        /// state-transition guards in
        /// <c>ExecutionReportProcessor.Apply</c> and
        /// <see cref="B3.Trading.Domain.Order.ApplyCumulativeFill"/> /
        /// <see cref="B3.Trading.Domain.Order.MarkCancelled"/> /
        /// <see cref="B3.Trading.Domain.Order.MarkRejected"/> /
        /// <see cref="B3.Trading.Domain.Order.MarkWorking"/>:
        /// <list type="bullet">
        ///   <item>Fills only advance forward on cumulative quantity and
        ///   never regress a terminal status (<c>Cancelled</c> /
        ///   <c>Rejected</c> / <c>Replaced</c>); a late fill on a
        ///   cancelled order keeps the order Cancelled.</item>
        ///   <item>Cancels are dropped if the order is already terminal
        ///   (<c>Filled</c> / <c>Rejected</c> / <c>Cancelled</c> /
        ///   <c>Replaced</c>).</item>
        ///   <item>Rejects are dropped if the order has any fill or is
        ///   terminal.</item>
        ///   <item><c>New</c> only transitions from <c>PendingNew</c> and
        ///   never touches leaves/cum.</item>
        /// </list>
        /// Without these guards, the history view diverges from the
        /// runtime book on ER orderings the runtime de-duplicates
        /// (e.g. <c>[Canceled(A), Fill(A)]</c> → runtime keeps A
        /// Cancelled; history previously flipped to Filled).
        /// </summary>
        public void ApplyEr(long seq, ExecutionReportReceivedEvent er)
        {
            if (Enum.TryParse<ExecKind>(er.ExecKind, ignoreCase: true, out var kind))
            {
                switch (kind)
                {
                    case ExecKind.New:
                        // Order.MarkWorking: PendingNew → Working only.
                        // Leaves/cum are NOT touched (the runtime never
                        // alters them on a New ER either; a re-delivered
                        // New must not regress a fillable order).
                        if (Status == OrderStatus.PendingNew)
                            Status = OrderStatus.Working;
                        break;

                    case ExecKind.PartialFill:
                    case ExecKind.Fill:
                        // Order.ApplyCumulativeFill: only advance when
                        // cumQty > current; preserve terminal status on
                        // late fills (Cancelled / Rejected / Replaced
                        // remain — exchange's truth still books to
                        // positions in the runtime, but the order
                        // surface keeps its terminal label).
                        if (er.CumulativeQuantity > CumulativeQuantity)
                        {
                            CumulativeQuantity = er.CumulativeQuantity;
                            LeavesQuantity = Math.Max(0, Quantity - CumulativeQuantity);
                            if (Status is not (OrderStatus.Cancelled
                                or OrderStatus.Rejected
                                or OrderStatus.Replaced))
                            {
                                Status = LeavesQuantity == 0
                                    ? OrderStatus.Filled
                                    : OrderStatus.PartiallyFilled;
                            }
                        }
                        break;

                    case ExecKind.Canceled:
                    case ExecKind.Expired:
                        // Order.MarkCancelled: dropped if already terminal.
                        // Note ExecutionReportProcessor.Apply also drops a
                        // Canceled ER for an already-PartiallyFilled order
                        // only via the MarkCancelled guard — which DOES
                        // allow PartiallyFilled→Cancelled (a working order
                        // with a partial can still be cancelled). Mirror
                        // exactly: only Filled/Rejected/Cancelled/Replaced
                        // block. Crucially, MarkCancelled only flips
                        // Status — it does NOT touch leaves/cum. Real
                        // venue cancels typically carry LeavesQuantity=0,
                        // so copying them into the projection would
                        // diverge from the runtime book (a 10-lot working
                        // order cancelled would show leaves=0 in history
                        // while runtime keeps leaves=10). Quantities only
                        // advance from fill ERs.
                        if (Status is not (OrderStatus.Filled
                            or OrderStatus.Rejected
                            or OrderStatus.Cancelled
                            or OrderStatus.Replaced))
                        {
                            Status = OrderStatus.Cancelled;
                        }
                        break;

                    case ExecKind.Rejected:
                        // Order.MarkRejected: dropped after any fill or
                        // any terminal. Matches the
                        // ExecutionReportProcessor guard set exactly.
                        // Like MarkCancelled, MarkRejected is status-only;
                        // do not copy leaves/cum from the ER.
                        if (Status is not (OrderStatus.Filled
                            or OrderStatus.PartiallyFilled
                            or OrderStatus.Rejected
                            or OrderStatus.Cancelled
                            or OrderStatus.Replaced))
                        {
                            Status = OrderStatus.Rejected;
                        }
                        break;

                    case ExecKind.Replaced:
                        // Defensive only: Replaced ERs flow through
                        // ApplyReplacedTerminal/HydrateFromReplaceEr in
                        // ProjectOrdersAsync. If we land here it means
                        // OrigClOrdId was missing AND no replace link
                        // was registered — fall back to MarkReplaced
                        // semantics on the targeted projection.
                        if (Status is not (OrderStatus.Filled
                            or OrderStatus.Rejected
                            or OrderStatus.Cancelled
                            or OrderStatus.Replaced))
                        {
                            Status = OrderStatus.Replaced;
                        }
                        break;
                }
            }
            // Mirror ExecutionReportProcessor.Apply (slice 1 of #132): a real
            // terminal ER proves the venue still knew the order, so any prior
            // advisory stale overlay was a false positive. Clear stale on
            // Filled/Cancelled/Rejected/Replaced — but NOT on PartiallyFilled
            // (trader's concern about the un-filled remainder is still valid).
            if (IsStale && Status is OrderStatus.Filled
                or OrderStatus.Cancelled
                or OrderStatus.Rejected
                or OrderStatus.Replaced)
            {
                IsStale = false;
                StaleReason = null;
                StaledAtUtc = null;
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
            // Mirror Order.MarkReplaced: a Replaced ack racing a real
            // terminal (Filled/Rejected/Cancelled) — or a duplicated
            // Replaced ER — must NOT regress the predecessor's status.
            // Only flip non-terminal predecessors to Replaced.
            if (Status is not (OrderStatus.Filled
                or OrderStatus.Rejected
                or OrderStatus.Cancelled
                or OrderStatus.Replaced))
            {
                Status = OrderStatus.Replaced;
            }
            // MarkReplaced clears any advisory stale (slice 1 of #132);
            // mirror that here so the projection matches the runtime.
            // Applied unconditionally — runtime ClearStale runs after
            // MarkReplaced regardless of whether status changed.
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

        /// <summary>
        /// Applied to the NEW ClOrdID's projection on a cancel-as-replace
        /// Canceled ER (B3MatchingPlatform "priority-lost" path, issue #241).
        /// Mirrors <c>ExecutionReportProcessor.Apply</c> for
        /// <c>ExecKind.Canceled</c>, which calls
        /// <c>ApplyReplaceAccepted(erLeaves: intent.NewQuantity, erCum: 0)</c>:
        /// the venue's cancel-side ER reports <c>LeavesQuantity=0</c>
        /// (it's a cancel ack), so we hydrate from the originating
        /// <see cref="OrderReplaceRequestedEvent"/>'s
        /// <c>NewQuantity</c>, not from the ER's leaves/cum. Cumulative is
        /// reset to 0 — the replacement is a brand-new order in the runtime
        /// book and does not inherit the predecessor's fills.
        /// </summary>
        public void HydrateFromCancelAsReplaceIntent(long seq, ExecutionReportReceivedEvent er, long intentNewQuantity)
        {
            LeavesQuantity = intentNewQuantity;
            CumulativeQuantity = 0;
            Status = intentNewQuantity == 0 ? OrderStatus.Filled : OrderStatus.Working;
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
            LastTs,
            DisplayQty,
            DisplayResetPolicy);
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
        // Q2.1 (#268) — composite-keyset tie-breaker. Required because
        // a Replaced ER advances both the original and the replacement
        // projection to the same LastSeq; sorting/paging by Seq alone
        // would silently drop one sibling whenever the page boundary
        // fell between them. ClOrdId is the secondary sort key for the
        // /orders/history walk; /executions/history sets it to 0
        // (each ER is one WAL row with a unique Seq). Default 0 keeps
        // pre-fix encoded cursors decodable.
        [System.Text.Json.Serialization.JsonPropertyName("cl")] public ulong ClOrdId { get; init; }
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
    DateTimeOffset LastUpdatedAtUtc,
    /// <summary>Q3.4 (#284). Native iceberg / reserve display quantity at
    /// submit (or, on a cancel-replace row, inherited from the predecessor
    /// and clamped to NewQuantity per Order.HydrateReplacement). Null =
    /// full disclosure / no reserve, including legacy WAL rows that
    /// pre-date the additive WAL field.</summary>
    long? DisplayQty = null,
    /// <summary>Q3.4 (#284). Refresh policy enum name (<c>"Always" |
    /// "OnPartialFill" | "Never"</c>); null iff <see cref="DisplayQty"/>
    /// is null. Today only <c>"Always"</c> is accepted at intake (SDK
    /// limitation — see #298).</summary>
    string? DisplayResetPolicy = null);

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
