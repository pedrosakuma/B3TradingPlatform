using B3.Trading.Application;
using B3.Trading.Application.Persistence;
using B3.Trading.Application.UserBots;
using B3.Trading.Domain;
using B3.Trading.Infrastructure.Persistence;
using Microsoft.Extensions.Logging.Abstractions;

namespace B3.Trading.Application.Tests;

/// <summary>
/// Sub-issue #171 (E). Behavioural coverage for the shared cancel
/// pipeline introduced by this slice. The two invariants that matter
/// most here:
///
/// <list type="number">
///   <item>The dispatcher <c>apply</c> callback runs synchronous
///   in-memory mutations only — the async gateway call must happen
///   AFTER <c>Dispatch</c> returns. A fake gateway that records call
///   ordering pins this contract.</item>
///   <item>When a <see cref="BotOrigin"/> is supplied, the bot
///   cancel-mapping registry is populated atomically with the WAL
///   record; otherwise it is left untouched (REST/WS unaffected).</item>
/// </list>
/// </summary>
public class OrderCancelServiceTests
{
    private static readonly Guid CredA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly EndClientId Owner = new("alice");

    private sealed class RecordingGateway : IExchangeGateway
    {
        public List<string> Calls { get; } = new();
        public TaskCompletionSource? CancelGate { get; set; }

        public Task SubmitAsync(Order order, CancellationToken cancellationToken)
        {
            Calls.Add("submit");
            return Task.CompletedTask;
        }

        public async Task CancelAsync(Order order, ulong newClOrdId, CancellationToken cancellationToken)
        {
            Calls.Add($"cancel:{newClOrdId}");
            if (CancelGate is { } g) await g.Task.ConfigureAwait(false);
        }

        public Task CancelReplaceAsync(
            Order original, ulong newClOrdId, long newQuantity, decimal? newPrice,
            TimeInForce? requestedTimeInForce, decimal? requestedStopPrice, DateTimeOffset? requestedGoodTillDate,
            CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    private static Order Working(ulong clOrdId)
        => new(clOrdId, Owner, "PETR4", 4321UL, OrderSide.Buy, OrderType.Limit, 100, 10m);

    [Fact]
    public async Task CancelAsync_HappyPath_AppliesInMemoryMutations_AndCallsGatewayOutsideDispatch()
    {
        // The apply callback must mutate ownership/clOrdId/registry
        // SYNCHRONOUSLY, and the gateway must be hit AFTER dispatch returns.
        // A blocking gate on the gateway proves the dispatcher lock is
        // released before the network I/O — otherwise nothing else could
        // proceed during a flaky cancel.
        var clOrdIds = new ClOrdIdPrefixRegistry();
        var ownership = new OrderOwnershipMap();
        var book = new WorkingOrderBook();
        var dispatcher = new EventDispatcher(new NullEventStore());
        var gateway = new RecordingGateway();
        var bots = new InMemoryUserBotOrderMappingRegistry();

        // Seed an existing order owned by Alice (mimic prior submit).
        const ulong original = 100UL;
        Assert.True(book.TryAdd(Working(original)));
        ownership.Register(original, Owner);
        bots.RegisterOrderInternal(original, CredA, externalClOrdId: 9UL);

        var sut = new OrderCancelService(clOrdIds, ownership, book, gateway, dispatcher,
            NullLogger<OrderCancelService>.Instance, bots);

        // Hold the gateway call so the test can observe ordering between
        // dispatcher-synchronous mutations and the OUTSIDE-the-lock I/O.
        gateway.CancelGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = sut.CancelAsync(Owner, original, CancellationToken.None,
            new BotOrigin(CredA, ExternalClOrdId: 77UL));

        for (int i = 0; i < 100 && gateway.Calls.Count == 0; i++)
            await Task.Yield();

        // Gateway has been called, blocked on the gate — meaning Dispatch
        // already returned with its in-memory mutations applied.
        Assert.Single(gateway.Calls);
        Assert.StartsWith("cancel:", gateway.Calls[0]);
        var cancelClOrdId = ulong.Parse(gateway.Calls[0]["cancel:".Length..]);

        // ownership cancel-link, watermark advance, and bot mapping all
        // visible without waiting for the gateway call to complete.
        Assert.True(ownership.TryResolveOrig(cancelClOrdId, out var origLinked));
        Assert.Equal(original, origLinked);
        Assert.True(bots.TryGetCancelMapping(cancelClOrdId, out var cm));
        Assert.Equal(original, cm.OriginalInternalClOrdId);
        Assert.Equal(77UL, cm.ExternalCancelClOrdId);

        gateway.CancelGate.SetResult();
        var result = await pending;
        Assert.Equal(OrderCancelResultKind.Accepted, result.Kind);
        Assert.Equal(cancelClOrdId, result.CancelClOrdId);
    }

    [Fact]
    public async Task CancelAsync_WithoutBotOrigin_DoesNotTouchBotMappingRegistry()
    {
        // REST DELETE path: the registry must not pick up REST-origin
        // cancels. The OrderCancelService still works without a registry,
        // and (when one is provided) leaves it untouched for non-bot calls.
        var clOrdIds = new ClOrdIdPrefixRegistry();
        var ownership = new OrderOwnershipMap();
        var book = new WorkingOrderBook();
        var dispatcher = new EventDispatcher(new NullEventStore());
        var gateway = new RecordingGateway();
        var bots = new InMemoryUserBotOrderMappingRegistry();

        const ulong original = 100UL;
        Assert.True(book.TryAdd(Working(original)));
        ownership.Register(original, Owner);

        var sut = new OrderCancelService(clOrdIds, ownership, book, gateway, dispatcher,
            NullLogger<OrderCancelService>.Instance, bots);

        var result = await sut.CancelAsync(Owner, original, CancellationToken.None,
            botOrigin: null);

        Assert.Equal(OrderCancelResultKind.Accepted, result.Kind);
        Assert.Empty(bots.SnapshotCancels());
    }

    [Fact]
    public async Task CancelAsync_NotFoundOrCrossOwner_ReturnsNotFound_NoWalNoGateway()
    {
        var clOrdIds = new ClOrdIdPrefixRegistry();
        var ownership = new OrderOwnershipMap();
        var book = new WorkingOrderBook();
        var dispatcher = new EventDispatcher(new NullEventStore());
        var gateway = new RecordingGateway();
        var bots = new InMemoryUserBotOrderMappingRegistry();

        var sut = new OrderCancelService(clOrdIds, ownership, book, gateway, dispatcher,
            NullLogger<OrderCancelService>.Instance, bots);

        // Unknown id.
        Assert.Equal(OrderCancelResultKind.NotFound,
            (await sut.CancelAsync(Owner, 100UL, CancellationToken.None)).Kind);

        // Cross-owner: order exists but is owned by someone else — must
        // surface as NotFound (info disclosure boundary).
        Assert.True(book.TryAdd(Working(101UL)));
        ownership.Register(101UL, Owner);
        Assert.Equal(OrderCancelResultKind.NotFound,
            (await sut.CancelAsync(new EndClientId("mallory"), 101UL, CancellationToken.None)).Kind);

        Assert.Empty(gateway.Calls);
    }
}
