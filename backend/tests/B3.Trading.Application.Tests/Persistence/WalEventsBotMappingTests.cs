using System.Text.Json;
using B3.Trading.Application.Persistence;

namespace B3.Trading.Application.Tests.Persistence;

/// <summary>
/// Sub-issue #171 (E). JSON round-trip + backward-compatibility coverage
/// for the new <see cref="OrderSubmittedEvent.BotMapping"/> side-record
/// and the new <see cref="OrderCancelRequestedEvent"/> WAL event.
///
/// <para>Schema rule (WalEvents.cs): never rename, only add nullable
/// fields. These tests pin that rule for the bot-origin additions: an
/// older WAL segment without the new field deserialises to a record
/// with <c>BotMapping = null</c>, matching the manual-order semantics
/// it originally carried.</para>
/// </summary>
public class WalEventsBotMappingTests
{
    private static JsonSerializerOptions Opts => new(JsonSerializerDefaults.Web);

    [Fact]
    public void OrderSubmittedEvent_WithBotMapping_RoundTripsViaPolymorphicBase()
    {
        var credId = Guid.NewGuid();
        var original = new OrderSubmittedEvent
        {
            ClOrdId = 42UL,
            EndClientId = "bot:b3t_abc",
            FirmId = "default",
            Symbol = "PETR4",
            SecurityId = 4321UL,
            Side = "Buy",
            Type = "Limit",
            Quantity = 100,
            Price = 12.34m,
            BotMapping = new BotOrderMapping(credId, 9999UL),
        };

        var json = JsonSerializer.Serialize<WalEvent>(original, Opts);
        var back = (OrderSubmittedEvent)JsonSerializer.Deserialize<WalEvent>(json, Opts)!;

        Assert.Equal(42UL, back.ClOrdId);
        Assert.NotNull(back.BotMapping);
        Assert.Equal(credId, back.BotMapping!.CredentialId);
        Assert.Equal(9999UL, back.BotMapping.ExternalClOrdId);
    }

    [Fact]
    public void OrderSubmittedEvent_WithoutBotMapping_RoundTripsAsNull()
    {
        var original = new OrderSubmittedEvent
        {
            ClOrdId = 7UL,
            EndClientId = "alice",
            FirmId = "default",
            Symbol = "VALE3",
            SecurityId = 9876UL,
            Side = "Sell",
            Type = "Market",
            Quantity = 10,
        };

        var json = JsonSerializer.Serialize<WalEvent>(original, Opts);
        var back = (OrderSubmittedEvent)JsonSerializer.Deserialize<WalEvent>(json, Opts)!;

        Assert.Null(back.BotMapping);
    }

    [Fact]
    public void OrderSubmittedEvent_LegacyJsonWithoutBotMapping_DeserialisesAsNull()
    {
        // Pre-#171 WAL segments were written without the BotMapping field.
        // Replay must accept them silently with BotMapping == null.
        var legacy = """
            {
              "kind":"order.submitted",
              "ClOrdId":12345,
              "EndClientId":"alice",
              "FirmId":"default",
              "Symbol":"PETR4",
              "SecurityId":4321,
              "Side":"Buy",
              "Type":"Limit",
              "Quantity":100,
              "Price":10.0,
              "TimestampUtc":"2024-01-01T00:00:00Z"
            }
            """;

        var back = (OrderSubmittedEvent)JsonSerializer.Deserialize<WalEvent>(legacy, Opts)!;

        Assert.Equal(12345UL, back.ClOrdId);
        Assert.Null(back.BotMapping);
    }

    [Fact]
    public void OrderCancelRequestedEvent_WithBotMapping_RoundTripsViaPolymorphicBase()
    {
        var credId = Guid.NewGuid();
        var evt = new OrderCancelRequestedEvent
        {
            CancelClOrdId = 200UL,
            OriginalClOrdId = 42UL,
            OwnerEndClientId = "bot:b3t_abc",
            BotMapping = new BotOrderMapping(credId, 5555UL),
        };

        var json = JsonSerializer.Serialize<WalEvent>(evt, Opts);
        Assert.Contains("\"kind\":\"order.cancel-requested\"", json);

        var back = (OrderCancelRequestedEvent)JsonSerializer.Deserialize<WalEvent>(json, Opts)!;
        Assert.Equal(200UL, back.CancelClOrdId);
        Assert.Equal(42UL, back.OriginalClOrdId);
        Assert.Equal("bot:b3t_abc", back.OwnerEndClientId);
        Assert.NotNull(back.BotMapping);
        Assert.Equal(credId, back.BotMapping!.CredentialId);
        Assert.Equal(5555UL, back.BotMapping.ExternalClOrdId);
    }

    [Fact]
    public void OrderCancelRequestedEvent_WithoutBotMapping_RoundTrips()
    {
        var evt = new OrderCancelRequestedEvent
        {
            CancelClOrdId = 200UL,
            OriginalClOrdId = 42UL,
            OwnerEndClientId = "alice",
        };

        var json = JsonSerializer.Serialize<WalEvent>(evt, Opts);
        var back = (OrderCancelRequestedEvent)JsonSerializer.Deserialize<WalEvent>(json, Opts)!;

        Assert.Null(back.BotMapping);
        Assert.Equal(200UL, back.CancelClOrdId);
    }
}
