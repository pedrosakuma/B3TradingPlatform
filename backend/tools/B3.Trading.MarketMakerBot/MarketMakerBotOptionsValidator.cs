using Microsoft.Extensions.Options;

namespace B3.Trading.MarketMakerBot;

public sealed class MarketMakerBotOptionsValidator : IValidateOptions<MarketMakerBotOptions>
{
    public ValidateOptionsResult Validate(string? name, MarketMakerBotOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var failures = new List<string>();
        var symbols = new HashSet<string>(StringComparer.Ordinal);

        if (options.CancelAckTimeout <= TimeSpan.Zero)
            failures.Add("MarketMaker:CancelAckTimeout must be positive.");
        if (options.StartupCleanupTimeout <= TimeSpan.Zero)
            failures.Add("MarketMaker:StartupCleanupTimeout must be positive.");

        if (!Enum.IsDefined(options.MarketData.FeedLossPolicy))
        {
            failures.Add("MarketMaker:MarketData:FeedLossPolicy is not supported.");
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(options.MarketData.WsUrl) &&
                !MarketDataOptionsValidation.TryGetWebSocketUri(options.MarketData.WsUrl, out _))
            {
                failures.Add(
                    "MarketMaker:MarketData:WsUrl, if set, must be an absolute ws:// or wss:// URI.");
            }
            if (options.MarketData.FeedLossPolicy == FeedLossPolicy.PauseAndCancel)
            {
                if (string.IsNullOrWhiteSpace(options.MarketData.WsUrl))
                {
                    failures.Add(
                        "MarketMaker:MarketData:WsUrl must be nonblank when FeedLossPolicy is PauseAndCancel.");
                }
                if (options.MarketData.MaxReferenceAge <= TimeSpan.Zero)
                {
                    failures.Add(
                        "MarketMaker:MarketData:MaxReferenceAge must be positive when FeedLossPolicy is PauseAndCancel.");
                }
            }
        }

        for (var index = 0; index < options.Instruments.Count; index++)
        {
            var instrument = options.Instruments[index];
            var path = $"{MarketMakerBotOptions.SectionName}:Instruments:{index}";
            if (!string.IsNullOrWhiteSpace(instrument.Symbol) && !symbols.Add(instrument.Symbol))
                failures.Add($"{path}:Symbol '{instrument.Symbol}' is duplicated.");

            var skew = instrument.InventorySkew;
            if (skew?.Enabled == true)
            {
                if (skew.FullSkewAtLots <= 0)
                    failures.Add($"{path}:InventorySkew:FullSkewAtLots must be positive when enabled.");
                if (skew.MaxSkewTicks < 0m)
                    failures.Add($"{path}:InventorySkew:MaxSkewTicks must be nonnegative when enabled.");

                if (skew.FullSkewAtLots > 0 && instrument.LotSize > 0)
                {
                    try
                    {
                        _ = checked(skew.FullSkewAtLots * instrument.LotSize);
                    }
                    catch (OverflowException)
                    {
                        failures.Add(
                            $"{path}:InventorySkew:FullSkewAtLots times LotSize exceeds the supported quantity range.");
                    }
                }

                if (skew.MaxSkewTicks >= 0m && instrument.TickSize > 0m)
                {
                    try
                    {
                        _ = checked(skew.MaxSkewTicks * instrument.TickSize);
                    }
                    catch (OverflowException)
                    {
                        failures.Add(
                            $"{path}:InventorySkew:MaxSkewTicks times TickSize exceeds the supported price range.");
                    }
                }
            }

            var volatility = instrument.VolatilitySpread;
            if (volatility?.Enabled != true)
                continue;

            if (volatility.Window <= TimeSpan.Zero)
                failures.Add($"{path}:VolatilitySpread:Window must be positive when enabled.");
            if (volatility.MaxSamples <= 0)
                failures.Add($"{path}:VolatilitySpread:MaxSamples must be positive when enabled.");
            if (volatility.MinSamples <= 0)
                failures.Add($"{path}:VolatilitySpread:MinSamples must be positive when enabled.");
            if (volatility.MinSamples > volatility.MaxSamples)
                failures.Add($"{path}:VolatilitySpread:MinSamples must not exceed MaxSamples.");
            if (volatility.Multiplier <= 0m)
                failures.Add($"{path}:VolatilitySpread:Multiplier must be positive when enabled.");
            if (volatility.MaxAdditionalSpreadTicks < 0)
                failures.Add($"{path}:VolatilitySpread:MaxAdditionalSpreadTicks must be nonnegative when enabled.");

            if (instrument.SpreadTicks >= 0 && volatility.MaxAdditionalSpreadTicks >= 0 &&
                instrument.TickSize > 0m)
            {
                try
                {
                    var effectiveTicks = checked(instrument.SpreadTicks + volatility.MaxAdditionalSpreadTicks);
                    _ = checked(effectiveTicks * instrument.TickSize);
                }
                catch (OverflowException)
                {
                    failures.Add(
                        $"{path}:SpreadTicks plus VolatilitySpread:MaxAdditionalSpreadTicks exceeds the supported price range.");
                }
            }
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}

internal static class MarketDataOptionsValidation
{
    internal static bool TryGetWebSocketUri(string value, out Uri? uri)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out uri))
            return false;
        return string.Equals(uri.Scheme, Uri.UriSchemeWs, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(uri.Scheme, Uri.UriSchemeWss, StringComparison.OrdinalIgnoreCase);
    }
}
