using System.Net;
using B3.Trading.SampleBot;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace B3.Trading.SampleBot.Tests;

public sealed class SampleBotWorkflowTests
{
    [Fact]
    public async Task RunAsync_FillPath_SubmitsSinglePassiveOrder()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-07-29T00:00:00Z"));
        var restClient = new FakeTradingPlatformRestClient();
        var workflow = CreateWorkflow(restClient, clock);
        var runTask = workflow.RunAsync(CancellationToken.None);

        await PrimeReadyStateAsync(workflow, clock);
        var submitted = await restClient.SubmitObserved.Task;
        await workflow.WorkingOrderReady.WaitAsync(TimeSpan.FromSeconds(5));
        await ((IPrivateFeedObserver)workflow).OnFrameAsync(
            new OrderDeltaFrame(1, new TradingOrder("101", "PETR4", 4321, "Buy", "Limit", 100, 0, 100, 29.99m, "Filled")),
            CancellationToken.None);

        var result = await runTask;

        Assert.Equal("filled", result.Outcome);
        Assert.Equal("PETR4", submitted.Symbol);
        Assert.Equal(4321UL, submitted.SecurityId);
        Assert.Equal(29.99m, submitted.Price);
        Assert.Single(restClient.SubmitCalls);
        Assert.Empty(restClient.CancelCalls);
    }

    [Fact]
    public async Task RunAsync_TimeoutPath_CancelsWorkingOrder()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-07-29T00:00:00Z"));
        var restClient = new FakeTradingPlatformRestClient
        {
            CancelHandler = (clOrdId, _, _) => Task.FromResult(new RestCallResult<OrderMutationResponse>(
                HttpStatusCode.Accepted,
                new OrderMutationResponse("cancel-1", clOrdId, "RecordedPendingApproval", false, null, null, null, null, null),
                null,
                null)),
        };
        var workflow = CreateWorkflow(restClient, clock);
        var runTask = workflow.RunAsync(CancellationToken.None);

        await PrimeReadyStateAsync(workflow, clock);
        await restClient.SubmitObserved.Task;
        await workflow.WorkingOrderReady.WaitAsync(TimeSpan.FromSeconds(5));
        await workflow.TriggerOrderTimeoutAsync(CancellationToken.None);
        await restClient.CancelObserved.Task;
        await ((IPrivateFeedObserver)workflow).OnFrameAsync(
            new OrderDeltaFrame(2, new TradingOrder("101", "PETR4", 4321, "Buy", "Limit", 100, 0, 0, 29.99m, "Cancelled")),
            CancellationToken.None);

        var result = await runTask;

        Assert.Equal("cancelled", result.Outcome);
        Assert.Single(restClient.SubmitCalls);
        Assert.Single(restClient.CancelCalls);
    }

    [Fact]
    public async Task RunAsync_TimeoutCancelTransportFailure_CompletesExplicitly()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-07-29T00:00:00Z"));
        var restClient = new FakeTradingPlatformRestClient
        {
            CancelHandler = (_, _, _) => throw new HttpRequestException("network down"),
        };
        var workflow = CreateWorkflow(restClient, clock);
        var runTask = workflow.RunAsync(CancellationToken.None);

        await PrimeReadyStateAsync(workflow, clock);
        await restClient.SubmitObserved.Task;
        await workflow.WorkingOrderReady.WaitAsync(TimeSpan.FromSeconds(5));

        await workflow.TriggerOrderTimeoutAsync(CancellationToken.None);
        var result = await runTask;

        Assert.Equal("cancel_error", result.Outcome);
        Assert.Contains("network down", result.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunAsync_RejectPath_StopsImmediately()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-07-29T00:00:00Z"));
        var restClient = new FakeTradingPlatformRestClient
        {
            SubmitHandler = (_, _, _) => Task.FromResult(new RestCallResult<OrderMutationResponse>(
                HttpStatusCode.Accepted,
                new OrderMutationResponse("submit-1", "101", "RecordedPendingApproval", false, "Rejected", "risk_limit", null, null, null),
                null,
                null)),
        };
        var workflow = CreateWorkflow(restClient, clock);
        var runTask = workflow.RunAsync(CancellationToken.None);

        await PrimeReadyStateAsync(workflow, clock);
        var result = await runTask;

        Assert.Equal("rejected", result.Outcome);
        Assert.Single(restClient.SubmitCalls);
        Assert.Empty(restClient.CancelCalls);
    }

    [Fact]
    public async Task RunAsync_StaleFeed_DoesNotSubmit()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-07-29T00:00:00Z"));
        var restClient = new FakeTradingPlatformRestClient();
        var workflow = CreateWorkflow(restClient, clock);
        using var cts = new CancellationTokenSource();
        var runTask = workflow.RunAsync(cts.Token);

        await ((IPrivateFeedObserver)workflow).OnConnectedAsync(false, CancellationToken.None);
        await ((IPrivateFeedObserver)workflow).OnFrameAsync(new OrdersSnapshotFrame(0, Array.Empty<TradingOrder>()), CancellationToken.None);
        await ((IPrivateFeedObserver)workflow).OnFrameAsync(new ExecutionsSnapshotFrame(0, Array.Empty<TradingExecution>()), CancellationToken.None);
        await ((IPrivateFeedObserver)workflow).OnFrameAsync(new PositionsSnapshotFrame(0, Array.Empty<TradingPosition>()), CancellationToken.None);
        await ((IPrivateFeedObserver)workflow).OnFrameAsync(
            new PhaseSnapshotFrame(0, "PETR4", new PhaseSnapshot("Open", clock.GetUtcNow())),
            CancellationToken.None);
        await ((ISampleBotMarketDataObserver)workflow).OnConnectedAsync(false, CancellationToken.None);
        await ((ISampleBotMarketDataObserver)workflow).OnQuoteAsync(
            new MarketDataQuote("PETR4", 4321, ReferencePriceSource.TradingReferencePrice, 30m, clock.GetUtcNow()),
            CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(6));

        await Task.Yield();
        Assert.Empty(restClient.SubmitCalls);

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runTask);
    }

    [Fact]
    public async Task RunAsync_MarketDataDisconnect_AttemptsCancelAndStops()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-07-29T00:00:00Z"));
        var restClient = new FakeTradingPlatformRestClient
        {
            CancelHandler = (clOrdId, _, _) => Task.FromResult(new RestCallResult<OrderMutationResponse>(
                HttpStatusCode.Accepted,
                new OrderMutationResponse("cancel-1", clOrdId, "RecordedPendingApproval", false, null, null, null, null, null),
                null,
                null)),
        };
        var workflow = CreateWorkflow(restClient, clock);
        var runTask = workflow.RunAsync(CancellationToken.None);

        await PrimeReadyStateAsync(workflow, clock);
        await restClient.SubmitObserved.Task;
        await workflow.WorkingOrderReady.WaitAsync(TimeSpan.FromSeconds(5));
        await ((ISampleBotMarketDataObserver)workflow).OnDisconnectedAsync(null, CancellationToken.None);

        var result = await runTask;

        Assert.Equal("feed_lost", result.Outcome);
        Assert.Single(restClient.CancelCalls);
    }

    [Fact]
    public async Task RunAsync_TradingDisconnect_AttemptsCancelAndStops()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-07-29T00:00:00Z"));
        var restClient = new FakeTradingPlatformRestClient
        {
            CancelHandler = (clOrdId, _, _) => Task.FromResult(new RestCallResult<OrderMutationResponse>(
                HttpStatusCode.Accepted,
                new OrderMutationResponse("cancel-1", clOrdId, "RecordedPendingApproval", false, null, null, null, null, null),
                null,
                null)),
        };
        var workflow = CreateWorkflow(restClient, clock);
        var runTask = workflow.RunAsync(CancellationToken.None);

        await PrimeReadyStateAsync(workflow, clock);
        await restClient.SubmitObserved.Task;
        await workflow.WorkingOrderReady.WaitAsync(TimeSpan.FromSeconds(5));
        await ((IPrivateFeedObserver)workflow).OnDisconnectedAsync(null, CancellationToken.None);

        var result = await runTask;

        Assert.Equal("feed_lost", result.Outcome);
        Assert.Single(restClient.CancelCalls);
    }

    private static SampleBotWorkflow CreateWorkflow(
        FakeTradingPlatformRestClient restClient,
        TimeProvider clock,
        ControlledDelay? delay = null) =>
        new(
            restClient,
            Options.Create(new SampleBotOptions
            {
                BaseUrl = "https://trading.local",
                Auth = new SampleBotAuthOptions
                {
                    Mode = SampleBotAuthMode.InternalToken,
                    InternalTradingToken = "jwt",
                },
                MarketData = new SampleBotMarketDataOptions
                {
                    WsUrl = "wss://marketdata.local/ws",
                    MaxAge = TimeSpan.FromSeconds(5),
                },
                DemoOrder = new DemoOrderOptions
                {
                    Enabled = true,
                    Symbol = "PETR4",
                    Side = "Buy",
                    Quantity = 100,
                    TickSize = 0.01m,
                    PriceOffsetTicks = 1,
                    MaxNotional = 5000m,
                    OrderTimeout = TimeSpan.FromSeconds(10),
                    CancellationAttemptTimeout = TimeSpan.FromSeconds(1),
                    RequireOpenPhase = true,
                    PostWorkflowWait = TimeSpan.Zero,
                    IdempotencyKeyPrefix = "samplebot-test",
                },
            }),
            clock,
            NullLogger<SampleBotWorkflow>.Instance,
            delay is null ? null : delay.DelayAsync);

    private static async Task PrimeReadyStateAsync(SampleBotWorkflow workflow, TimeProvider clock)
    {
        await ((IPrivateFeedObserver)workflow).OnConnectedAsync(false, CancellationToken.None);
        await ((IPrivateFeedObserver)workflow).OnFrameAsync(new OrdersSnapshotFrame(0, Array.Empty<TradingOrder>()), CancellationToken.None);
        await ((IPrivateFeedObserver)workflow).OnFrameAsync(new ExecutionsSnapshotFrame(0, Array.Empty<TradingExecution>()), CancellationToken.None);
        await ((IPrivateFeedObserver)workflow).OnFrameAsync(new PositionsSnapshotFrame(0, Array.Empty<TradingPosition>()), CancellationToken.None);
        await ((IPrivateFeedObserver)workflow).OnFrameAsync(
            new PhaseSnapshotFrame(0, "PETR4", new PhaseSnapshot("Open", clock.GetUtcNow())),
            CancellationToken.None);
        await ((ISampleBotMarketDataObserver)workflow).OnConnectedAsync(false, CancellationToken.None);
        await ((ISampleBotMarketDataObserver)workflow).OnQuoteAsync(
            new MarketDataQuote("PETR4", 4321, ReferencePriceSource.TradingReferencePrice, 30m, clock.GetUtcNow()),
            CancellationToken.None);
    }

}
