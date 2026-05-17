using System.Text.Json;
using B3.Trading.Application.Persistence;

namespace B3.Trading.Application.Tests.Persistence;

/// <summary>
/// Q3.4 (#284). Pins additive evolution of
/// <see cref="OrderSubmittedEvent"/> and <see cref="OrderSnapshot"/>
/// for the native iceberg display-qty fields. WAL segments and
/// snapshot files written before this slice (no <c>DisplayQty</c>
/// or <c>DisplayResetPolicy</c>) must hydrate cleanly as
/// full-disclosure / no-reserve orders — the semantics they
/// actually carried.
/// </summary>
public class OrderDisplayQtyBackCompatTests
{
    private static readonly JsonSerializerOptions Opts = new(JsonSerializerDefaults.Web);

    [Fact]
    public void OrderSubmittedEvent_OldPayload_DisplayFieldsDefaultToNull()
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
          "Price": 30.5,
          "TimeInForce": "Day"
        }
        """;

        var ev = JsonSerializer.Deserialize<OrderSubmittedEvent>(oldJson, Opts);

        Assert.NotNull(ev);
        Assert.Null(ev!.DisplayQty);
        Assert.Null(ev.DisplayResetPolicy);
    }

    [Fact]
    public void OrderSubmittedEvent_NewPayload_RoundTripsDisplayFields()
    {
        var ev = new OrderSubmittedEvent
        {
            TimestampUtc = DateTimeOffset.UtcNow,
            ClOrdId = 1UL,
            EndClientId = "alice",
            FirmId = "default",
            Symbol = "PETR4",
            SecurityId = 4321UL,
            Side = "Buy",
            Type = "Limit",
            Quantity = 100,
            Price = 30.5m,
            TimeInForce = "Day",
            DisplayQty = 10,
            DisplayResetPolicy = "OnPartialFill",
        };

        var json = JsonSerializer.Serialize(ev, Opts);
        var rt = JsonSerializer.Deserialize<OrderSubmittedEvent>(json, Opts)!;

        Assert.Equal(10L, rt.DisplayQty);
        Assert.Equal("OnPartialFill", rt.DisplayResetPolicy);
    }

    [Fact]
    public void OrderSnapshot_OldPayload_DisplayFieldsDefaultToNull()
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
          "Status": "Working"
        }
        """;

        var snap = JsonSerializer.Deserialize<OrderSnapshot>(oldJson, Opts);

        Assert.NotNull(snap);
        Assert.Null(snap!.DisplayQty);
        Assert.Null(snap.DisplayResetPolicy);
    }

    [Fact]
    public void OrderSnapshot_NewPayload_RoundTripsDisplayFields()
    {
        var snap = new OrderSnapshot(
            7UL, "alice", "PETR4", 4321UL, "Buy", "Limit",
            100, 30.5m, 100, 0, "Working")
        {
            DisplayQty = 25,
            DisplayResetPolicy = "Never",
        };

        var json = JsonSerializer.Serialize(snap, Opts);
        var rt = JsonSerializer.Deserialize<OrderSnapshot>(json, Opts)!;

        Assert.Equal(25L, rt.DisplayQty);
        Assert.Equal("Never", rt.DisplayResetPolicy);
    }
}
