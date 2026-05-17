using System.Text.Json;
using System.Text.Json.Serialization;

namespace B3.Trading.Application.Persistence;

/// <summary>
/// Source-generated <see cref="JsonSerializerContext"/> covering every
/// <see cref="WalEvent"/> derived payload. Used by the WAL serialise /
/// deserialise paths in <c>FileEventStore</c> (and by the EOD materialiser
/// when it walks segments) to avoid the per-call reflection cost of
/// <see cref="System.Text.Json.JsonSerializer"/>'s reflection-based
/// converters. Polymorphic dispatch (the <c>"kind"</c> discriminator) is
/// preserved by the existing <see cref="JsonPolymorphicAttribute"/> on
/// <see cref="WalEvent"/>.
///
/// <para>
/// <b>Drop-in compatibility (RFC §6.1).</b> The context inherits the
/// <see cref="System.Text.Json.JsonSerializerDefaults.Web"/> defaults
/// (camelCase property names on write, case-insensitive on read,
/// <c>JsonNumberHandling.AllowReadingFromString</c>) — byte-for-byte the
/// same options the WAL has used since day one. New segments produced
/// by the source-generated path are still readable by the old reflection-
/// based deserialiser, and old segments are still readable by the new
/// path. Property round-trip tests pin the equivalence so a sourcegen
/// regression fails CI before recovery breaks at runtime.
/// </para>
/// </summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(WalEvent))]
[JsonSerializable(typeof(OrderSubmittedEvent))]
[JsonSerializable(typeof(OrderCancelRequestedEvent))]
[JsonSerializable(typeof(OrderReplaceRequestedEvent))]
[JsonSerializable(typeof(ExecutionReportReceivedEvent))]
[JsonSerializable(typeof(KillSwitchToggledEvent))]
[JsonSerializable(typeof(SymbolHaltToggledEvent))]
[JsonSerializable(typeof(SessionPhaseChangedEvent))]
[JsonSerializable(typeof(AlgoCreatedEvent))]
[JsonSerializable(typeof(AlgoCancelRequestedEvent))]
[JsonSerializable(typeof(AlgoTerminalStateRecordedEvent))]
[JsonSerializable(typeof(AlgoVwapSlicedEvent))]
[JsonSerializable(typeof(AlgoPovSlicedEvent))]
[JsonSerializable(typeof(OrderStaledEvent))]
[JsonSerializable(typeof(OrderStaleClearedEvent))]
[JsonSerializable(typeof(UserBotCredentialCreatedEvent))]
[JsonSerializable(typeof(UserBotCredentialRevokedEvent))]
[JsonSerializable(typeof(BotSessionInitializedEvent))]
[JsonSerializable(typeof(BotSessionVerAdvancedEvent))]
[JsonSerializable(typeof(BotSessionSeqAdvancedEvent))]
[JsonSerializable(typeof(BotOrderMapping))]
[JsonSerializable(typeof(OrderExpiredEvent))]
[JsonSerializable(typeof(CashLedgerEvent))]
[JsonSerializable(typeof(FeeAccruedEvent))]
[JsonSerializable(typeof(RealizedPnlEvent))]
public sealed partial class WalEventJsonContext : JsonSerializerContext
{
}
