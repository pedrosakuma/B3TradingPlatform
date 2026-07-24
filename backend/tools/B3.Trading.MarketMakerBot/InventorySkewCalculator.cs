namespace B3.Trading.MarketMakerBot;

public readonly record struct InventorySkewResult(
    decimal NormalizedInventory,
    decimal SkewTicks,
    decimal MidShift);

/// <summary>Pure inventory normalization and quote-mid shift calculation.</summary>
public static class InventorySkewCalculator
{
    public static InventorySkewResult Calculate(
        InventorySkewConfig config,
        long netQuantity,
        long lotSize,
        decimal tickSize)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (!config.Enabled)
            return default;
        if (config.FullSkewAtLots <= 0)
            throw new ArgumentOutOfRangeException(nameof(config), "FullSkewAtLots must be positive when enabled.");
        if (config.MaxSkewTicks < 0m)
            throw new ArgumentOutOfRangeException(nameof(config), "MaxSkewTicks must be nonnegative when enabled.");
        if (lotSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(lotSize));
        if (tickSize <= 0m)
            throw new ArgumentOutOfRangeException(nameof(tickSize));

        var fullSkewQuantity = checked(config.FullSkewAtLots * lotSize);
        var normalized = Math.Clamp((decimal)netQuantity / fullSkewQuantity, -1m, 1m);
        var skewTicks = checked(config.MaxSkewTicks * normalized);
        var midShift = checked(-skewTicks * tickSize);
        return new(normalized, skewTicks, midShift);
    }
}
