namespace B3.Trading.MarketMakerBot.Tests;

internal sealed class ManualTimeProvider : TimeProvider
{
    private DateTimeOffset _now;

    public ManualTimeProvider(DateTimeOffset start) => _now = start;
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan delta) => _now += delta;
}
