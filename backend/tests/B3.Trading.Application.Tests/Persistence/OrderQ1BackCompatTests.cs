using System.Text.Json;
using B3.Trading.Application.Persistence;

namespace B3.Trading.Application.Tests.Persistence;

/// <summary>
/// Q1.1 (#253). Pins additive evolution of <see cref="OrderSubmittedEvent"/>
/// and <see cref="OrderSnapshot"/>. WAL segments and snapshot files written
/// before the Q1.1 slice (no <c>TimeInForce</c>, <c>StopPrice</c>, or
/// <c>GoodTillDate</c>) must hydrate cleanly with the implicit-Day /
/// no-stop / no-expiry semantics they actually carried.
/// </summary>
public class OrderQ1BackCompatTests
{
    private static readonly JsonSerializerOptions Opts = new(JsonSerializerDefaults.Web);

    [Fact]
    public void OrderSubmittedEvent_OldPayload_DeserialisesWithDefaults()
    {
        const string oldJson = """
        {
          "TimestampUtc": "2024-06-15T12:34:56.789+00:00",
          "ClOrdId": 42,
          "EndClientId": "alice",
          "FirmId": "default",
          "Symbol": "PETR4",
          "SecurityId": 4321,
          "Side": "Buy",
          "Type": "Limit",
          "Quantity": 100,
          "Price": 30.5
        }
        """;

        var ev = JsonSerializer.Deserialize<OrderSubmittedEvent>(oldJson, Opts);

        Assert.NotNull(ev);
        Assert.Equal("Day", ev!.TimeInForce);
        Assert.Null(ev.StopPrice);
        Assert.Null(ev.GoodTillDate);
        Assert.Equal(42UL, ev.ClOrdId);
        Assert.Equal(100, ev.Quantity);
    }

    [Fact]
    public void OrderSnapshot_OldPayload_DeserialisesWithDefaults()
    {
        const string oldJson = """
        {
          "ClOrdId": 7,
          "EndClientId": "alice",
          "Symbol": "PETR4",
          "SecurityId": 4321,
          "Side": "Buy",
          "Type": "Limit",
          "Quantity": 100,
          "Price": 30.5,
          "LeavesQuantity": 100,
          "CumulativeQuantity": 0,
          "Status": "New"
        }
        """;

        var snap = JsonSerializer.Deserialize<OrderSnapshot>(oldJson, Opts);

        Assert.NotNull(snap);
        Assert.Equal("Day", snap!.TimeInForce);
        Assert.Null(snap.StopPrice);
        Assert.Null(snap.GoodTillDate);
        Assert.False(snap.IsStale);
    }

    [Fact]
    public void OrderSubmittedEvent_NewPayload_RoundTrips()
    {
        var ts = new DateTimeOffset(2024, 6, 15, 12, 34, 56, 789, TimeSpan.Zero);
        var expiry = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);

        var ev = new OrderSubmittedEvent
        {
            TimestampUtc = ts,
            ClOrdId = 1UL,
            EndClientId = "alice",
            FirmId = "default",
            Symbol = "PETR4",
            SecurityId = 4321UL,
            Side = "Buy",
            Type = "StopLimit",
            Quantity = 100,
            Price = 30.5m,
            TimeInForce = "GTD",
            StopPrice = 29m,
            GoodTillDate = expiry,
        };

        var json = JsonSerializer.Serialize(ev, Opts);
        var rt = JsonSerializer.Deserialize<OrderSubmittedEvent>(json, Opts)!;

        Assert.Equal("GTD", rt.TimeInForce);
        Assert.Equal(29m, rt.StopPrice);
        Assert.Equal(expiry, rt.GoodTillDate);
    }
}
