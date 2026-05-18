namespace B3.Trading.Domain;

public enum OrderSide
{
    Buy,
    Sell,
}

public enum OrderType
{
    Limit,
    Market,
    /// <summary>
    /// Stop order: triggers a Market order when last trade price reaches
    /// <see cref="Order.StopPrice"/>. Wire byte = SBE <c>STOP_LOSS</c>.
    /// </summary>
    StopLoss,
    /// <summary>
    /// Stop-limit order: triggers a Limit order at <see cref="Order.Price"/>
    /// when last trade price reaches <see cref="Order.StopPrice"/>. Wire
    /// byte = SBE <c>STOP_LIMIT</c>.
    /// </summary>
    StopLimit,
    /// <summary>
    /// Market-with-leftover-as-Limit: marketable up to <see cref="Order.Price"/>;
    /// any unfilled remainder rests on the book as a Limit at that price.
    /// Wire byte = SBE <c>MARKET_WITH_LEFTOVER_AS_LIMIT</c>.
    /// </summary>
    MarketWithLeftover,
}

/// <summary>
/// Helpers over <see cref="OrderType"/> that capture cross-cutting
/// classifications used by the risk / margin pipeline.
/// </summary>
public static class OrderTypeExtensions
{
    /// <summary>
    /// True iff this order type carries a limit price that the
    /// reserve-on-submit margin provider will use to up-front reserve
    /// cash on a buy submit. Must stay in lock-step with that
    /// provider's reserve criterion (Buy +
    /// <see cref="Order.Price"/> non-null + positive notional): every
    /// listed type below is one that *can* be priced and therefore can
    /// have a reservation that the cancel-replace pipeline must
    /// re-baseline. <see cref="OrderType.Market"/> and
    /// <see cref="OrderType.StopLoss"/> are excluded — they trigger
    /// into a Market and have no upfront price to reserve against.
    /// </summary>
    public static bool IsMarginBearing(this OrderType type) =>
        type is OrderType.Limit
             or OrderType.StopLimit
             or OrderType.MarketWithLeftover;
}

/// <summary>
/// Time-In-Force for a working order. The default value is <see cref="Day"/>
/// so older WAL/snapshot payloads that pre-date this enum hydrate with
/// the implicit "Day" semantics they actually carried.
/// </summary>
public enum TimeInForce
{
    /// <summary>Resting until the end of the trading day; cancelled at session close.</summary>
    Day,
    /// <summary>Match what is immediately marketable; cancel any remainder.</summary>
    IOC,
    /// <summary>All-or-nothing on the immediately marketable book; otherwise cancel.</summary>
    FOK,
    /// <summary>Resting until cancelled.</summary>
    GTC,
    /// <summary>Resting until <see cref="Order.GoodTillDate"/>; cancelled at end of that day.</summary>
    GTD,
    /// <summary>Submitted to (only) the closing auction.</summary>
    AtClose,
    /// <summary>Submitted to (only) the next opening / re-opening call auction.</summary>
    GoodForAuction,
}

/// <summary>
/// Trading session phase for an instrument (or the venue as a whole).
/// Drives the <c>SessionPhaseCheck</c> pre-trade gate (#108): every
/// phase admits a different subset of order types.
///
/// <para>B3 cash-equities reality has more granular sub-phases (e.g.
/// random opening window, post-trade), but for pre-trade routing what
/// matters is whether a continuous match or a call-auction is on, and
/// whether the venue is open at all. The six values below cover that
/// product surface without leaking match-engine internals into the
/// risk model.</para>
/// </summary>
public enum SessionPhase
{
    /// <summary>Venue closed — no orders accepted.</summary>
    Closed,
    /// <summary>Pre-opening cancel-and-amend window before the call.</summary>
    PreOpening,
    /// <summary>Opening call auction — limit orders only, no market.</summary>
    OpeningAuction,
    /// <summary>Continuous matching — all order types allowed.</summary>
    Continuous,
    /// <summary>Closing call auction — limit orders only, no market.</summary>
    ClosingAuction,
    /// <summary>After-hours session — limit orders only, no market.</summary>
    AfterHours,
}

/// <summary>
/// Q3.4 (#284). Policy that drives whether/how the venue refreshes
/// the visible portion (display qty) of an iceberg/reserve order
/// after a fill. Applies only when <see cref="Order.DisplayQty"/>
/// is set (full-disclosure orders have no reserve to refresh).
///
/// <para>The B3 EntryPoint SDK 0.14.3 does not expose a refresh-policy
/// flag on <c>NewOrderRequest</c> — only <c>MaxFloor</c> (the initial
/// visible qty). Until the SDK plumbs a per-order policy, every
/// iceberg submission inherits the venue's default refresh semantics
/// (currently <see cref="Always"/> on B3). The enum is retained on
/// the platform side so the trader's intent is recorded in the WAL /
/// snapshot and surfaced through the REST/UI surface; the value is
/// informational/local-only on the wire (see TODO in
/// <c>B3EntryPointClientGateway.SubmitAsync</c>).</para>
/// </summary>
public enum DisplayResetPolicy
{
    /// <summary>Every fill refreshes display back to <see cref="Order.DisplayQty"/>. Most common iceberg semantics; matches the venue default.</summary>
    Always,
    /// <summary>Refresh only on partial fills; on full-display fills don't refresh and let the book consume the next slice naturally.</summary>
    OnPartialFill,
    /// <summary>No refresh; once visible qty is consumed it stays at 0 (one-shot peek into the reserve).</summary>
    Never,
}

public enum OrderStatus
{
    PendingNew,
    Working,
    PartiallyFilled,
    Filled,
    Cancelled,
    Rejected,

    /// <summary>
    /// Terminal state assigned to the original order after a successful
    /// cancel-replace (FIX 35=G amend). The replacement order lives under
    /// a different ClOrdID; the original is non-restable from this point
    /// on and is filtered out of all open-order projections (margin,
    /// risk, blotter, snapshots). Slice 1 of #122 introduces the value;
    /// slice 2 wires the transition.
    /// </summary>
    Replaced,
}

/// <summary>
/// Working order owned by a single end-client. Quantity / status mutate as
/// EntryPoint ExecutionReports flow back from the exchange. Persistence is
/// out-of-scope for the bootstrap; v1 is ephemeral and re-derived from ER
/// replay on (re)connect.
/// </summary>
public sealed class Order
{
    /// <summary>
    /// <paramref name="firmId"/> is the FIXP session this order belongs to.
    /// Required by the gateway to route cancel/replace requests to the right
    /// upstream <c>EntryPointClient</c> when the host is configured for
    /// multiple firms. Default <c>"DEFAULT"</c> exists only to keep older
    /// unit tests terse; production call sites always pass an explicit firm.
    /// </summary>
    public Order(
        ulong clOrdId,
        EndClientId owner,
        string symbol,
        ulong securityId,
        OrderSide side,
        OrderType type,
        long quantity,
        decimal? price,
        string firmId = "DEFAULT",
        ulong? parentAlgoId = null,
        int? algoSliceSeq = null,
        TimeInForce timeInForce = TimeInForce.Day,
        decimal? stopPrice = null,
        DateTimeOffset? goodTillDate = null,
        long? displayQty = null,
        DisplayResetPolicy? displayResetPolicy = null,
        SubAccountId? subAccountId = null)
    {
        if (clOrdId == 0)
            throw new ArgumentOutOfRangeException(nameof(clOrdId), "ClOrdID cannot be zero (reserved as null sentinel by EntryPoint).");
        if (string.IsNullOrWhiteSpace(firmId))
            throw new ArgumentException("FirmId required.", nameof(firmId));
        if (parentAlgoId is 0)
            throw new ArgumentOutOfRangeException(nameof(parentAlgoId), "ParentAlgoId cannot be zero (reserved as null sentinel).");
        if ((parentAlgoId is null) != (algoSliceSeq is null))
            throw new ArgumentException("ParentAlgoId and AlgoSliceSeq must be set together (both null = manual order; both set = algo child).");
        if (algoSliceSeq is < 0)
            throw new ArgumentOutOfRangeException(nameof(algoSliceSeq));

        // Q1.1 (#253) — StopPrice/GoodTillDate cross-field invariants.
        // Always-true invariants checked here (so WAL replay and snapshot
        // hydrate cannot reconstitute illegal combinations). The wallclock
        // "GoodTillDate must be in the future" check belongs at submit
        // time and lives in the API surface, not in the ctor.
        var requiresStop = type is OrderType.StopLoss or OrderType.StopLimit;
        if (requiresStop && (!stopPrice.HasValue || stopPrice.Value <= 0m))
            throw new ArgumentException(
                $"StopPrice is required and must be positive for OrderType.{type}.",
                nameof(stopPrice));
        if (!requiresStop && stopPrice.HasValue)
            throw new ArgumentException(
                $"StopPrice must be null for OrderType.{type} (only StopLoss/StopLimit accept a stop trigger).",
                nameof(stopPrice));
        if (timeInForce == TimeInForce.GTD && !goodTillDate.HasValue)
            throw new ArgumentException(
                "GoodTillDate is required when TimeInForce == GTD.",
                nameof(goodTillDate));
        if (timeInForce != TimeInForce.GTD && goodTillDate.HasValue)
            throw new ArgumentException(
                $"GoodTillDate must be null when TimeInForce == {timeInForce} (only GTD carries an expiry).",
                nameof(goodTillDate));

        // Q3.4 (#284). Iceberg / reserve display-qty invariants.
        // displayQty=null → full disclosure (no reserve); a non-null
        // value MUST be strictly positive and not exceed total order
        // quantity (a "reserve" larger than the order is nonsensical;
        // an equal value is identical to no reserve and is allowed
        // only for symmetry with the validation message). Policy is
        // null iff displayQty is null; when displayQty is set without
        // an explicit policy we default to Always — the most common
        // iceberg semantic and the venue default.
        if (displayQty.HasValue)
        {
            if (displayQty.Value <= 0)
                throw new ArgumentException(
                    "DisplayQty must be positive when set (use null for full disclosure / no reserve).",
                    nameof(displayQty));
            if (displayQty.Value > quantity)
                throw new ArgumentException(
                    $"DisplayQty ({displayQty.Value}) must not exceed order Quantity ({quantity}).",
                    nameof(displayQty));
            displayResetPolicy ??= B3.Trading.Domain.DisplayResetPolicy.Always;
        }
        else if (displayResetPolicy.HasValue)
        {
            throw new ArgumentException(
                "DisplayResetPolicy must be null when DisplayQty is null (full-disclosure orders have no reserve to refresh).",
                nameof(displayResetPolicy));
        }

        ClOrdId = clOrdId;
        Owner = owner;
        Symbol = symbol;
        SecurityId = securityId;
        Side = side;
        Type = type;
        Quantity = quantity;
        Price = price;
        FirmId = firmId;
        ParentAlgoId = parentAlgoId;
        AlgoSliceSeq = algoSliceSeq;
        TimeInForce = timeInForce;
        StopPrice = stopPrice;
        GoodTillDate = goodTillDate;
        DisplayQty = displayQty;
        DisplayResetPolicy = displayResetPolicy;
        SubAccountId = subAccountId;
        LeavesQuantity = quantity;
        Status = OrderStatus.PendingNew;
    }

    public ulong ClOrdId { get; }
    public EndClientId Owner { get; }
    public string Symbol { get; }
    public ulong SecurityId { get; }
    public string FirmId { get; }
    public OrderSide Side { get; }
    public OrderType Type { get; }
    public long Quantity { get; }
    public decimal? Price { get; }
    /// <summary>
    /// When set, this order is a child slice produced by an
    /// <c>AlgoEngine</c> on behalf of the parent <see cref="Algo"/> with
    /// id <see cref="ParentAlgoId"/> and slice index <see cref="AlgoSliceSeq"/>.
    /// Manual orders submitted via <c>POST /orders</c> leave both fields
    /// <c>null</c>. The pair is set together or both <c>null</c> — never
    /// one without the other (RFC §4.2).
    /// </summary>
    public ulong? ParentAlgoId { get; }
    public int? AlgoSliceSeq { get; }

    /// <summary>
    /// Q1.1 (#253). Time-in-force for the order. Defaults to <see cref="TimeInForce.Day"/>
    /// for legacy code paths that pre-date the field; persisted on
    /// <c>OrderSubmittedEvent</c> and the snapshot.
    /// </summary>
    public TimeInForce TimeInForce { get; }

    /// <summary>
    /// Q1.1 (#253). Trigger price for <see cref="OrderType.StopLoss"/> /
    /// <see cref="OrderType.StopLimit"/>. <c>null</c> for every other
    /// <see cref="OrderType"/>; the constructor enforces that invariant
    /// in both directions.
    /// </summary>
    public decimal? StopPrice { get; }

    /// <summary>
    /// Q1.1 (#253). Expiry timestamp for <see cref="TimeInForce.GTD"/>.
    /// <c>null</c> for every other <see cref="TimeInForce"/>; the
    /// constructor enforces that invariant in both directions. The
    /// "must be in the future" check is the submit pipeline's job (see
    /// <c>OrderSubmissionService</c>) — replay/hydration must accept
    /// an already-elapsed timestamp so recovery is total.
    /// </summary>
    public DateTimeOffset? GoodTillDate { get; }

    /// <summary>
    /// Q3.4 (#284). Native iceberg / reserve display quantity — the
    /// visible portion the venue exposes on the public book on
    /// submit. <c>null</c> = full disclosure (no reserve / no
    /// iceberg behaviour). When set, must satisfy
    /// <c>0 &lt; DisplayQty &lt;= Quantity</c> (enforced by the
    /// constructor). Distinct from the client-emulated
    /// <c>IcebergAlgo</c> / <c>IcebergParameters</c>, which slices
    /// at the algo layer; this field rides on a plain
    /// <see cref="Order"/> and is plumbed natively via the SDK's
    /// <c>MaxFloor</c> field — the venue itself drains the hidden
    /// reserve and refreshes the visible portion per
    /// <see cref="DisplayResetPolicy"/>.
    /// </summary>
    public long? DisplayQty { get; }

    /// <summary>
    /// Q3.4 (#284). Refresh semantics for the visible portion of an
    /// iceberg order. <c>null</c> iff <see cref="DisplayQty"/> is
    /// <c>null</c>; otherwise defaults to
    /// <see cref="B3.Trading.Domain.DisplayResetPolicy.Always"/>.
    /// See the enum for the per-value semantics — and for why this
    /// is currently informational/local-only on the SDK wire (the
    /// B3 EntryPoint 0.14.3 <c>NewOrderRequest</c> does not expose
    /// a refresh-policy flag; the venue defaults to <c>Always</c>).
    /// </summary>
    public DisplayResetPolicy? DisplayResetPolicy { get; }

    /// <summary>
    /// Q4.1 (#301). Optional sub-account bucket the order is booked
    /// against. <c>null</c> = master bucket (the only value legacy
    /// callers ever produced). Sub-accounts are namespaced per-firm
    /// at the registry level — see
    /// <see cref="Domain.SubAccountId"/> and
    /// <c>SubAccountsRegistry</c>.
    /// </summary>
    public SubAccountId? SubAccountId { get; }

    public long LeavesQuantity { get; private set; }
    public long CumulativeQuantity { get; private set; }
    public OrderStatus Status { get; private set; }

    /// <summary>
    /// Slice 1 of #132. Advisory flag set when the platform suspects the
    /// venue no longer knows about this order — typically because the
    /// matching engine restarted with a fresh book while trading-host
    /// retained its WAL/snapshot state. Stale orders remain in their
    /// previous status (Working/PartiallyFilled) so positions/cash/risk
    /// accounting is unchanged, but Cancel/Modify is blocked at the API
    /// (409) since sending those against a phantom is wasted bandwidth
    /// and creates extra ClOrdIDs that the venue will reject. Cleared
    /// automatically when a real terminal ER arrives (the venue actually
    /// knew the order — false positive). NOT a status because the
    /// underlying business state hasn't changed; it's an overlay.
    /// </summary>
    public bool IsStale { get; private set; }

    /// <summary>Free-text reason recorded when staleness was set.</summary>
    public string? StaleReason { get; private set; }

    /// <summary>Wall-clock timestamp of the first stale mark (preserved on idempotent re-marks).</summary>
    public DateTimeOffset? StaledAtUtc { get; private set; }

    /// <summary>
    /// Slice 1 of #132. Mark this order as suspected-stale-by-venue.
    /// Returns <c>true</c> when the call mutated state (i.e. the order
    /// was restable and not already stale), <c>false</c> otherwise.
    ///
    /// <para>
    /// Only Working / PartiallyFilled orders may be marked stale. We
    /// deliberately exclude PendingNew (the venue may simply not have
    /// acked yet — that's a different bug class) and every terminal
    /// status (Filled/Cancelled/Rejected/Replaced — nothing left to
    /// be ghosted). Idempotent: re-marking an already-stale order is
    /// a no-op and preserves the original <see cref="StaledAtUtc"/>.
    /// </para>
    /// </summary>
    public bool MarkStale(string reason, DateTimeOffset atUtc)
    {
        if (string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("Stale reason required.", nameof(reason));
        if (IsStale)
            return false;
        if (Status is not (OrderStatus.Working or OrderStatus.PartiallyFilled))
            return false;
        IsStale = true;
        StaleReason = reason;
        StaledAtUtc = atUtc;
        return true;
    }

    /// <summary>
    /// Clears advisory staleness. Called by
    /// <see cref="ExecutionReportProcessor"/> on terminal ERs (the venue
    /// actually still knew the order — the stale mark was a false
    /// positive) and by the admin "clear stale" path. Returns
    /// <c>true</c> when state changed.
    /// </summary>
    public bool ClearStale()
    {
        if (!IsStale)
            return false;
        IsStale = false;
        StaleReason = null;
        StaledAtUtc = null;
        return true;
    }

    public void ApplyFill(long fillQty)
    {
        if (fillQty <= 0)
            throw new ArgumentOutOfRangeException(nameof(fillQty));
        if (fillQty > LeavesQuantity)
            throw new InvalidOperationException("Fill exceeds leaves quantity.");

        CumulativeQuantity += fillQty;
        LeavesQuantity -= fillQty;
        Status = LeavesQuantity == 0 ? OrderStatus.Filled : OrderStatus.PartiallyFilled;
    }

    /// <summary>
    /// Cumulative-quantity-driven fill application. Returns the delta that
    /// was applied (0 when the incoming <paramref name="newCumulativeQty"/>
    /// is stale/duplicate). Designed to be safe under ER replay and
    /// out-of-order delivery: only ever advances forward, never throws,
    /// and preserves a terminal <see cref="OrderStatus.Cancelled"/> /
    /// <see cref="OrderStatus.Rejected"/> when a "late" fill arrives after
    /// the terminal ER (the exchange may legitimately deliver a fill that
    /// happened pre-cancel after the cancel-ack).
    ///
    /// <para>
    /// Overfill (newCumQty &gt; Quantity) is permitted: leaves clamps at 0
    /// and the field still advances to whatever the exchange reports,
    /// because the WAL replay must remain total — throwing here would
    /// poison recovery for any persisted ER stream containing an overfill.
    /// </para>
    /// </summary>
    public long ApplyCumulativeFill(long newCumulativeQty)
    {
        if (newCumulativeQty <= CumulativeQuantity)
            return 0;

        var delta = newCumulativeQty - CumulativeQuantity;
        CumulativeQuantity = newCumulativeQty;
        LeavesQuantity = Math.Max(0, Quantity - newCumulativeQty);

        // Status only advances; never regresses out of a terminal state.
        // Replaced is also terminal-for-restability — an original order in
        // Replaced status is not a restable surface for late fills (those
        // arrive against the replacement ClOrdID).
        if (Status is not (OrderStatus.Cancelled or OrderStatus.Rejected or OrderStatus.Replaced))
            Status = LeavesQuantity == 0 ? OrderStatus.Filled : OrderStatus.PartiallyFilled;

        return delta;
    }

    public void MarkWorking()
    {
        // Idempotency: New ER may be re-delivered after reconnect. Only the
        // PendingNew→Working transition is meaningful; later ERs (including
        // any that re-state New) must not regress an already-fillable
        // order back to Working.
        if (Status == OrderStatus.PendingNew)
            Status = OrderStatus.Working;
    }

    public void MarkCancelled()
    {
        // Once filled / replaced, the order can't be cancelled — a stale
        // Cancelled ER delivered after the final fill (or after a
        // successful replace re-targeted under a new ClOrdID) would
        // otherwise regress status.
        if (Status is OrderStatus.Filled or OrderStatus.Rejected or OrderStatus.Cancelled or OrderStatus.Replaced)
            return;
        Status = OrderStatus.Cancelled;
    }

    public void MarkRejected()
    {
        // Rejection is only valid before any fill. A stale Reject after a
        // partial/full fill (or after the order was replaced) must be
        // ignored.
        if (Status is OrderStatus.Filled or OrderStatus.PartiallyFilled or OrderStatus.Rejected or OrderStatus.Cancelled or OrderStatus.Replaced)
            return;
        Status = OrderStatus.Rejected;
    }

    /// <summary>
    /// Marks the original order as terminally replaced. Slice 1 of #122
    /// only exposes the transition; slice 2 will fire it from
    /// <see cref="ExecutionReportProcessor"/> on a successful Replaced
    /// ER. Idempotent and refuses to regress out of Filled/Rejected
    /// (a late Replaced ER must not erase a final fill).
    /// </summary>
    public void MarkReplaced()
    {
        if (Status is OrderStatus.Filled or OrderStatus.Rejected or OrderStatus.Cancelled or OrderStatus.Replaced)
            return;
        Status = OrderStatus.Replaced;
    }

    /// <summary>
    /// Reconstructs an order from snapshot data. For persistence recovery
    /// only — bypasses the state-machine invariants because the snapshot
    /// was, by construction, produced from a sequence of valid mutations.
    /// </summary>
    internal static Order Hydrate(
        ulong clOrdId, EndClientId owner, string symbol, ulong securityId, OrderSide side, OrderType type,
        long quantity, decimal? price, long leaves, long cumQty, OrderStatus status, string firmId = "DEFAULT",
        ulong? parentAlgoId = null, int? algoSliceSeq = null,
        bool isStale = false, string? staleReason = null, DateTimeOffset? staledAtUtc = null,
        TimeInForce timeInForce = TimeInForce.Day,
        decimal? stopPrice = null,
        DateTimeOffset? goodTillDate = null,
        long? displayQty = null,
        DisplayResetPolicy? displayResetPolicy = null,
        SubAccountId? subAccountId = null)
    {
        var o = new Order(clOrdId, owner, symbol, securityId, side, type, quantity, price, firmId, parentAlgoId, algoSliceSeq,
            timeInForce, stopPrice, goodTillDate, displayQty, displayResetPolicy, subAccountId);
        o.LeavesQuantity = leaves;
        o.CumulativeQuantity = cumQty;
        o.Status = status;
        if (isStale)
        {
            o.IsStale = true;
            o.StaleReason = staleReason;
            o.StaledAtUtc = staledAtUtc;
        }
        return o;
    }

    /// <summary>
    /// Slice 2 of #122. Builds the replacement Order that the venue
    /// just acknowledged via a Replaced ER. Symbol/side/type/owner/
    /// firm/parent-algo are inherited from the original; quantity and
    /// price are the values requested by the modify; cumQty and leaves
    /// come from the ER (the venue's view of how much was already
    /// filled under the original at the moment the replace took effect).
    ///
    /// <para>
    /// Status is derived from the cum/leaves baseline:
    /// <c>cum &gt;= quantity</c> → <see cref="OrderStatus.Filled"/> (the
    /// modify was over-filled before it took effect — degenerate but
    /// possible if a fill races the replace; status reflects venue truth);
    /// <c>cum &gt; 0</c> → <see cref="OrderStatus.PartiallyFilled"/>;
    /// otherwise → <see cref="OrderStatus.Working"/> (no
    /// <see cref="OrderStatus.PendingNew"/> because the venue has
    /// already accepted the replacement — slice 2 invariant).
    /// </para>
    ///
    /// <para>
    /// Existing fills booked under the original ClOrdID are NOT
    /// re-booked here — <see cref="PositionKeeper"/> already saw them
    /// when their fill ERs flowed under the original. The cum/leaves
    /// values exist on the replacement only so subsequent fill ERs
    /// (which arrive under the new ClOrdID) can advance via
    /// <see cref="ApplyCumulativeFill"/> from the correct baseline.
    /// </para>
    /// </summary>
    public static Order HydrateReplacement(
        Order original,
        ulong newClOrdId,
        long newQuantity,
        decimal? newPrice,
        long erLeaves,
        long erCumulative,
        TimeInForce? requestedTimeInForce = null,
        decimal? requestedStopPrice = null,
        DateTimeOffset? requestedGoodTillDate = null)
    {
        ArgumentNullException.ThrowIfNull(original);
        if (newClOrdId == 0)
            throw new ArgumentOutOfRangeException(nameof(newClOrdId), "ClOrdID cannot be zero.");
        if (newQuantity <= 0)
            throw new ArgumentOutOfRangeException(nameof(newQuantity), "Replacement quantity must be positive.");
        if (erCumulative < 0)
            throw new ArgumentOutOfRangeException(nameof(erCumulative));
        if (erLeaves < 0)
            throw new ArgumentOutOfRangeException(nameof(erLeaves));

        var (effTif, effStop, effGtd) = MergeReplacementOptionals(
            original.Type, original.TimeInForce, original.StopPrice, original.GoodTillDate,
            requestedTimeInForce, requestedStopPrice, requestedGoodTillDate);

        var status = erCumulative >= newQuantity
            ? OrderStatus.Filled
            : (erCumulative > 0 ? OrderStatus.PartiallyFilled : OrderStatus.Working);

        // Q3.4 (#284). Iceberg display fields are inherited from the
        // original on cancel-replace — the modify pipeline does not
        // currently expose a way to alter DisplayQty / policy. If the
        // operator shrinks the order quantity below the original
        // visible portion, clamp DisplayQty to the new quantity so
        // the ctor invariant (DisplayQty &lt;= Quantity) still holds.
        // A future modify-pipeline slice can plumb explicit overrides.
        long? effDisplayQty = original.DisplayQty;
        DisplayResetPolicy? effPolicy = original.DisplayResetPolicy;
        if (effDisplayQty.HasValue && effDisplayQty.Value > newQuantity)
            effDisplayQty = newQuantity;

        return Hydrate(
            clOrdId: newClOrdId,
            owner: original.Owner,
            symbol: original.Symbol,
            securityId: original.SecurityId,
            side: original.Side,
            type: original.Type,
            quantity: newQuantity,
            price: newPrice,
            leaves: erLeaves,
            cumQty: erCumulative,
            status: status,
            firmId: original.FirmId,
            parentAlgoId: original.ParentAlgoId,
            algoSliceSeq: original.AlgoSliceSeq,
            // Q1.1 (#253) — TIF / StopPrice / GoodTillDate are mergeable
            // through the modify pipeline. Null on the requested side =
            // inherit the original; non-null = override. OrderType is NOT
            // modifiable in B3 cancel-replace (FIX standard) so it is
            // always inherited from the original.
            timeInForce: effTif,
            stopPrice: effStop,
            goodTillDate: effGtd,
            displayQty: effDisplayQty,
            displayResetPolicy: effPolicy,
            subAccountId: original.SubAccountId);
    }

    /// <summary>
    /// Q1.1 (#253). Pure merge function for the modify pipeline's
    /// optional Q1.1 fields. The rule, mirrored at every site that
    /// computes a replacement (early validation in
    /// <c>OrderModifyService</c>, hydration in
    /// <see cref="HydrateReplacement"/>, gateway dispatch in
    /// <c>B3EntryPointClientGateway.CancelReplaceAsync</c>):
    ///
    /// <list type="bullet">
    ///   <item><c>effTif</c> = requested ?? original.</item>
    ///   <item>If <c>effType</c> ∈ {StopLoss, StopLimit}:
    ///     <c>effStop</c> = requested ?? original (must be &gt; 0).
    ///     Otherwise: <c>requestedStop</c> must be null and
    ///     <c>effStop</c> is null.</item>
    ///   <item>If <c>effTif == GTD</c>: <c>effGtd</c> = requested ??
    ///     original (must be non-null). Otherwise: <c>requestedGtd</c>
    ///     must be null AND <c>effGtd</c> is auto-cleared to null —
    ///     this is how callers move TIF away from GTD without having
    ///     to redundantly null-out an inherited expiry.</item>
    /// </list>
    ///
    /// <para>
    /// Throws <see cref="ArgumentException"/> on any violation; the
    /// caller is expected to translate it into a modify rejection
    /// (<c>OrderModifyResultKind.BadRequest</c>) before WAL append.
    /// </para>
    /// </summary>
    public static (TimeInForce EffectiveTimeInForce, decimal? EffectiveStopPrice, DateTimeOffset? EffectiveGoodTillDate)
        MergeReplacementOptionals(
            OrderType originalType,
            TimeInForce originalTimeInForce,
            decimal? originalStopPrice,
            DateTimeOffset? originalGoodTillDate,
            TimeInForce? requestedTimeInForce,
            decimal? requestedStopPrice,
            DateTimeOffset? requestedGoodTillDate)
    {
        var effTif = requestedTimeInForce ?? originalTimeInForce;

        var requiresStop = originalType is OrderType.StopLoss or OrderType.StopLimit;
        decimal? effStop;
        if (requiresStop)
        {
            effStop = requestedStopPrice ?? originalStopPrice;
            if (!effStop.HasValue || effStop.Value <= 0m)
                throw new ArgumentException(
                    $"StopPrice is required and must be positive for OrderType.{originalType}.",
                    nameof(requestedStopPrice));
        }
        else
        {
            if (requestedStopPrice.HasValue)
                throw new ArgumentException(
                    $"StopPrice must be null for OrderType.{originalType} (only StopLoss/StopLimit accept a stop trigger).",
                    nameof(requestedStopPrice));
            effStop = null;
        }

        DateTimeOffset? effGtd;
        if (effTif == TimeInForce.GTD)
        {
            effGtd = requestedGoodTillDate ?? originalGoodTillDate;
            if (!effGtd.HasValue)
                throw new ArgumentException(
                    "GoodTillDate is required when TimeInForce == GTD.",
                    nameof(requestedGoodTillDate));
        }
        else
        {
            if (requestedGoodTillDate.HasValue)
                throw new ArgumentException(
                    $"GoodTillDate must be null when TimeInForce == {effTif} (only GTD carries an expiry).",
                    nameof(requestedGoodTillDate));
            // TIF moving away from GTD: auto-clear inherited expiry so
            // the merged Order satisfies the GTD-iff-GoodTillDate
            // invariant without forcing callers to null it explicitly.
            effGtd = null;
        }

        return (effTif, effStop, effGtd);
    }
}
