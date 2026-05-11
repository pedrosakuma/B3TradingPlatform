using System.Text.Json;
using System.Text.Json.Nodes;
using B3.Trading.Application.Persistence;

namespace B3.Trading.Application.Tests.Persistence;

/// <summary>
/// P2 (RFC §6.1). Pins the contract that <see cref="WalEventJsonContext"/>
/// is a drop-in replacement for the reflection-based serialiser the WAL
/// has used since day one. Every <see cref="WalEvent"/>-derived payload:
///
/// <list type="number">
///   <item>round-trips through the source-generated context (serialise →
///         deserialise → structural equality);</item>
///   <item>produces byte-identical UTF-8 JSON to
///         <c>JsonSerializer.SerializeToUtf8Bytes(payload, JsonSerializerDefaults.Web)</c>,
///         so existing on-disk segments stay readable by both paths and a
///         rollback to the reflection path keeps working.</item>
/// </list>
///
/// <para>If a future <see cref="WalEvent"/> subtype is added without being
/// registered on <see cref="WalEventJsonContext"/>, the polymorphic
/// dispatch test below will fail loudly — sourcegen polymorphism only
/// works for types listed via <see cref="System.Text.Json.Serialization.JsonSerializableAttribute"/>.</para>
/// </summary>
public class WalEventJsonContextRoundTripTests
{
    private static readonly JsonSerializerOptions ReflectionOpts = new(JsonSerializerDefaults.Web);

    public static TheoryData<string, WalEvent> Payloads()
    {
        var ts = new DateTimeOffset(2024, 6, 15, 12, 34, 56, 789, TimeSpan.Zero);
        var credId = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var data = new TheoryData<string, WalEvent>();

        data.Add("OrderSubmitted+Bot", new OrderSubmittedEvent
        {
            TimestampUtc = ts,
            ClOrdId = 42UL,
            EndClientId = "bot:b3t_abc",
            FirmId = "default",
            Symbol = "PETR4",
            SecurityId = 4321UL,
            Side = "Buy",
            Type = "Limit",
            Quantity = 100,
            Price = 12.34m,
            ParentAlgoId = 7UL,
            AlgoSliceSeq = 3,
            BotMapping = new BotOrderMapping(credId, 9999UL),
        });

        data.Add("OrderSubmitted+Manual", new OrderSubmittedEvent
        {
            TimestampUtc = ts,
            ClOrdId = 7UL,
            EndClientId = "alice",
            FirmId = "default",
            Symbol = "VALE3",
            SecurityId = 9876UL,
            Side = "Sell",
            Type = "Market",
            Quantity = 10,
        });

        data.Add("OrderCancelRequested+Bot", new OrderCancelRequestedEvent
        {
            TimestampUtc = ts,
            CancelClOrdId = 200UL,
            OriginalClOrdId = 42UL,
            OwnerEndClientId = "bot:b3t_abc",
            BotMapping = new BotOrderMapping(credId, 5555UL),
        });

        data.Add("OrderCancelRequested+Manual", new OrderCancelRequestedEvent
        {
            TimestampUtc = ts,
            CancelClOrdId = 200UL,
            OriginalClOrdId = 42UL,
            OwnerEndClientId = "alice",
        });

        data.Add("OrderReplaceRequested", new OrderReplaceRequestedEvent
        {
            TimestampUtc = ts,
            OriginalClOrdId = 1UL,
            NewClOrdId = 2UL,
            EndClientId = "alice",
            FirmId = "default",
            Symbol = "PETR4",
            SecurityId = 4321UL,
            Side = "Buy",
            Type = "Limit",
            NewQuantity = 50,
            NewPrice = 13.50m,
            ParentAlgoId = 99UL,
            AlgoSliceSeq = 1,
        });

        data.Add("ExecutionReportReceived", new ExecutionReportReceivedEvent
        {
            TimestampUtc = ts,
            ClOrdId = 42UL,
            ExecKind = "PartialFill",
            LeavesQuantity = 50,
            CumulativeQuantity = 50,
            LastQuantity = 50,
            LastPrice = 12.30m,
            RejectReason = null,
            Synthetic = false,
            OrigClOrdId = 0UL,
        });

        data.Add("ExecutionReportReceived+SyntheticReject", new ExecutionReportReceivedEvent
        {
            TimestampUtc = ts,
            ClOrdId = 99UL,
            ExecKind = "Rejected",
            LeavesQuantity = 0,
            CumulativeQuantity = 0,
            LastQuantity = 0,
            LastPrice = 0m,
            RejectReason = "risk-decline",
            Synthetic = true,
            OrigClOrdId = 0UL,
        });

        data.Add("KillSwitchToggled", new KillSwitchToggledEvent
        {
            TimestampUtc = ts,
            Scope = "firm",
            Target = "default",
            Killed = true,
            ActorUserId = "ops",
        });

        data.Add("SymbolHaltToggled", new SymbolHaltToggledEvent
        {
            TimestampUtc = ts,
            Symbol = "PETR4",
            Halted = true,
            ActorUserId = null,
        });

        data.Add("SessionPhaseChanged+PerSymbol", new SessionPhaseChangedEvent
        {
            TimestampUtc = ts,
            Symbol = "PETR4",
            Phase = "Continuous",
            Cleared = false,
            ActorUserId = "ops",
        });

        data.Add("SessionPhaseChanged+GlobalCleared", new SessionPhaseChangedEvent
        {
            TimestampUtc = ts,
            Symbol = null,
            Phase = "Continuous",
            Cleared = true,
        });

        data.Add("AlgoCreated+Iceberg", new AlgoCreatedEvent
        {
            TimestampUtc = ts,
            AlgoId = 1001UL,
            EndClientId = "alice",
            FirmId = "default",
            Symbol = "PETR4",
            SecurityId = 4321UL,
            Side = "Buy",
            Type = "Iceberg",
            TotalQuantity = 1000,
            CreatedAtUtc = ts,
            IcebergDisplayQuantity = 100,
            IcebergLimitPrice = 12.34m,
        });

        data.Add("AlgoCreated+Twap", new AlgoCreatedEvent
        {
            TimestampUtc = ts,
            AlgoId = 1002UL,
            EndClientId = "alice",
            FirmId = "default",
            Symbol = "VALE3",
            SecurityId = 9876UL,
            Side = "Sell",
            Type = "Twap",
            TotalQuantity = 5000,
            CreatedAtUtc = ts,
            TwapStartUtc = ts,
            TwapEndUtc = ts.AddMinutes(30),
            TwapSliceCount = 6,
            TwapChildOrderType = "Limit",
            TwapChildPrice = 80.00m,
        });

        data.Add("AlgoCancelRequested", new AlgoCancelRequestedEvent
        {
            TimestampUtc = ts,
            AlgoId = 1001UL,
            FirmId = "default",
            ActorUserId = "alice",
        });

        data.Add("AlgoTerminalStateRecorded", new AlgoTerminalStateRecordedEvent
        {
            TimestampUtc = ts,
            AlgoId = 1001UL,
            FirmId = "default",
            Status = "Cancelled",
            Reason = "OperatorCancel",
            AtUtc = ts,
        });

        data.Add("OrderStaled", new OrderStaledEvent
        {
            TimestampUtc = ts,
            ClOrdId = 42UL,
            FirmId = "default",
            Reason = "venue-restart",
            StaledAtUtc = ts,
            ActorUserId = "ops",
        });

        data.Add("OrderStaleCleared", new OrderStaleClearedEvent
        {
            TimestampUtc = ts,
            ClOrdId = 42UL,
            FirmId = "default",
            ResolvedBy = "er-terminal",
        });

        data.Add("UserBotCredentialCreated", new UserBotCredentialCreatedEvent
        {
            TimestampUtc = ts,
            Id = credId,
            UserId = "alice",
            CredShortId = "b3t_abc",
            Label = "my-bot",
            SecretHash = "$2a$12$abcdefghijklmnopqrstuv",
            CreatedAtUtc = ts,
        });

        data.Add("UserBotCredentialRevoked", new UserBotCredentialRevokedEvent
        {
            TimestampUtc = ts,
            Id = credId,
            UserId = "alice",
            RevokedAtUtc = ts,
        });

        data.Add("BotSessionInitialized", new BotSessionInitializedEvent
        {
            TimestampUtc = ts,
            CredentialId = credId,
            SessionId = 12345u,
            InitialVer = 1UL,
            CreatedAtUtc = ts,
        });

        data.Add("BotSessionVerAdvanced", new BotSessionVerAdvancedEvent
        {
            TimestampUtc = ts,
            CredentialId = credId,
            OldVer = 1UL,
            NewVer = 2UL,
            Reason = "single-active-violation",
        });

        data.Add("BotSessionSeqAdvanced", new BotSessionSeqAdvancedEvent
        {
            TimestampUtc = ts,
            CredentialId = credId,
            CheckpointedOutboundSeq = 100UL,
            At = ts,
        });

        return data;
    }

    [Theory]
    [MemberData(nameof(Payloads))]
    public void SourceGen_RoundTrips_Polymorphically(string label, WalEvent original)
    {
        _ = label;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(original, WalEventJsonContext.Default.WalEvent);
        var back = JsonSerializer.Deserialize(bytes, WalEventJsonContext.Default.WalEvent);

        Assert.NotNull(back);
        Assert.IsType(original.GetType(), back);
        // Records implement structural equality by default — every public
        // property participates, so this asserts every field round-trips.
        Assert.Equal(original, back);
    }

    [Theory]
    [MemberData(nameof(Payloads))]
    public void SourceGen_OutputIsByteIdenticalToReflectionPath(string label, WalEvent original)
    {
        _ = label;
        var sourceGenBytes = JsonSerializer.SerializeToUtf8Bytes(original, WalEventJsonContext.Default.WalEvent);
        var reflectionBytes = JsonSerializer.SerializeToUtf8Bytes<WalEvent>(original, ReflectionOpts);

        // Byte-for-byte equality is the strongest guarantee that on-disk
        // WAL format is unchanged: any divergence (property order, casing,
        // number formatting, escaping) shows up here before it shows up
        // as a recovery mismatch on a rolled-back deploy.
        Assert.Equal(reflectionBytes, sourceGenBytes);
    }

    [Theory]
    [MemberData(nameof(Payloads))]
    public void ReflectionWritten_Bytes_ParseViaSourceGen_BackToSameEvent(string label, WalEvent original)
    {
        _ = label;
        // Existing WAL segments on disk were written by the reflection
        // path. The source-gen deserialiser must read them back to a
        // structurally-equal event.
        var reflectionBytes = JsonSerializer.SerializeToUtf8Bytes<WalEvent>(original, ReflectionOpts);
        var back = JsonSerializer.Deserialize(reflectionBytes, WalEventJsonContext.Default.WalEvent);

        Assert.NotNull(back);
        Assert.IsType(original.GetType(), back);
        Assert.Equal(original, back);
    }

    [Theory]
    [MemberData(nameof(Payloads))]
    public void SourceGenWritten_Bytes_ParseViaReflection_BackToSameEvent(string label, WalEvent original)
    {
        _ = label;
        // Rollback safety: if we revert this PR, the previous reflection
        // deserialiser must still understand segments that the source-gen
        // path wrote. (This is implicit in the byte-equivalence test above
        // but stated independently as an explicit invariant.)
        var sourceGenBytes = JsonSerializer.SerializeToUtf8Bytes(original, WalEventJsonContext.Default.WalEvent);
        var back = JsonSerializer.Deserialize<WalEvent>(sourceGenBytes, ReflectionOpts);

        Assert.NotNull(back);
        Assert.IsType(original.GetType(), back);
        Assert.Equal(original, back);
    }

    [Theory]
    [MemberData(nameof(Payloads))]
    public void SourceGen_Output_HasSameJsonShape_AsReflection(string label, WalEvent original)
    {
        _ = label;
        // Defence-in-depth alongside the byte-equality test: even when
        // both paths emit the same bytes, deep-compare the JsonNode tree
        // so a regression that introduces a cosmetic-only diff (extra
        // whitespace, different unicode escaping) surfaces with a
        // readable structural diff message rather than a raw byte length
        // mismatch.
        var sourceGenJson = JsonSerializer.Serialize(original, WalEventJsonContext.Default.WalEvent);
        var reflectionJson = JsonSerializer.Serialize<WalEvent>(original, ReflectionOpts);

        var a = JsonNode.Parse(sourceGenJson);
        var b = JsonNode.Parse(reflectionJson);
        Assert.True(JsonNode.DeepEquals(a, b),
            $"JSON shape mismatch.\n  source-gen: {sourceGenJson}\n  reflection: {reflectionJson}");
    }

    [Fact]
    public void Context_RegistersEveryWalEventSubtype()
    {
        // Polymorphic dispatch via System.Text.Json source-gen only works
        // for types that have a JsonSerializable entry on the context.
        // If a new WalEvent-derived record is added without being
        // registered on WalEventJsonContext, this test fails loudly —
        // before recovery starts losing events at runtime.
        var derived = typeof(WalEvent).Assembly
            .GetTypes()
            .Where(t => !t.IsAbstract && typeof(WalEvent).IsAssignableFrom(t))
            .ToArray();

        var ctx = WalEventJsonContext.Default;
        foreach (var t in derived)
        {
            var info = ctx.GetTypeInfo(t);
            Assert.True(info is not null,
                $"WalEventJsonContext is missing a [JsonSerializable(typeof({t.Name}))] entry.");
        }
    }
}
