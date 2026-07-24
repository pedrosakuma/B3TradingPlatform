using Microsoft.Extensions.Options;

namespace B3.Trading.MarketMakerBot;

public sealed class MarketMakerBotOptionsValidator : IValidateOptions<MarketMakerBotOptions>
{
    public ValidateOptionsResult Validate(string? name, MarketMakerBotOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var failures = new List<string>();
        var symbols = new HashSet<string>(StringComparer.Ordinal);

        for (var index = 0; index < options.Instruments.Count; index++)
        {
            var instrument = options.Instruments[index];
            var path = $"{MarketMakerBotOptions.SectionName}:Instruments:{index}";
            if (!string.IsNullOrWhiteSpace(instrument.Symbol) && !symbols.Add(instrument.Symbol))
                failures.Add($"{path}:Symbol '{instrument.Symbol}' is duplicated.");

            var skew = instrument.InventorySkew;
            if (skew is null || !skew.Enabled)
                continue;
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

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
