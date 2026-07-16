using System.Diagnostics.Metrics;
using B3.Trading.Application;
using B3.Trading.Application.Lifecycle;
using B3.Trading.Application.Observability;
using B3.Trading.Application.Persistence;
using B3.Trading.Application.Risk;
using B3.Trading.Application.Risk.Accounting;
using B3.Trading.Domain;
using B3.Trading.Infrastructure.Persistence;
using Microsoft.Extensions.Logging.Abstractions;

namespace B3.Trading.Application.Tests.Observability;

/// <summary>
/// OPT-F (#488). The order-flow counters must distinguish equity
/// from option submissions so dashboards can split the two flows
/// and a dedicated cabinet/worthless-OTM surveillance counter must
/// fire on the OPT-C (#485) zero-price-option path.
///
/// <para>
/// Process-global Meter caveat (see CodebaseFact "testing" /
/// MetricsFirmTagTests): the listener captures increments from
/// every parallel TestAppFactory; assertions filter by symbol +
/// value, never on "last seen".
/// </para>
/// </summary>
public class OptionMetricsTagsTests
{
    private static readonly EndClientId Alice = new("alice");

    [Fact]
    public async Task OrdersSubmitted_CarriesSecurityTypeTag_ForOptionSymbol()
    {
        var tags = new System.Collections.Concurrent.ConcurrentBag<(string symbol, string securityType, long value)>();
        using var listener = StartListener("trading.orders.submitted", (value, kv) =>
        {
            string? symbol = null, secType = null;
            foreach (var t in kv)
            {
                if (t.Key == "symbol" && t.Value is string s) symbol = s;
                if (t.Key == "security_type" && t.Value is string st) secType = st;
            }
            if (symbol is not null && secType is not null)
                tags.Add((symbol, secType, value));
        });

        var h = new Harness(WithPetrl200());
        var req = new OrderSubmissionRequest(
            Alice, "FIRM-A", "PETRL200", 4321UL, OrderSide.Buy, OrderType.Limit,
            Quantity: 10, Price: 0.50m);

        var result = await h.Submitter.SubmitAsync(req, CancellationToken.None);
        Assert.Equal(OrderSubmissionResultKind.Accepted, result.Kind);

        Poll(() => tags.Any(t => t.symbol == "PETRL200" && t.securityType == "option" && t.value >= 1),
            $"expected at least one PETRL200/option increment; saw: [{string.Join(",", tags)}]");
    }

    [Fact]
    public async Task OrdersSubmitted_CarriesSecurityTypeTag_ForEquitySymbol()
    {
        var tags = new System.Collections.Concurrent.ConcurrentBag<(string symbol, string securityType)>();
        using var listener = StartListener("trading.orders.submitted", (_, kv) =>
        {
            string? symbol = null, secType = null;
            foreach (var t in kv)
            {
                if (t.Key == "symbol" && t.Value is string s) symbol = s;
                if (t.Key == "security_type" && t.Value is string st) secType = st;
            }
            if (symbol is not null && secType is not null)
                tags.Add((symbol, secType));
        });

        var h = new Harness(WithPetr4Equity());
        var req = new OrderSubmissionRequest(
            Alice, "FIRM-A", "PETR4", 4321UL, OrderSide.Buy, OrderType.Limit,
            Quantity: 100, Price: 30m);

        var result = await h.Submitter.SubmitAsync(req, CancellationToken.None);
        Assert.Equal(OrderSubmissionResultKind.Accepted, result.Kind);

        Poll(() => tags.Any(t => t.symbol == "PETR4" && t.securityType == "equity"),
            $"expected PETR4/equity tag; saw: [{string.Join(",", tags)}]");
    }

    [Fact]
    public async Task OrdersSubmitted_TagIsUnknown_WhenSymbolDirectoryNotInjected()
    {
        var tags = new System.Collections.Concurrent.ConcurrentBag<(string symbol, string securityType)>();
        using var listener = StartListener("trading.orders.submitted", (_, kv) =>
        {
            string? symbol = null, secType = null;
            foreach (var t in kv)
            {
                if (t.Key == "symbol" && t.Value is string s) symbol = s;
                if (t.Key == "security_type" && t.Value is string st) secType = st;
            }
            if (symbol is not null && secType is not null)
                tags.Add((symbol, secType));
        });

        var h = new Harness(symbolDirectory: null);
        var req = new OrderSubmissionRequest(
            Alice, "FIRM-A", "UNKNOWN1", 4321UL, OrderSide.Buy, OrderType.Limit,
            Quantity: 100, Price: 30m);

        var result = await h.Submitter.SubmitAsync(req, CancellationToken.None);
        Assert.Equal(OrderSubmissionResultKind.Accepted, result.Kind);

        Poll(() => tags.Any(t => t.symbol == "UNKNOWN1" && t.securityType == "unknown"),
            $"expected UNKNOWN1/unknown tag; saw: [{string.Join(",", tags)}]");
    }

    [Fact]
    public async Task OptionZeroPriceCounter_Fires_OnCabinetTrade()
    {
        var hits = new System.Collections.Concurrent.ConcurrentBag<(string symbol, string putCall, long value)>();
        using var listener = StartListener("trading.options.zero_price_orders_submitted", (value, kv) =>
        {
            string? symbol = null, pc = null;
            foreach (var t in kv)
            {
                if (t.Key == "symbol" && t.Value is string s) symbol = s;
                if (t.Key == "put_call" && t.Value is string p) pc = p;
            }
            if (symbol is not null && pc is not null)
                hits.Add((symbol, pc, value));
        });

        var h = new Harness(WithPetrl200());
        var req = new OrderSubmissionRequest(
            Alice, "FIRM-A", "PETRL200", 4321UL, OrderSide.Sell, OrderType.Limit,
            Quantity: 5, Price: 0m);

        var result = await h.Submitter.SubmitAsync(req, CancellationToken.None);
        Assert.Equal(OrderSubmissionResultKind.Accepted, result.Kind);

        Poll(() => hits.Any(t => t.symbol == "PETRL200" && t.putCall == "call" && t.value >= 1),
            $"expected one PETRL200/call cabinet increment; saw: [{string.Join(",", hits)}]");
    }

    [Fact]
    public async Task OptionZeroPriceCounter_DoesNotFire_OnRealOptionPrice()
    {
        var hits = new System.Collections.Concurrent.ConcurrentBag<string>();
        using var listener = StartListener("trading.options.zero_price_orders_submitted", (_, kv) =>
        {
            foreach (var t in kv)
                if (t.Key == "symbol" && t.Value is string s)
                    hits.Add(s);
        });

        var h = new Harness(WithPetrl200_NonZeroAlias());
        var req = new OrderSubmissionRequest(
            Alice, "FIRM-A", "PETRL200_NONZERO", 4321UL, OrderSide.Buy, OrderType.Limit,
            Quantity: 1, Price: 0.05m);

        var result = await h.Submitter.SubmitAsync(req, CancellationToken.None);
        Assert.Equal(OrderSubmissionResultKind.Accepted, result.Kind);

        await Task.Delay(150);
        Assert.DoesNotContain("PETRL200_NONZERO", hits);
    }

    [Fact]
    public async Task OptionZeroPriceCounter_DoesNotFire_OnEquityZero()
    {
        // OPT-C scope guardrail: counter must be strictly options-only.
        // Equity at price=0 isn't venue-legal anyway, but if some path
        // ever sends one, the cabinet surveillance counter must not
        // mis-attribute it.
        var hits = new System.Collections.Concurrent.ConcurrentBag<string>();
        using var listener = StartListener("trading.options.zero_price_orders_submitted", (_, kv) =>
        {
            foreach (var t in kv)
                if (t.Key == "symbol" && t.Value is string s)
                    hits.Add(s);
        });

        var h = new Harness(WithPetr4Equity());
        var req = new OrderSubmissionRequest(
            Alice, "FIRM-A", "PETR4_EQUITY_ZERO", 4321UL, OrderSide.Buy, OrderType.Limit,
            Quantity: 100, Price: 0m);
        await h.Submitter.SubmitAsync(req, CancellationToken.None);

        await Task.Delay(150);
        Assert.DoesNotContain("PETR4_EQUITY_ZERO", hits);
    }

    private static MeterListener StartListener(string instrumentName, Action<long, ReadOnlySpan<KeyValuePair<string, object?>>> onMeasurement)
    {
        var listener = new MeterListener();
        listener.InstrumentPublished = (instr, l) =>
        {
            if (instr.Meter.Name == "B3.Trading" && instr.Name == instrumentName)
                l.EnableMeasurementEvents(instr);
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) => onMeasurement(value, tags));
        listener.Start();
        return listener;
    }

    private static void Poll(Func<bool> predicate, string message)
    {
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (!predicate() && DateTime.UtcNow < deadline)
            Thread.Sleep(20);
        Assert.True(predicate(), message);
    }

    private static SymbolDirectory WithPetrl200()
    {
        var opts = new SymbolDirectoryOptions();
        opts.Specs["PETRL200"] = new InstrumentSpecOptions
        {
            Option = new OptionMetadataOptions
            {
                ExpirationDate = new DateOnly(2026, 12, 18),
                PutOrCall = "Call",
                ExerciseStyle = "American",
                ContractMultiplier = 100m,
            },
        };
        return new SymbolDirectory(opts);
    }

    private static SymbolDirectory WithPetrl200_NonZeroAlias()
    {
        var opts = new SymbolDirectoryOptions();
        opts.Specs["PETRL200_NONZERO"] = new InstrumentSpecOptions
        {
            Option = new OptionMetadataOptions
            {
                ExpirationDate = new DateOnly(2026, 12, 18),
                PutOrCall = "Call",
                ExerciseStyle = "American",
                ContractMultiplier = 100m,
            },
        };
        return new SymbolDirectory(opts);
    }

    private static SymbolDirectory WithPetr4Equity()
    {
        var opts = new SymbolDirectoryOptions();
        opts.Specs["PETR4"] = new InstrumentSpecOptions { TickSize = 0.01m, LotSize = 100 };
        opts.Specs["PETR4_EQUITY_ZERO"] = new InstrumentSpecOptions { TickSize = 0.01m, LotSize = 100 };
        return new SymbolDirectory(opts);
    }

    private sealed class Harness
    {
        public WorkingOrderBook Book { get; } = new();
        public OrderOwnershipMap Ownership { get; } = new();
        public ClOrdIdPrefixRegistry ClOrdIds { get; } = new();
        public NullEventStore Store { get; } = new();
        public EventDispatcher Dispatcher { get; }
        public RecordingGateway Gateway { get; } = new();
        public NoOpExecutionEventSink Sink { get; } = new();
        public RiskPipeline Risk { get; } = new(Array.Empty<IRiskCheck>());
        public NoOpMarginProvider Margin { get; } = new();
        public CompositeRiskAccountant Accountant { get; } = new(Array.Empty<IRiskAccountant>());
        public NeverDrainingGate Drain { get; } = new();
        public OrderSubmissionService Submitter { get; }

        public Harness(SymbolDirectory? symbolDirectory = null)
        {
            Dispatcher = new EventDispatcher(Store);
            Submitter = new OrderSubmissionService(
                ClOrdIds, Ownership, Book, Gateway, Sink, Risk, Margin, Accountant,
                Dispatcher, Drain, NullLogger<OrderSubmissionService>.Instance,
                symbolDirectory: symbolDirectory);
        }
    }

    private sealed class RecordingGateway : IExchangeGateway
    {
        public Task SubmitAsync(Order order, CancellationToken ct) => Task.CompletedTask;
        public Task CancelAsync(Order order, ulong newClOrdId, CancellationToken ct) => Task.CompletedTask;
        public Task CancelReplaceAsync(
            Order original, ulong newClOrdId, long newQuantity, decimal? newPrice,
            TimeInForce? requestedTimeInForce, decimal? requestedStopPrice,
            DateTimeOffset? requestedGoodTillDate, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class NeverDrainingGate : IDrainController
    {
        public bool IsDraining => false;
        public void BeginDrain(string reason) { }
        public Task CompleteDrainAsync(CancellationToken ct) => Task.CompletedTask;
    }
}
