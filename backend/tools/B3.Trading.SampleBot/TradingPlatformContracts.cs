using System.Net;

namespace B3.Trading.SampleBot;

internal sealed record SubAccountDto(string Id, string? DisplayName, bool Active);

internal sealed record SubmitOrderCommand(
    string Symbol,
    ulong SecurityId,
    string Side,
    long Quantity,
    decimal Price,
    string? SubAccountId);

internal sealed record SubmitOrderRequest(
    string Symbol,
    ulong SecurityId,
    string Side,
    string Type,
    long Quantity,
    decimal Price,
    string? SubAccountId = null);

internal sealed record OrderMutationResponse(
    string? MutationId,
    string? ClOrdId,
    string State,
    bool Replayed,
    string? Status,
    string? Reason,
    string? Code,
    string? Error,
    string? LookupUrl);

internal sealed record RestCallResult<T>(HttpStatusCode StatusCode, T? Payload, string? ErrorCode, string? ErrorMessage)
{
    public bool IsSuccessStatusCode => (int)StatusCode is >= 200 and <= 299;
}

internal sealed record TradingOrder(
    string ClOrdId,
    string Symbol,
    ulong SecurityId,
    string Side,
    string Type,
    long Quantity,
    long LeavesQuantity,
    long CumulativeQuantity,
    decimal? Price,
    string Status,
    string? ParentAlgoId = null,
    int? AlgoSliceSeq = null,
    bool IsStale = false,
    string? StaleReason = null,
    DateTimeOffset? StaledAtUtc = null,
    string TimeInForce = "Day",
    decimal? StopPrice = null,
    DateTimeOffset? GoodTillDate = null,
    long? DisplayQty = null,
    string? DisplayResetPolicy = null,
    string? SubAccountId = null,
    string? SecurityType = null,
    decimal? OptionStrikePrice = null,
    string? OptionExpirationDate = null,
    string? OptionPutOrCall = null,
    string? OptionUnderlyingSymbol = null,
    decimal? OptionContractMultiplier = null);

internal sealed record TradingExecution(
    string ClOrdId,
    string Symbol,
    string Side,
    string Status,
    string Kind,
    long LeavesQuantity,
    long CumulativeQuantity,
    long LastQuantity,
    decimal LastPrice,
    string? RejectReason,
    DateTimeOffset TimestampUtc,
    bool IsNativeStp = false,
    TradingBookTouch? BookTouch = null);

internal sealed record TradingBookTouch(
    decimal? BestBid,
    decimal? BestAsk,
    decimal? MidPrice,
    decimal? LastTradePrice,
    DateTimeOffset CapturedAtUtc,
    bool Stale);

internal sealed record TradingPosition(
    string Symbol,
    long NetQuantity,
    decimal AverageEntryPrice,
    string? SubAccountId = null,
    string? SecurityType = null,
    decimal? OptionStrikePrice = null,
    string? OptionExpirationDate = null,
    string? OptionPutOrCall = null,
    string? OptionUnderlyingSymbol = null,
    decimal? OptionContractMultiplier = null);
