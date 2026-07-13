using System.Text.Json.Serialization;
using B3.Trading.Domain;

namespace B3.Trading.Application.Persistence;

/// <summary>
/// Base type for every event written to the WAL. Each derived record is
/// serialised as a single JSON object inside a length+CRC framed record on
/// disk. The <see cref="JsonPolymorphicAttribute"/> discriminator keeps
/// the schema explicit and forward-compatible: an unknown subtype is
/// <i>skipped with a structured warning</i> during recovery rather than
/// crashing the reader — older binaries can therefore traverse WAL
/// segments produced by newer engines (rolling deploys, downgrade paths,
/// EOD materialisation of mixed-version days). See
/// <see cref="B3.Trading.Infrastructure.Persistence.FileEventStore.ReadFromAsync"/>
/// for the skip site. Genuine corruption (malformed JSON for a
/// <i>known</i> kind, missing required fields, etc.) still surfaces as
/// an exception — only the polymorphic discriminator is treated leniently.
///
/// <para>
/// Schema evolution rule: never rename fields, only add new optional ones.
/// To remove a field, leave it on the record as <c>[Obsolete]</c> until
/// every retained segment has rotated out.
/// </para>
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(OrderSubmittedEvent), "order.submitted")]
[JsonDerivedType(typeof(OrderReplaceRequestedEvent), "order.replace-requested")]
[JsonDerivedType(typeof(OrderReplaceRejectedEvent), "order.replace-rejected")]
[JsonDerivedType(typeof(OrderReplaceAmbiguousMarginHeldEvent), "order.replace-ambiguous-margin-held")]
[JsonDerivedType(typeof(ExecutionReportReceivedEvent), "er.received")]
[JsonDerivedType(typeof(KillSwitchToggledEvent), "killswitch.toggled")]
[JsonDerivedType(typeof(SymbolHaltToggledEvent), "symbol-halt.toggled")]
[JsonDerivedType(typeof(SessionPhaseChangedEvent), "session-phase.changed")]
[JsonDerivedType(typeof(AlgoCreatedEvent), "algo.created")]
[JsonDerivedType(typeof(AlgoCancelRequestedEvent), "algo.cancel-requested")]
[JsonDerivedType(typeof(AlgoTerminalStateRecordedEvent), "algo.terminal")]
[JsonDerivedType(typeof(AlgoVwapSlicedEvent), "algo.vwap.sliced")]
[JsonDerivedType(typeof(AlgoPovSlicedEvent), "algo.pov.sliced")]
[JsonDerivedType(typeof(AlgoPeggedRepeggedEvent), "algo.pegged.repegged")]
[JsonDerivedType(typeof(AlgoPeggedRepegStartedEvent), "algo.pegged.repeg-started")]
[JsonDerivedType(typeof(AlgoPeggedRepegResolvedEvent), "algo.pegged.repeg-resolved")]
[JsonDerivedType(typeof(AlgoChildModifiedEvent), "algo.child.modified")]
[JsonDerivedType(typeof(OrderStaledEvent), "order.staled")]
[JsonDerivedType(typeof(OrderStaleClearedEvent), "order.stale-cleared")]
[JsonDerivedType(typeof(UserBotCredentialCreatedEvent), "userbot.cred.created")]
[JsonDerivedType(typeof(UserBotCredentialRevokedEvent), "userbot.cred.revoked")]
[JsonDerivedType(typeof(UserBotCredentialCertBindingChangedEvent), "userbot.cred.cert-binding-changed")]
[JsonDerivedType(typeof(BotSessionInitializedEvent), "userbot.session.initialized")]
[JsonDerivedType(typeof(BotSessionVerAdvancedEvent), "userbot.session.ver-advanced")]
[JsonDerivedType(typeof(BotSessionSeqAdvancedEvent), "userbot.session.seq-advanced")]
[JsonDerivedType(typeof(OrderCancelRequestedEvent), "order.cancel-requested")]
[JsonDerivedType(typeof(OrderExpiredEvent), "order.expired")]
[JsonDerivedType(typeof(CashLedgerEvent), "cash.ledger")]
[JsonDerivedType(typeof(FeeAccruedEvent), "fee.accrued")]
[JsonDerivedType(typeof(RealizedPnlEvent), "pnl.realized")]
[JsonDerivedType(typeof(SubAccountCreatedEvent), "sub-account.created")]
[JsonDerivedType(typeof(SubAccountDeactivatedEvent), "sub-account.deactivated")]
[JsonDerivedType(typeof(AuditLogEvent), "audit.log")]
[JsonDerivedType(typeof(BusinessRejectReceivedEvent), "business-reject.received")]
public abstract record WalEvent
{
    public DateTimeOffset TimestampUtc { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Captured at the moment an order is accepted by the API
/// (post-validation, post-ClOrdID allocation, before risk evaluation).
/// Replay re-creates the in-memory <c>Order</c>, registers ownership, and
/// re-allocates the ClOrdID counter watermark via the registry snapshot.
/// </summary>
public sealed record OrderSubmittedEvent : WalEvent
{
    public required ulong ClOrdId { get; init; }
    public required string EndClientId { get; init; }
    public required string FirmId { get; init; }
    public required string Symbol { get; init; }
    public required ulong SecurityId { get; init; }
    public required string Side { get; init; }
    public required string Type { get; init; }
    public required long Quantity { get; init; }
    public decimal? Price { get; init; }

    /// <summary>
    /// When set, the order is a child slice of an <see cref="AlgoCreatedEvent"/>'s
    /// parent. Both algo fields are set together or both <c>null</c>; manual
    /// orders submitted via <c>POST /orders</c> emit <c>null</c>. Added in
    /// algo orders v0 — older WAL segments without these fields deserialise
    /// with <c>null</c> on both, which matches the manual-order semantics
    /// they actually carried.
    /// </summary>
    public ulong? ParentAlgoId { get; init; }
    public int? AlgoSliceSeq { get; init; }

    /// <summary>
    /// Sub-issue #171 (E). When non-null, the order was submitted via
    /// the FIXP listener on behalf of a user-bot credential. Carries
    /// the side-mapping needed by <c>IUserBotOrderMappingRegistry</c>
    /// to reverse-route subsequent ERs back to the originating bot
    /// session (sub-issue F #172). Field is purely informational for
    /// REST/WS submissions where it serialises as <c>null</c>; older
    /// WAL segments without the field deserialise as <c>null</c> too,
    /// matching the manual-order semantics they actually carried.
    /// </summary>
    public BotOrderMapping? BotMapping { get; init; }

    /// <summary>
    /// Q1.1 (#253). Time-in-force at submit time. Defaults to
    /// <c>"Day"</c> so older WAL segments without the field deserialise
    /// with the implicit "Day" semantics they actually carried (the
    /// gateway hardcoded TIF=Day before this slice). Stored as the
    /// enum name to keep the wire format stable across future
    /// renumberings of <see cref="Domain.TimeInForce"/>.
    /// </summary>
    public string TimeInForce { get; init; } = nameof(B3.Trading.Domain.TimeInForce.Day);

    /// <summary>
    /// Q1.1 (#253). Trigger price for <see cref="Domain.OrderType.StopLoss"/> /
    /// <see cref="Domain.OrderType.StopLimit"/>. <c>null</c> for every other
    /// type. Older WAL segments without the field deserialise as
    /// <c>null</c>, matching the no-stop-orders semantics they carried.
    /// </summary>
    public decimal? StopPrice { get; init; }

    /// <summary>
    /// Q1.1 (#253). Expiry timestamp for <see cref="Domain.TimeInForce.GTD"/>.
    /// <c>null</c> for every other TIF. Older WAL segments without the
    /// field deserialise as <c>null</c>, matching the no-GTD semantics
    /// they carried.
    /// </summary>
    public DateTimeOffset? GoodTillDate { get; init; }

    /// <summary>
    /// Q3.4 (#284). Native iceberg / reserve display quantity. Null
    /// = full disclosure (no reserve). Older WAL segments without
    /// the field deserialise as <c>null</c>, matching the
    /// no-reserve semantics they actually carried.
    /// </summary>
    public long? DisplayQty { get; init; }

    /// <summary>
    /// Q3.4 (#284). Refresh policy for the visible portion of an
    /// iceberg order. Stored as the enum name to keep the wire
    /// format stable across future renumberings of
    /// <see cref="Domain.DisplayResetPolicy"/>. Null iff
    /// <see cref="DisplayQty"/> is null; otherwise one of
    /// <c>"Always" | "OnPartialFill" | "Never"</c>. Older WAL
    /// segments without the field deserialise as <c>null</c>.
    /// </summary>
    public string? DisplayResetPolicy { get; init; }

    /// <summary>
    /// Q4.1 (#301). Optional sub-account bucket. <c>null</c> = master
    /// bucket — every legacy WAL segment deserialises with this field
    /// missing, which maps to <c>null</c>, which is the only value
    /// the platform produced before this slice. Validated through
    /// <see cref="Domain.SubAccountId"/>'s constructor on replay so a
    /// corrupted id surfaces as a deserialisation error rather than
    /// silently poisoning the sub-account books.
    /// </summary>
    public string? SubAccountId { get; init; }
}

/// <summary>
/// Sub-record carried by <see cref="OrderSubmittedEvent"/> /
/// <see cref="OrderCancelRequestedEvent"/> when the order originates from
/// the FIXP listener (RFC §4.6 ClOrdId / §4.8 persistence). The pair
/// <c>(CredentialId, ExternalClOrdId)</c> is the bot-visible identity
/// for the order; the platform's internal <c>ulong</c> ClOrdID continues
/// to be the on-the-wire and on-WAL identifier.
/// </summary>
public sealed record BotOrderMapping(Guid CredentialId, ulong ExternalClOrdId);

/// <summary>
/// Sub-issue #171 (E). Recorded the moment a cancel request reaches the
/// platform — REST <c>DELETE /orders/{clOrdId}</c> or a FIXP
/// <c>OrderCancelRequest</c>. The previous in-memory-only path
/// (<c>OwnershipMap.RegisterCancelLink</c>) lost cancel-side state on
/// restart; persisting it as a WAL event closes the FIXP-cancel ER
/// round-trip across restart per RFC §4.6.
///
/// <para>The dispatcher <c>apply</c> callback runs the in-memory
/// mutations under the lock: ownership cancel-link, ClOrdID watermark
/// advance, and (when <see cref="BotMapping"/> is set) the bot
/// cancel-mapping registration. The async <c>gateway.CancelAsync</c>
/// I/O is invoked OUTSIDE the dispatcher lock per the dispatcher's
/// "synchronous in-memory work only" contract. On replay only the
/// in-memory mutation runs (no gateway call).</para>
/// </summary>
public sealed record OrderCancelRequestedEvent : WalEvent
{
    public required ulong CancelClOrdId { get; init; }
    public required ulong OriginalClOrdId { get; init; }
    public required string OwnerEndClientId { get; init; }
    public BotOrderMapping? BotMapping { get; init; }
}

/// <summary>
/// Slice 4 of #122. Recorded the moment <c>PUT /orders/{clOrdId}</c>
/// reaches the modify pipeline (post-validation, post-risk, after
/// margin Prepare succeeded but before the gateway dispatch).
///
/// <para>
/// Replay re-registers the in-flight intent in
/// <see cref="PendingReplacementRegistry"/> and the new→orig link in
/// <see cref="OrderOwnershipMap"/> so that, if the host restarts
/// between the WAL append and the venue's Replaced/Rejected ER, the
/// rebuilt state can still resolve the eventual ack. Margin
/// reservations are NOT replayed (matches
/// <see cref="OrderSubmittedEvent"/> semantics — reservations are
/// not durable across restarts in slice 2 of #107).
/// </para>
/// </summary>
public sealed record OrderReplaceRequestedEvent : WalEvent
{
    public required ulong OriginalClOrdId { get; init; }
    public required ulong NewClOrdId { get; init; }
    public required string EndClientId { get; init; }
    public required string FirmId { get; init; }
    public required string Symbol { get; init; }
    public required ulong SecurityId { get; init; }
    public required string Side { get; init; }
    public required string Type { get; init; }
    public required long NewQuantity { get; init; }
    public decimal? NewPrice { get; init; }
    public ulong? ParentAlgoId { get; init; }
    public int? AlgoSliceSeq { get; init; }

    // Q1.1 (#253) — optional modify-pipeline overrides for the three
    // Q1.1 fields. Null defaults so deserializing pre-Q1.1 WAL payloads
    // (which never carried these properties) hydrates as "no override
    // requested" → HydrateReplacement inherits everything from the
    // original Order. Distinct names (Requested*) avoid ambiguity with
    // the hydrated Order's TimeInForce/StopPrice/GoodTillDate fields.
    // TIF is stored as the enum name (string) to mirror
    // <see cref="OrderSubmittedEvent.TimeInForce"/> and stay stable
    // across enum renumberings.
    public string? RequestedTimeInForce { get; init; }
    public decimal? RequestedStopPrice { get; init; }
    public DateTimeOffset? RequestedGoodTillDate { get; init; }
}

/// <summary>
/// #337 — pre-trade reject on the modify (cancel-replace) pipeline.
/// Recorded when <see cref="B3.Trading.Application.OrderModifyService"/>
/// rejects a replace request via the risk pipeline OR the margin
/// coordinator, before any gateway dispatch. Closes the audit gap
/// flagged by the risk-pipeline-ordering RFC (#262): without this
/// event the rejected modify left zero WAL footprint — invisible to
/// <c>/executions/history</c>, the FE blotter, the CVM 35/505 export
/// (#308), the drop-copy feed (#306) and the best-exec touch capture
/// (#307). The companion <see cref="OrderReplaceRequestedEvent"/> is
/// NOT emitted on the reject path (no intent registration, no
/// new-ClOrdId watermark advance needed downstream), so this record
/// is the sole durable trace of the burned ClOrdId.
///
/// <para>
/// <b>Replay semantics.</b> Pure audit — replay does NOT mutate the
/// working book, ownership map, replacement registry, margin or
/// position state. The <see cref="NewClOrdId"/> is recorded so the
/// ClOrdId watermark advances during replay (consistent with the
/// successful-replace path), preventing the same ID from being
/// re-issued post-restart. See
/// <c>StateSnapshotter.ApplyWalEventDuringRecovery</c>.
/// </para>
///
/// <para>
/// <b>Additive payload.</b> Older binaries skip the unknown
/// discriminator with a structured warning (see <see cref="WalEvent"/>
/// schema evolution rule), so segments produced with this event remain
/// readable by pre-#337 readers.
/// </para>
/// </summary>
public sealed record OrderReplaceRejectedEvent : WalEvent
{
    public required ulong OriginalClOrdId { get; init; }
    /// <summary>
    /// ClOrdId that was generated and burned by the reject. Recorded
    /// so the post-restart watermark replay advances past it; never
    /// reused for a live order.
    /// </summary>
    public required ulong NewClOrdId { get; init; }
    public required string EndClientId { get; init; }
    public required string FirmId { get; init; }
    public required string Symbol { get; init; }
    public required ulong SecurityId { get; init; }
    public required string Side { get; init; }
    public required string Type { get; init; }
    public required long RequestedQuantity { get; init; }
    public decimal? RequestedPrice { get; init; }
    public string? RequestedTimeInForce { get; init; }
    public decimal? RequestedStopPrice { get; init; }
    public DateTimeOffset? RequestedGoodTillDate { get; init; }
    /// <summary>
    /// Origin of the reject: <c>"risk"</c> for a pre-WAL
    /// <see cref="B3.Trading.Application.Risk.RiskPipeline"/> decline,
    /// <c>"margin"</c> for an
    /// <see cref="B3.Trading.Application.Risk.Accounting.IReplaceMarginCoordinator"/>
    /// decline. Mirrors the <c>path=modify</c> +
    /// <c>reason=…</c> tags on <c>trading.orders.rejected.by_risk</c>.
    /// </summary>
    public required string Source { get; init; }
    /// <summary>
    /// Human-readable / machine-parsable reason string from the
    /// rejecting check (e.g. <c>"position_limit_exceeded"</c>,
    /// <c>"margin_insufficient"</c>). Same field the synthetic
    /// <see cref="ExecutionReportReceivedEvent.RejectReason"/> carries
    /// on the submit-side reject.
    /// </summary>
    public required string Reason { get; init; }
    public ulong? ParentAlgoId { get; init; }
    public int? AlgoSliceSeq { get; init; }
}

/// <summary>
/// Pass-5 review (#299) P1. Recorded the moment the modify pipeline
/// observes an AMBIGUOUS gateway dispatch failure (an exception
/// thrown by <c>IEntryPointClient.CancelReplaceAsync</c> after the
/// margin coordinator's <c>PrepareReplaceAsync</c> succeeded AND the
/// intent was registered). The transient margin reservation under
/// <see cref="NewClOrdId"/> is intentionally KEPT — the venue may
/// still send a Replaced ER, in which case <c>CommitReplace</c>
/// converges without re-checking capacity. The
/// <see cref="PendingReplacementRegistry"/> entry is flagged so the
/// AlgoScheduler's TTL sweep can bound the leak if no terminal ER
/// ever arrives.
///
/// <para>
/// <b>Why a WAL event (vs. snapshot-only).</b> The held flag must be
/// durable across a crash that lands BETWEEN the intent's matching
/// <see cref="OrderReplaceRequestedEvent"/> and the next periodic
/// snapshot, otherwise replay re-hydrates the intent without the
/// flag, the TTL sweep never reaps it, and a late Replaced ER drives
/// <c>CommitReplace</c> against a freed-on-restart reservation —
/// breaking the over-allocation invariant. <see cref="EventReplayer"/>
/// re-calls <c>PrepareReplaceAsync</c> on this event to re-establish
/// the transient reservation, and re-marks the registry entry with
/// the persisted <see cref="HeldAtUtc"/> so the post-restart sweep
/// converges on the same TTL deadline the pre-crash sweep would
/// have observed.
/// </para>
///
/// <para>
/// <b>Additive payload.</b> Older binaries skip unknown discriminators
/// with a structured warning (see <see cref="WalEvent"/>'s schema
/// evolution rule), so segments produced with this event remain
/// safely readable by pre-pass-5 readers.
/// </para>
/// </summary>
public sealed record OrderReplaceAmbiguousMarginHeldEvent : WalEvent
{
    public required ulong NewClOrdId { get; init; }
    public required ulong OriginalClOrdId { get; init; }
    public required string EndClientId { get; init; }
    /// <summary>
    /// The upsize-delta-bearing remaining notional the modify
    /// pipeline passed to <c>PrepareReplaceAsync</c>. Replay
    /// re-invokes the coordinator with the same value to re-establish
    /// the held reservation; zero/negative implies a downsize or
    /// non-margin-bearing order (replay still re-runs Prepare so the
    /// transient entry is recreated for a future Commit/Abort).
    /// </summary>
    public required decimal NewRemainingNotional { get; init; }
    /// <summary>
    /// Wall-clock at which the engine observed the ambiguous send.
    /// The TTL sweep ages from this stamp post-restart so a crash
    /// inside the TTL window doesn't reset the deadline.
    /// </summary>
    public required DateTimeOffset HeldAtUtc { get; init; }
}

/// <summary>
/// Every ER routed by the platform — both real EntryPoint reports and
/// synthetic rejections (risk decline / gateway failure). Replay drives
/// <c>ExecutionReportProcessor.Apply</c>, which mutates orders + positions
/// deterministically from the ER fields alone.
/// </summary>
public sealed record ExecutionReportReceivedEvent : WalEvent
{
    public required ulong ClOrdId { get; init; }
    public required string ExecKind { get; init; }
    public required long LeavesQuantity { get; init; }
    public required long CumulativeQuantity { get; init; }
    public required long LastQuantity { get; init; }
    public required decimal LastPrice { get; init; }
    public string? RejectReason { get; init; }
    public required bool Synthetic { get; init; }
    /// <summary>
    /// Original ClOrdID for cancel/replace acks; <c>0</c> when not applicable.
    /// Replay uses it to mutate the original order rather than the cancel-side
    /// ClOrdID (which has no in-memory order).
    /// </summary>
    public ulong OrigClOrdId { get; init; }
    /// <summary>
    /// PR #317 P1. Firm the ER arrived on at the wire (populated by
    /// <c>B3EntryPointClientGateway</c> / <c>MockEntryPointClient</c>).
    /// Nullable + non-required for back-compat with legacy WAL events
    /// written before #317 — a null value on replay means "skip the
    /// cross-firm sanity check" so existing logs hydrate unchanged.
    /// On the live path, <c>ExecutionReportProcessor.Apply</c> rejects
    /// an ER whose <c>FirmId</c> mismatches the resolved order's firm
    /// (counted on <c>trading.er.firm_mismatch_total</c>).
    /// </summary>
    public string? FirmId { get; init; }

    /// <summary>
    /// Q4.7 (#307). Top-of-book snapshot captured at the moment a
    /// Fill / PartialFill ER landed at the host. Populated only for
    /// <c>ExecKind=Fill</c> / <c>PartialFill</c>; all other kinds
    /// persist <c>null</c>. Additive (nullable, no <c>required</c>) so
    /// older WAL segments without the field hydrate as <c>null</c> and
    /// downstream consumers treat them as "no touch captured".
    /// </summary>
    public MarketData.BookTouchSnapshot? BookTouch { get; init; }
}

/// <summary>
/// Kill-switch toggle on either an end-client or a firm. Audit trail for
/// "who pulled the plug, when, and why" — and the only way recovery
/// reconstructs the kill-switch state, since killing is not a side-effect
/// of an exchange ER.
/// </summary>
public sealed record KillSwitchToggledEvent : WalEvent
{
    public required string Scope { get; init; }   // "end-client" | "firm"
    public required string Target { get; init; }
    public required bool Killed { get; init; }    // true=kill, false=revive
    public string? ActorUserId { get; init; }
}

/// <summary>
/// Per-symbol trading halt toggle. Audit trail for "who halted what,
/// when, and why" — and the only way recovery reconstructs the halt
/// set, since halts are an out-of-band admin decision rather than a
/// side-effect of an exchange ER.
///
/// <para>
/// <see cref="Origin"/> distinguishes operator vs venue halts
/// (#370 Stage A). Null on legacy events (pre-#370) — recovery treats
/// the absence as <see cref="B3.Trading.Application.MarketData.HaltOrigin.Operator"/>,
/// matching the single-origin behaviour that existed before the slice.
/// </para>
/// </summary>
public sealed record SymbolHaltToggledEvent : WalEvent
{
    public required string Symbol { get; init; }
    public required bool Halted { get; init; }    // true=halt, false=resume
    public string? ActorUserId { get; init; }

    /// <summary>
    /// Origin of the halt/resume. Null on legacy events; new code
    /// always sets it. See
    /// <see cref="B3.Trading.Application.MarketData.HaltOrigin"/>.
    /// </summary>
    public B3.Trading.Application.MarketData.HaltOrigin? Origin { get; init; }
}

/// <summary>
/// Trading session phase change for a symbol or the venue default
/// (#108). Mirrors the audit-trail posture of <see cref="SymbolHaltToggledEvent"/>:
/// "who moved which scope into which phase, when". Recovery rebuilds
/// the per-symbol overrides + global default by replaying these in
/// arrival order on top of the snapshot.
///
/// <para>When <see cref="Symbol"/> is null/empty the event sets the
/// global default (<c>SetDefaultPhase</c>); otherwise it sets/clears
/// a per-symbol override. <see cref="Cleared"/> = true means "remove
/// the override" (the per-symbol path falls back to the default);
/// the <see cref="Phase"/> field is then advisory only.</para>
/// </summary>
public sealed record SessionPhaseChangedEvent : WalEvent
{
    public string? Symbol { get; init; }
    public required string Phase { get; init; }
    public bool Cleared { get; init; }
    public string? ActorUserId { get; init; }
}

/// <summary>
/// Captures the parent params at submit time. Authoritative source of
/// truth for everything in the algo aggregate except the derived state
/// (FilledQuantity / Status), which is reconstructed during replay from
/// the child <see cref="OrderSubmittedEvent"/> + <see cref="ExecutionReportReceivedEvent"/>
/// stream and the terminal events below. See RFC §4.5.
/// </summary>
public sealed record AlgoCreatedEvent : WalEvent
{
    public required ulong AlgoId { get; init; }
    public required string EndClientId { get; init; }
    public required string FirmId { get; init; }
    public required string Symbol { get; init; }
    public required ulong SecurityId { get; init; }
    public required string Side { get; init; }
    public required string Type { get; init; }   // "Iceberg" | "Twap" | "Vwap" | "Pov" | "Pegged"
    public required long TotalQuantity { get; init; }
    public required DateTimeOffset CreatedAtUtc { get; init; }

    /// <summary>
    /// Q4.1 (#301). Optional sub-account bucket. Inherited by every
    /// child <see cref="OrderSubmittedEvent"/> the engine produces
    /// for this parent. <c>null</c> = master bucket (pre-#301
    /// semantics — every legacy AlgoCreated row).
    /// </summary>
    public string? SubAccountId { get; init; }
    /// <summary>
    /// Iceberg-only: visible slice quantity. Mutually exclusive with
    /// the Twap fields below. Validated by the engine, not the WAL —
    /// the event mirrors whatever the submit pipeline accepted.
    /// </summary>
    public long? IcebergDisplayQuantity { get; init; }
    public decimal? IcebergLimitPrice { get; init; }

    public DateTimeOffset? TwapStartUtc { get; init; }
    public DateTimeOffset? TwapEndUtc { get; init; }
    public int? TwapSliceCount { get; init; }
    public string? TwapChildOrderType { get; init; }   // "Limit" | "Market"
    public decimal? TwapChildPrice { get; init; }

    // Q3.1 (#281) — VWAP fields. Mirror the TWAP block above; only the
    // block matching <see cref="Type"/> = "Vwap" is populated. Additive
    // — older replays / snapshots without these fields stay valid.
    public DateTimeOffset? VwapStartUtc { get; init; }
    public DateTimeOffset? VwapEndUtc { get; init; }
    public string? VwapChildOrderType { get; init; }   // "Limit" | "Market"
    public decimal? VwapChildPrice { get; init; }
    public long? VwapTickIntervalTicks { get; init; }
    public decimal? VwapSliceMaxPct { get; init; }
    public decimal? VwapPriceLimit { get; init; }
    public decimal? VwapParticipationCap { get; init; }

    // Q3.2 (#282) — POV fields. Mirror the VWAP block above; only the
    // block matching <see cref="Type"/> = "Pov" is populated. Additive —
    // older replays / snapshots without these fields stay valid.
    public DateTimeOffset? PovStartUtc { get; init; }
    public DateTimeOffset? PovEndUtc { get; init; }
    public string? PovChildOrderType { get; init; }   // "Limit" | "Market"
    public decimal? PovChildPrice { get; init; }
    public decimal? PovParticipationRate { get; init; }
    public long? PovTickIntervalTicks { get; init; }
    public decimal? PovPriceLimit { get; init; }
    public long? PovMinSliceQty { get; init; }

    // Q3.3 (#283) — Pegged fields. Mirror the POV block above; only the
    // block matching <see cref="Type"/> = "Pegged" is populated. Additive
    // — older replays / snapshots without these fields stay valid.
    public string? PeggedRef { get; init; }                 // PegRef enum name
    public int? PeggedOffsetTicks { get; init; }
    public long? PeggedRepegIntervalTicks { get; init; }
    public decimal? PeggedTickSize { get; init; }
    public string? PeggedChildOrderType { get; init; }      // "Limit" | "Market"
    public decimal? PeggedPriceLimit { get; init; }
}

/// <summary>
/// Recorded when <c>DELETE /algo/{id}</c> reaches the engine, before the
/// child cancels are dispatched. Replay uses it to set the parent to
/// <c>Cancelling</c>; the eventual <see cref="AlgoTerminalStateRecordedEvent"/>
/// promotes it to <c>Cancelled</c>.
/// </summary>
public sealed record AlgoCancelRequestedEvent : WalEvent
{
    public required ulong AlgoId { get; init; }
    public required string FirmId { get; init; }
    public string? ActorUserId { get; init; }
}

/// <summary>
/// Recorded when the parent reaches a terminal state
/// (<c>Completed</c>, <c>Cancelled</c>, <c>Expired</c>, <c>Suspended</c>).
/// The <see cref="Reason"/> is the durable companion that distinguishes
/// "user pulled it" from "venue rejected the third slice in a row".
/// </summary>
public sealed record AlgoTerminalStateRecordedEvent : WalEvent
{
    public required ulong AlgoId { get; init; }
    public required string FirmId { get; init; }
    public required string Status { get; init; }    // AlgoStatus enum name
    public required string Reason { get; init; }    // AlgoTerminalReason enum name
    public required DateTimeOffset AtUtc { get; init; }
}

/// <summary>
/// Q3.1 (#281). Per-slice audit envelope for VWAP parents — captures the
/// target/actual curve gap at slice-emit time. NOT replayed for state
/// reconstruction (the slice itself is recorded via the child
/// <see cref="OrderSubmittedEvent"/>, same as TWAP); the engine treats
/// this event as observability-only so a future omission of the WAL
/// write would not desynchronise recovery. Mirrors the <c>AlgoVwapSliced</c>
/// label called out in the issue body.
/// </summary>
public sealed record AlgoVwapSlicedEvent : WalEvent
{
    public required ulong AlgoId { get; init; }
    public required string FirmId { get; init; }
    public required int SliceSeq { get; init; }
    public required long TargetCumQty { get; init; }
    public required long ExecutedCum { get; init; }
    public required long SliceQty { get; init; }
    public required DateTimeOffset PlannedAtUtc { get; init; }
}

/// <summary>
/// Q3.2 (#282). Per-slice audit envelope for POV parents — captures the
/// market-volume baseline and the resulting share at slice-emit time.
///
/// <para>
/// Pass-1 review (#295) P1#1. Unlike <see cref="AlgoVwapSlicedEvent"/>,
/// this event IS replayed: the additive <see cref="MarketVolumeSeen"/>
/// and <see cref="LastEvaluateAtUtc"/> fields restore the per-POV
/// scheduling baseline so a restart does not under-slice waiting for
/// the in-memory <see cref="MarketData.VolumeCurveEstimator"/> buckets
/// to re-observe the pre-crash cumulative market volume. The slice
/// itself is still recorded via the child <see cref="OrderSubmittedEvent"/>
/// — replay of this event mutates only <see cref="PovProgressBook"/>,
/// never the parent's <c>FilledQuantity</c>.
/// </para>
/// </summary>
public sealed record AlgoPovSlicedEvent : WalEvent
{
    public required ulong AlgoId { get; init; }
    public required string FirmId { get; init; }
    public required int SliceSeq { get; init; }
    public required long CumMarketVolume { get; init; }
    public required long ExecutedCum { get; init; }
    public required long SliceQty { get; init; }
    public required DateTimeOffset PlannedAtUtc { get; init; }

    // Pass-1 review (#295) P1#1. Persisted POV progress so a restart
    // does NOT under-slice waiting for VolumeCurveEstimator's in-memory
    // buckets to catch up to the pre-crash cumulative market volume.
    // Additive — older WAL segments deserialise with default values
    // (0 / default(DateTimeOffset)); engine recovery treats a default
    // LastEvaluateAtUtc as "no prior progress; seed from StartUtc".
    public long MarketVolumeSeen { get; init; }
    public DateTimeOffset LastEvaluateAtUtc { get; init; }
}

/// <summary>
/// Q3.3 (#283). Per-repeg audit envelope for Pegged parents — captures
/// the live reference price, the old child price (cancelled) and the
/// new target price the engine is placing at. NOT replayed for state
/// reconstruction (the cancel + new child are already on the WAL via
/// <see cref="OrderCancelRequestedEvent"/> + <see cref="OrderSubmittedEvent"/>);
/// the engine treats this event as observability-only so a future
/// omission of the WAL write would not desynchronise recovery. Mirrors
/// the <c>AlgoVwapSliced</c> shape called out in the issue body.
/// </summary>
public sealed record AlgoPeggedRepeggedEvent : WalEvent
{
    public required ulong AlgoId { get; init; }
    public required string FirmId { get; init; }
    public required int SliceSeq { get; init; }
    public required string RefKind { get; init; }     // PegRef enum name
    public required decimal RefPrice { get; init; }
    public required decimal OldChildPrice { get; init; }
    public required decimal NewTargetPrice { get; init; }
    public required DateTimeOffset AtUtc { get; init; }
}

/// <summary>
/// Pass-1 review (#296) P1-C. Pre-cancel marker for a Pegged repeg
/// cycle. Persisted BEFORE the engine issues the cancel wire-call so
/// recovery can distinguish "engine-initiated cancel awaiting ack"
/// from "venue-initiated cancel" (which suspends the parent).
///
/// <para>Replay populates <see cref="Algo.PeggedRepegBook"/> with
/// <see cref="CancelledChildClOrdId"/> + audit fields; engine
/// <c>Reconcile</c> reads the book to seed
/// <c>AlgoParentRuntime.RepegPending</c> + the sticky
/// <c>LastRepegCancelledChildId</c> marker so a post-restart
/// cancel-ack ER routes through <c>SubmitNextSliceAsync</c> rather
/// than the <c>VenueCancelled</c>-suspension path. Cleared by
/// <see cref="AlgoPeggedRepegResolvedEvent"/> on cycle completion
/// or by <see cref="AlgoTerminalStateRecordedEvent"/> on parent
/// terminal. Additive: snapshots and WAL segments pre-dating the
/// event deserialise as before.</para>
/// </summary>
public sealed record AlgoPeggedRepegStartedEvent : WalEvent
{
    public required ulong AlgoId { get; init; }
    public required string FirmId { get; init; }
    public required ulong CancelledChildClOrdId { get; init; }
    public required ulong NewClOrdId { get; init; }
    public required decimal TargetPrice { get; init; }
    public required DateTimeOffset AtUtc { get; init; }
}

/// <summary>
/// Pass-1 review (#296) P1-C. Companion to
/// <see cref="AlgoPeggedRepegStartedEvent"/>: emitted when the engine
/// has consumed the cancel-ack ER and successfully submitted the
/// replacement child. Replay clears the pending entry from
/// <c>PeggedRepegBook</c> so post-restart state matches the
/// in-memory steady-state (no pending repeg). Additive.
///
/// <para>Pass-2 review (#296) P1-A. <see cref="Aborted"/> distinguishes
/// the normal cycle-completion path (<c>false</c>) from a roll-back
/// caused by a failed gateway cancel mid-cycle (<c>true</c>): the
/// engine had already persisted the Started marker and called
/// <c>PeggedRepegBook.Set</c> but the wire-call to the venue
/// failed, so no replacement child will be submitted. Replay
/// handling is identical (clear the book entry) — the flag is
/// audit-only so EOD / forensic tooling can tell apart "cycle
/// succeeded" from "cycle aborted; engine will retry next tick".
/// Field is optional: pre-existing Resolved events deserialise as
/// <c>false</c>.</para>
/// </summary>
public sealed record AlgoPeggedRepegResolvedEvent : WalEvent
{
    public required ulong AlgoId { get; init; }
    public required string FirmId { get; init; }
    public required ulong CancelledChildClOrdId { get; init; }
    public required DateTimeOffset AtUtc { get; init; }

    /// <summary>
    /// Pass-2 review (#296) P1-A. <c>true</c> when the cycle was
    /// rolled back because the gateway cancel call failed after the
    /// Started marker was persisted; <c>false</c> (default) for the
    /// normal cancel-ack-then-replace flow. Additive — old segments
    /// decode as <c>false</c>.
    /// </summary>
    public bool Aborted { get; init; }

    /// <summary>
    /// Pass-4 review (#296) P1. Audit-only annotation describing why
    /// the cycle resolved. Null (default) for the steady-state cancel-
    /// ack-then-replace path. Currently emitted values:
    /// <list type="bullet">
    ///   <item><c>FilledBeforeCancelAck</c> — a terminal Fill ER for
    ///   the just-cancelled child reached the engine before (or
    ///   instead of) the cancel-ack, so the quantity was consumed by
    ///   the existing child and no replacement was submitted. The
    ///   companion <see cref="Aborted"/> stays <c>false</c> because
    ///   the cancel did reach the venue (only its ack didn't beat the
    ///   fill); replay still just clears the
    ///   <c>PeggedRepegBook</c> entry. Additive — pre-existing
    ///   segments decode as <c>null</c>.</item>
    /// </list>
    /// </summary>
    public string? Reason { get; init; }
}

/// <summary>
/// Q3.5 (#285). Audit envelope for an algo child cancel-replace
/// (modify) — captures the old and new ClOrdIDs plus the requested
/// quantity/price overrides and the human-readable reason
/// (<c>operator</c>, <c>pegged_repeg</c>, …). NOT replayed for state
/// reconstruction: the actual cancel-replace is already on the WAL
/// via the companion <see cref="OrderReplaceRequestedEvent"/>; this
/// event exists so EOD / forensic tooling can correlate "engine
/// modified a child" back to the algo that drove it without joining
/// across multiple events.
/// </summary>
public sealed record AlgoChildModifiedEvent : WalEvent
{
    public required ulong AlgoId { get; init; }
    public required string FirmId { get; init; }
    public required ulong OldChildClOrdId { get; init; }
    public required ulong NewChildClOrdId { get; init; }
    public long? NewQuantity { get; init; }
    public decimal? NewPrice { get; init; }
    public required string Reason { get; init; }
    public required DateTimeOffset AtUtc { get; init; }
}

/// <summary>
/// Slice 1 of #132. Persists an admin / operator decision to flag a
/// working order as suspected-stale-by-venue (typically after a venue
/// restart that reset its book without our trading-host noticing). The
/// underlying business state is unchanged — replay re-applies the
/// advisory overlay so post-recovery state matches what the operator
/// saw before the restart.
/// </summary>
public sealed record OrderStaledEvent : WalEvent
{
    public required ulong ClOrdId { get; init; }
    public required string FirmId { get; init; }
    public required string Reason { get; init; }
    public required DateTimeOffset StaledAtUtc { get; init; }
    public string? ActorUserId { get; init; }
}

/// <summary>
/// Slice 1 of #132. Records that the staleness overlay was lifted —
/// either because an admin explicitly cleared it or because a real
/// terminal ER arrived (the venue actually still knew the order, so the
/// stale mark was a false positive). Idempotent on replay.
/// </summary>
public sealed record OrderStaleClearedEvent : WalEvent
{
    public required ulong ClOrdId { get; init; }
    public required string FirmId { get; init; }
    public required string ResolvedBy { get; init; }    // "admin" or "er-terminal"
    public string? ActorUserId { get; init; }
}

/// <summary>
/// Sub-issue #169. Recorded the moment a user mints a new bot
/// credential. Replay reconstructs the credential row in
/// <c>InMemoryUserBotCredentialRegistry</c>; the plaintext secret is
/// shown to the caller exactly once at create time and is never
/// included on the WAL. The bcrypt(cost=12) hash is the only secret-
/// derived material persisted.
/// </summary>
public sealed record UserBotCredentialCreatedEvent : WalEvent
{
    public required Guid Id { get; init; }
    public required string UserId { get; init; }
    public required string CredShortId { get; init; }
    public required string Label { get; init; }
    public required string SecretHash { get; init; }
    public required DateTimeOffset CreatedAtUtc { get; init; }

    /// <summary>
    /// Optional SHA-256 client-certificate thumbprint pin (RFC
    /// user-bot-fixp-mtls-v0 §4.3, sub-issue #540). <c>null</c> = unpinned.
    /// Not <c>required</c> so credentials written before mTLS replay as
    /// unpinned with no WAL migration.
    /// </summary>
    public string? BoundCertThumbprint { get; init; }
}

/// <summary>
/// Sub-issue #169. Soft-revoke audit record. Replay flips the row's
/// <c>RevokedAtUtc</c> field — listeners reject the PAT after restore.
/// </summary>
public sealed record UserBotCredentialRevokedEvent : WalEvent
{
    public required Guid Id { get; init; }
    public required string UserId { get; init; }
    public required DateTimeOffset RevokedAtUtc { get; init; }
}

/// <summary>
/// Sub-issue #540 (RFC user-bot-fixp-mtls-v0 §4.3). Records a change to a
/// credential's client-certificate thumbprint pin: set, re-pin, or clear.
/// <see cref="BoundCertThumbprint"/> is <c>null</c> when the operator clears
/// the pin (credential becomes unpinned again). The thumbprint is non-secret.
/// </summary>
public sealed record UserBotCredentialCertBindingChangedEvent : WalEvent
{
    public required Guid Id { get; init; }
    public required string UserId { get; init; }
    public required DateTimeOffset ChangedAtUtc { get; init; }
    public string? BoundCertThumbprint { get; init; }
}

/// <summary>
/// Sub-issue #170. First-access allocation of a per-credential FIXP
/// session: the platform mints a stable <see cref="SessionId"/> (uint32,
/// non-zero) and seeds <see cref="InitialVer"/>=1. Replay reconstructs
/// the row in <c>InMemoryUserBotSessionRegistry</c>.
/// </summary>
public sealed record BotSessionInitializedEvent : WalEvent
{
    public required Guid CredentialId { get; init; }
    public required uint SessionId { get; init; }
    public required ulong InitialVer { get; init; }
    public required DateTimeOffset CreatedAtUtc { get; init; }
}

/// <summary>
/// Sub-issue #170. Forced version bump (RFC §4.8). Persisted **before**
/// the platform sends any bot-observable response carrying the new ver,
/// guarded by an explicit <c>FlushAsync</c> fence so a crash cannot let
/// the bot observe a newVer that recovery would roll back. Reasons in
/// v0: <c>"single-active-violation"</c> (sub-issue D),
/// <c>"overflow"</c> (sub-issue G), <c>"operator"</c> (admin endpoint).
/// </summary>
public sealed record BotSessionVerAdvancedEvent : WalEvent
{
    public required Guid CredentialId { get; init; }
    public required ulong OldVer { get; init; }
    public required ulong NewVer { get; init; }
    public required string Reason { get; init; }
}

/// <summary>
/// Sub-issue #172 (F). Periodic outbound-seq watermark for a credential's
/// FIXP session. Appended on the cadence defined by RFC §4.8: every 5
/// seconds OR every 100 outbound messages, whichever comes first. NOT
/// appended per-ER (would double WAL pressure of FIXP-originated orders).
///
/// <para>This event is a "best-effort durability watermark" — it does
/// NOT need <c>FlushAsync</c>. Recovery seeds the registry with the
/// most recent value seen; sub-issue G's retransmit treats requests
/// older than the checkpoint as unreplayable.</para>
/// </summary>
public sealed record BotSessionSeqAdvancedEvent : WalEvent
{
    public required Guid CredentialId { get; init; }
    public required ulong CheckpointedOutboundSeq { get; init; }
    public required DateTimeOffset At { get; init; }
}

/// <summary>
/// Q1.3 (#255). Recorded the moment the GTD scheduler decides an order
/// has reached its <see cref="Domain.Order.GoodTillDate"/> and dispatches
/// a cancel for it. Strictly informational: the cancel itself flows
/// through the regular <c>OrderCancelService</c> pipeline (which
/// produces the eventual <c>ExecutionReportReceivedEvent</c> with
/// <c>Canceled</c>), and this event is what lets downstream sinks
/// project the cancel as <c>kind=Expired</c> instead of the usual
/// <c>kind=Canceled</c>. <see cref="Reason"/> is an open-ended string
/// (<c>"Gtd"</c> in v0; future auction-expired flows reuse the same
/// envelope with a different reason). Replay is a no-op — the
/// downstream <c>Canceled</c> ER (also on the WAL) drives all in-memory
/// state mutation; this event only carries audit / WS-projection
/// metadata. Additive: older WAL segments without this event replay
/// unchanged.
/// </summary>
public sealed record OrderExpiredEvent : WalEvent
{
    public required ulong ClOrdId { get; init; }
    public required string Reason { get; init; }
    public required DateTimeOffset AtUtc { get; init; }
}

/// <summary>
/// Q2.2 (#269). Operator-driven cash deposit or withdrawal for an
/// end-client. The platform's cash projection (see
/// <see cref="B3.Trading.Application.CashKeeper"/>) is built from this
/// event stream ONLY — it is intentionally decoupled from
/// <see cref="ExecutionReportReceivedEvent"/> fills (which feed the
/// separate <see cref="B3.Trading.Application.CashLedger"/> used by the
/// margin pipeline). Folding fill-driven cash deltas into the same
/// projection is deferred to the P&amp;L engine slice (#271) so the
/// audit-grade ledger here stays a pure record of operator activity.
///
/// <para>
/// Field semantics: <see cref="Kind"/> is the literal string
/// <c>"Deposit"</c> or <c>"Withdrawal"</c> (mirrors the enum-name
/// stability convention used by other WAL records, e.g. TIF). Amount is
/// strictly positive; sign is implied by <see cref="Kind"/>. Currency
/// is whitelisted to <c>"BRL"</c> in v0; future multi-currency expands
/// the whitelist without changing the wire shape. <see cref="Reference"/>
/// is operator free-form (ticket id, journal note); persisted verbatim
/// for the operator audit trail. <see cref="OperatorId"/> is the JWT
/// <c>sub</c> of the admin who issued the call (nullable to match the
/// existing <c>ActorUserId</c> nullability on other admin-side events).
/// </para>
/// </summary>
public sealed record CashLedgerEvent : WalEvent
{
    public required string EndClientId { get; init; }
    /// <summary>
    /// <c>"Deposit"</c> or <c>"Withdrawal"</c>. Property is named
    /// <c>Operation</c> (not <c>Kind</c>) so it does not collide with
    /// the polymorphism discriminator on <see cref="WalEvent"/>, which
    /// is also serialised as <c>"kind"</c>. The HTTP request payload
    /// continues to use <c>kind</c> at the API surface (see
    /// <c>CashLedgerRequest</c>); the API handler maps it onto this
    /// field at dispatch time.
    /// </summary>
    public required string Operation { get; init; }
    public required decimal Amount { get; init; }
    public required string Currency { get; init; }
    public string? Reference { get; init; }
    public string? OperatorId { get; init; }
}

/// <summary>
/// Q2.3 (#270). Per-fill fee accrual emitted by
/// <c>ExecutionReportProcessor</c> immediately after a successful
/// PartialFill / Fill ER is folded into <c>PositionKeeper</c> /
/// <c>CashLedger</c>. Lands AFTER the originating
/// <see cref="ExecutionReportReceivedEvent"/> in the WAL by virtue of
/// the dispatcher's append ordering — same lock; sequential
/// <c>Append</c> calls — so a tail-replay walking events in seq order
/// always sees the fill before its fees.
///
/// <para>
/// <b>ExecutionId stability for idempotence.</b>
/// <see cref="ExecutionId"/> is the deterministic combination
/// <c>{ClOrdId}:{CumulativeQuantityAfterFill}</c>: a re-applied fill
/// at the same cumulative quantity (FIXP retransmit, WAL replay) will
/// re-produce the same id, and <c>FeeKeeper.Apply</c> deduplicates on
/// it before advancing the totals. The cum-after-fill choice — rather
/// than the wire <c>LastQuantity</c> — matches
/// <see cref="B3.Trading.Domain.Order.ApplyCumulativeFill"/>'s own
/// idempotence pivot, so the keeper and the order book never disagree
/// on whether a given fill was already booked.
/// </para>
///
/// <para>
/// <b>Fields.</b> <see cref="EventSeq"/> is intentionally absent: seq
/// is assigned by the WAL store on append. <see cref="Notional"/> /
/// <see cref="Brokerage"/> / <see cref="Emolumentos"/> /
/// <see cref="Liquidacao"/> / <see cref="Total"/> are pre-computed
/// (every component rounded to 2dp <c>AwayFromZero</c>; total is the
/// sum of the rounded components) so consumers downstream do not
/// re-derive them and risk drifting from the calculator's rounding.
/// </para>
/// </summary>
public sealed record FeeAccruedEvent : WalEvent
{
    public required ulong ClOrdId { get; init; }

    /// <summary>
    /// Stable per-fill identity — see the type-level remarks. Format:
    /// <c>{ClOrdId}:{CumulativeQuantityAfterFill}</c>. Plain string so
    /// downstream consumers can index/dedupe without reconstructing the
    /// composite key.
    /// </summary>
    public required string ExecutionId { get; init; }

    public required string EndClientId { get; init; }
    public required string Symbol { get; init; }

    /// <summary>
    /// Order side at the time of the fill, serialised as the enum name
    /// (<c>"Buy"</c> / <c>"Sell"</c>) to keep the wire format stable
    /// across enum renumberings.
    /// </summary>
    public required string Side { get; init; }

    /// <summary>
    /// Forward-delta quantity for this fill (<i>not</i> cumulative);
    /// matches the <c>delta</c> booked to <c>PositionKeeper</c> /
    /// <c>CashLedger</c> in the same dispatch.
    /// </summary>
    public required long FillQuantity { get; init; }

    public required decimal FillPrice { get; init; }
    public required decimal Notional { get; init; }
    public required decimal Brokerage { get; init; }
    public required decimal Emolumentos { get; init; }
    public required decimal Liquidacao { get; init; }
    public required decimal Total { get; init; }

    /// <summary>Q4.1 (#301). Optional sub-account bucket; inherited
    /// from the originating order. Legacy rows hydrate as <c>null</c>
    /// (master).</summary>
    public string? SubAccountId { get; init; }
}

/// <summary>
/// Q2.4 (#271). Realized P&amp;L delta emitted by
/// <c>ExecutionReportProcessor</c> immediately after a Fill ER on the
/// opposing side of the current position has been folded into
/// <c>PositionKeeper</c>. Lands AFTER the originating
/// <see cref="ExecutionReportReceivedEvent"/> in the WAL by virtue of
/// the dispatcher's append ordering — the same sequencing rule that
/// governs <see cref="FeeAccruedEvent"/>.
///
/// <para>
/// <b>Avg-cost basis (decision documented in #271).</b> Realized
/// proceeds are computed as <c>(price - avgPrice) * closedQty</c> for
/// long positions and <c>(avgPrice - price) * closedQty</c> for shorts,
/// where <c>avgPrice</c> is the <i>pre-fill</i>
/// <c>Position.AverageEntryPrice</c> and <c>closedQty</c> is the portion
/// of the fill that offset the pre-fill quantity (the remainder, if the
/// position flipped past zero, opens fresh and contributes no realized
/// component). Avg-cost is the B3/CVM-standard basis for cash equities
/// and avoids the per-lot bookkeeping FIFO would require.
/// </para>
///
/// <para>
/// <b>Idempotence.</b> <see cref="ExecutionId"/> uses the same stable
/// <c>{ClOrdId}:{CumulativeQuantityAfterFill}</c> shape as
/// <see cref="FeeAccruedEvent.ExecutionId"/>; <c>PnlKeeper.Apply</c>
/// dedupes on it so a re-applied event (FIXP retransmit, WAL replay)
/// is a no-op.
/// </para>
///
/// <para>
/// <b>GROSS, not NET.</b> <see cref="DeltaRealized"/> /
/// <see cref="RunningTotal"/> are pre-fee. The Q2.5 statement (#272)
/// composes fees on top.
/// </para>
/// </summary>
public sealed record RealizedPnlEvent : WalEvent
{
    public required ulong ClOrdId { get; init; }

    /// <summary>
    /// Stable per-fill identity — see the type-level remarks. Format:
    /// <c>{ClOrdId}:{CumulativeQuantityAfterFill}</c>.
    /// </summary>
    public required string ExecutionId { get; init; }

    public required string EndClientId { get; init; }
    public required string Symbol { get; init; }

    /// <summary>
    /// UTC day key used to bucket <see cref="DeltaRealized"/>; matches
    /// <c>DateOnly.FromDateTime(TimestampUtc.UtcDateTime)</c>. Persisted
    /// explicitly so a future timezone migration cannot retroactively
    /// re-bucket historical events.
    /// </summary>
    public required DateOnly DayKey { get; init; }

    public required decimal DeltaRealized { get; init; }

    /// <summary>
    /// Cumulative realized for (<see cref="EndClientId"/>,
    /// <see cref="Symbol"/>, <see cref="DayKey"/>) AFTER applying
    /// <see cref="DeltaRealized"/>. Persisted so a snapshot+tail
    /// recovery treats the durable value as authoritative even if the
    /// avg-cost calculation in the keeper has drifted between runs.
    /// </summary>
    public required decimal RunningTotal { get; init; }

    /// <summary>Q4.1 (#301). Optional sub-account bucket; inherited
    /// from the originating order. Legacy rows hydrate as <c>null</c>
    /// (master).</summary>
    public string? SubAccountId { get; init; }

    /// <summary>
    /// Q4.1 (#301). Firm namespace for sub-account fan-out. Required
    /// whenever <see cref="SubAccountId"/> is set so the replayer can
    /// route the delta into the firm-scoped
    /// <see cref="B3.Trading.Application.SubAccountPnlKeeper"/> without
    /// depending on the <see cref="B3.Trading.Application.WorkingOrderBook"/>
    /// having been restored first. Pre-#301 WAL segments and
    /// post-#301 master-bucket rows (no <c>SubAccountId</c>) hydrate
    /// this as <c>null</c>; the fan-out is short-circuited in those
    /// cases.
    /// </summary>
    public string? FirmId { get; init; }
}

/// <summary>
/// Q4.1 (#301). Recorded when a new sub-account is registered for a
/// firm via <c>POST /sub-accounts</c>. The
/// <c>SubAccountsRegistry</c> rehydrates the in-memory set on
/// recovery by replaying this event family (CREATE / DEACTIVATE).
/// Sub-accounts are namespaced per-firm: <c>(FirmId, Id)</c> is the
/// uniqueness key.
/// </summary>
public sealed record SubAccountCreatedEvent : WalEvent
{
    public required string FirmId { get; init; }
    public required string Id { get; init; }
    public string? DisplayName { get; init; }
    public string? ActorUserId { get; init; }
}

/// <summary>
/// Q4.1 (#301). Soft-delete companion to
/// <see cref="SubAccountCreatedEvent"/>. The registry marks the
/// matching entry inactive; history endpoints continue to surface
/// the id, but the submit pipeline rejects new orders against it
/// (the registry returns <c>false</c> from <c>IsActive</c>).
/// Re-issuing a CREATE for the same id revives it.
/// </summary>
public sealed record SubAccountDeactivatedEvent : WalEvent
{
    public required string FirmId { get; init; }
    public required string Id { get; init; }
    public string? ActorUserId { get; init; }
}

/// <summary>
/// Q4.5 (#305). Single envelope for every operationally-significant
/// audit event the trading host emits — login attempts, 2FA
/// lifecycle, admin role/config mutations. Persisted on the WAL so
/// the audit surface survives crash + restart and is reconstructible
/// from segments alone (forensic replay). Additive type — older
/// readers that don't know <c>"audit.log"</c> skip the record with
/// a structured warning (see <see cref="WalEvent"/> remarks),
/// preserving forward-compat.
///
/// <para>The <see cref="EventType"/> field is a hierarchical string
/// (e.g. <c>"auth.login.success"</c>, <c>"admin.config.change"</c>)
/// so the read-path filter supports both exact match and prefix
/// glob (<c>"auth.*"</c>) without an enum bound on the WAL.
/// <see cref="Details"/> carries call-site-specific context
/// (endpoint path, target user, before/after summary) as a flat
/// string map so the on-disk schema is stable as new capture sites
/// land. Audit replay folds these into an in-memory ring-buffer
/// keeper — see <c>AuditLogKeeper</c> — which is the source of
/// truth for <c>GET /admin/audit</c>; no snapshot field is
/// reserved (the keeper rehydrates from a WAL tail on recovery,
/// bounded by its configured retention cap).</para>
/// </summary>
public sealed record AuditLogEvent : WalEvent
{
    /// <summary>Hierarchical event type, e.g. <c>auth.login.success</c>.</summary>
    public required string EventType { get; init; }

    /// <summary>Outcome class: <c>success</c>, <c>failure</c>, or <c>denied</c>.</summary>
    public required string Outcome { get; init; }

    /// <summary>Acting user's JWT subject when authenticated; null for anonymous capture sites (e.g. failed login of an unknown user).</summary>
    public string? ActorUserId { get; init; }

    /// <summary>Username the client presented at the capture site. May differ from <see cref="ActorUserId"/> on failed-login paths.</summary>
    public string? ActorUsername { get; init; }

    /// <summary>Firm assigned at the capture site (success branch) or the actor's firm claim. Null when not resolvable.</summary>
    public string? ActorFirm { get; init; }

    /// <summary>Role claim at the capture site. Null when not resolvable.</summary>
    public string? ActorRole { get; init; }

    /// <summary>Source IP from <c>HttpContext.Connection.RemoteIpAddress</c>, normalised to its string form. Null for non-HTTP capture sites.</summary>
    public string? SourceIp { get; init; }

    /// <summary>HTTP path being exercised at the capture site (e.g. <c>/auth/login</c>, <c>/admin/cash</c>). Null for non-HTTP capture sites.</summary>
    public string? ResourcePath { get; init; }

    /// <summary>Machine-readable reason code, especially for failure / denied outcomes (<c>unknown_user</c>, <c>bad_password</c>, <c>locked</c>, <c>2fa_wrong_code</c>, …).</summary>
    public string? ReasonCode { get; init; }

    /// <summary>Free-form per-capture-site context — endpoint args, before/after summaries, target user, etc. Bounded to small string values by convention so the WAL record stays compact.</summary>
    public Dictionary<string, string>? Details { get; init; }
}

/// <summary>
/// #432. Records a venue <c>BusinessReject</c> — a structural rejection
/// of an inbound message (malformed payload, unknown <c>SecurityID</c>,
/// outside trading hours, etc.) that never produces an
/// <see cref="ExecutionReportReceivedEvent"/>. Persisted so the operator
/// can reconcile "request sent but no ER" gaps without log scraping and
/// so the WAL replay reconstructs the audit trail.
///
/// <para>
/// Has no ClOrdID anchor — the venue references the inbound seqnum
/// (<see cref="RefSeqNum"/>) rather than a business identifier. The
/// event is replay-inert: it does not mutate order state. It exists
/// purely for audit / history visibility.
/// </para>
/// </summary>
public sealed record BusinessRejectReceivedEvent : WalEvent
{
    /// <summary>Firm the reject arrived on (gateway-stamped).</summary>
    public required string FirmId { get; init; }
    /// <summary>FIX <c>RefSeqNum</c> — session seqnum of the rejected inbound message.</summary>
    public required ulong RefSeqNum { get; init; }
    /// <summary>FIX <c>BusinessRejectReason</c> code as emitted by the venue.</summary>
    public required int RejectReason { get; init; }
    /// <summary>Free-form detail string from the venue.</summary>
    public string? Text { get; init; }
    /// <summary>Venue session inbound seqnum of the reject message itself.</summary>
    public required ulong SeqNum { get; init; }
    /// <summary>Venue <c>SendingTime</c> from the reject envelope.</summary>
    public required DateTimeOffset SendingTime { get; init; }
}
