using System.Text.Json.Serialization;

namespace B3.Trading.Application.Persistence;

/// <summary>
/// Base type for every event written to the WAL. Each derived record is
/// serialised as a single JSON object inside a length+CRC framed record on
/// disk. The <see cref="JsonPolymorphicAttribute"/> discriminator keeps the
/// schema explicit and forward-compatible: unknown subtypes are rejected
/// loudly during recovery rather than silently mis-applied.
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
[JsonDerivedType(typeof(ExecutionReportReceivedEvent), "er.received")]
[JsonDerivedType(typeof(KillSwitchToggledEvent), "killswitch.toggled")]
[JsonDerivedType(typeof(SymbolHaltToggledEvent), "symbol-halt.toggled")]
[JsonDerivedType(typeof(SessionPhaseChangedEvent), "session-phase.changed")]
[JsonDerivedType(typeof(AlgoCreatedEvent), "algo.created")]
[JsonDerivedType(typeof(AlgoCancelRequestedEvent), "algo.cancel-requested")]
[JsonDerivedType(typeof(AlgoTerminalStateRecordedEvent), "algo.terminal")]
[JsonDerivedType(typeof(OrderStaledEvent), "order.staled")]
[JsonDerivedType(typeof(OrderStaleClearedEvent), "order.stale-cleared")]
[JsonDerivedType(typeof(UserBotCredentialCreatedEvent), "userbot.cred.created")]
[JsonDerivedType(typeof(UserBotCredentialRevokedEvent), "userbot.cred.revoked")]
[JsonDerivedType(typeof(BotSessionInitializedEvent), "userbot.session.initialized")]
[JsonDerivedType(typeof(BotSessionVerAdvancedEvent), "userbot.session.ver-advanced")]
[JsonDerivedType(typeof(OrderCancelRequestedEvent), "order.cancel-requested")]
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
/// </summary>
public sealed record SymbolHaltToggledEvent : WalEvent
{
    public required string Symbol { get; init; }
    public required bool Halted { get; init; }    // true=halt, false=resume
    public string? ActorUserId { get; init; }
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
    public required string Type { get; init; }   // "Iceberg" | "Twap"
    public required long TotalQuantity { get; init; }
    public required DateTimeOffset CreatedAtUtc { get; init; }
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
