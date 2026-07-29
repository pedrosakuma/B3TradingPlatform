using System.Text.Json;

namespace B3.Trading.SampleBot;

internal static class PrivateFeedProtocol
{
    public static readonly string[] PrivateChannels = ["orders.me", "executions.me", "positions.me"];

    public static string BuildSubscribeCommand(IEnumerable<string> channels) => JsonSerializer.Serialize(
        new { type = "subscribe", channels = channels.ToArray() },
        SampleBotJson.Options);

    public static PrivateFeedFrame Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var type = root.GetProperty("type").GetString();
        if (string.Equals(type, "error", StringComparison.Ordinal))
        {
            return new ProtocolErrorFrame(
                root.TryGetProperty("code", out var codeElement) ? codeElement.GetString() ?? "unknown_error" : "unknown_error",
                root.TryGetProperty("message", out var messageElement) ? messageElement.GetString() ?? string.Empty : string.Empty);
        }

        var channel = root.GetProperty("channel").GetString();
        var seq = root.TryGetProperty("seq", out var seqElement) && seqElement.TryGetInt64(out var parsedSeq)
            ? parsedSeq
            : 0;
        var data = root.GetProperty("data");

        return (type, channel) switch
        {
            ("snapshot", "orders.me") => new OrdersSnapshotFrame(seq, data.Deserialize<TradingOrder[]>(SampleBotJson.Options) ?? Array.Empty<TradingOrder>()),
            ("delta", "orders.me") => new OrderDeltaFrame(seq, data.Deserialize<TradingOrder>(SampleBotJson.Options)
                ?? throw new InvalidOperationException("orders.me delta payload was empty.")),
            ("snapshot", "executions.me") => new ExecutionsSnapshotFrame(seq, data.Deserialize<TradingExecution[]>(SampleBotJson.Options) ?? Array.Empty<TradingExecution>()),
            ("delta", "executions.me") => new ExecutionDeltaFrame(seq, data.Deserialize<TradingExecution>(SampleBotJson.Options)
                ?? throw new InvalidOperationException("executions.me delta payload was empty.")),
            ("snapshot", "positions.me") => new PositionsSnapshotFrame(seq, data.Deserialize<TradingPosition[]>(SampleBotJson.Options) ?? Array.Empty<TradingPosition>()),
            ("delta", "positions.me") => new PositionDeltaFrame(seq, data.Deserialize<TradingPosition>(SampleBotJson.Options)
                ?? throw new InvalidOperationException("positions.me delta payload was empty.")),
            var (_, phaseChannel) when phaseChannel is not null && phaseChannel.StartsWith("phases.", StringComparison.Ordinal) =>
                ParsePhaseFrame(type, phaseChannel, seq, data),
            _ => throw new InvalidOperationException($"Unsupported websocket frame type='{type}' channel='{channel}'."),
        };
    }

    private static PrivateFeedFrame ParsePhaseFrame(string? type, string channel, long seq, JsonElement data)
    {
        var payload = data.Deserialize<PhaseSnapshot>(SampleBotJson.Options)
            ?? throw new InvalidOperationException($"{channel} payload was empty.");
        var symbol = channel["phases.".Length..];

        return type switch
        {
            "snapshot" => new PhaseSnapshotFrame(seq, symbol, payload),
            "delta" => new PhaseDeltaFrame(seq, symbol, payload),
            _ => throw new InvalidOperationException($"Unsupported websocket frame type='{type}' channel='{channel}'."),
        };
    }
}

internal abstract record PrivateFeedFrame;
internal sealed record OrdersSnapshotFrame(long Seq, IReadOnlyList<TradingOrder> Orders) : PrivateFeedFrame;
internal sealed record OrderDeltaFrame(long Seq, TradingOrder Order) : PrivateFeedFrame;
internal sealed record ExecutionsSnapshotFrame(long Seq, IReadOnlyList<TradingExecution> Executions) : PrivateFeedFrame;
internal sealed record ExecutionDeltaFrame(long Seq, TradingExecution Execution) : PrivateFeedFrame;
internal sealed record PositionsSnapshotFrame(long Seq, IReadOnlyList<TradingPosition> Positions) : PrivateFeedFrame;
internal sealed record PositionDeltaFrame(long Seq, TradingPosition Position) : PrivateFeedFrame;
internal sealed record PhaseSnapshotFrame(long Seq, string Symbol, PhaseSnapshot Phase) : PrivateFeedFrame;
internal sealed record PhaseDeltaFrame(long Seq, string Symbol, PhaseSnapshot Phase) : PrivateFeedFrame;
internal sealed record ProtocolErrorFrame(string Code, string Message) : PrivateFeedFrame;

internal sealed record PhaseSnapshot(string Phase, DateTimeOffset? At);
