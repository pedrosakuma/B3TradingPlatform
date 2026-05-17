using B3.Trading.Application.MarketData;
using Xunit;

namespace B3.Trading.Application.Tests.MarketData;

public class VolumeCurveEstimatorTests
{
    private static readonly DateTimeOffset Day1Start = new(
        DateOnly.FromDateTime(DateTime.UtcNow).ToDateTime(TimeOnly.MinValue),
        TimeSpan.Zero);
    private const string Sym = "PETR4";

    [Fact]
    public void CdfAt_NoData_ReturnsUniformFallback()
    {
        // Mirrors a TWAP-shaped curve so the engine still makes progress
        // before any trade arrives.
        var sut = new VolumeCurveEstimator();
        var start = Day1Start.AddHours(9);
        var end = start.AddHours(1);
        var mid = start.AddMinutes(30);

        Assert.Equal(0.5, sut.CdfAt(Sym, start, end, mid), 6);
    }

    [Fact]
    public void CdfAt_BeforeStart_Zero_AfterEnd_One()
    {
        var sut = new VolumeCurveEstimator();
        var start = Day1Start.AddHours(9);
        var end = start.AddHours(1);
        Assert.Equal(0d, sut.CdfAt(Sym, start, end, start.AddMinutes(-5)));
        Assert.Equal(0d, sut.CdfAt(Sym, start, end, start));
        Assert.Equal(1d, sut.CdfAt(Sym, start, end, end));
        Assert.Equal(1d, sut.CdfAt(Sym, start, end, end.AddMinutes(1)));
    }

    [Fact]
    public void CdfAt_UsesObservedVolume_BlendedWithExtrapolation()
    {
        // Pass-1 review (#294) P1#1B: at any point inside the window
        // the CDF denominator is observed[start..at] +
        // extrapolated[at..end]. With all observed volume in the first
        // half (run-rate identical to remainder extrapolation) the CDF
        // at the midpoint is 0.5 — NOT 1.0 as the old normalisation
        // would have wrongly returned.
        var sut = new VolumeCurveEstimator();
        var start = Day1Start.AddHours(9);
        var end = start.AddHours(1);
        var mid = start.AddMinutes(30);

        sut.RecordTrade(Sym, 1000, start.AddMinutes(5));
        sut.RecordTrade(Sym, 1000, start.AddMinutes(15));

        var cdf = sut.CdfAt(Sym, start, end, mid);
        Assert.Equal(0.5, cdf, 6);
    }

    [Fact]
    public void CdfAt_DoesNotJumpToOne_AfterSingleObservedBucket()
    {
        // Pass-1 review (#294) P1#1B regression. Window is 1h; only the
        // very first 5-min bucket has any volume. Evaluated 30min in,
        // the *old* code returned 1.0 (observed/observed = 1) which made
        // the VWAP scheduler think the day was done and over-slice. The
        // blended denominator keeps the CDF well below 1.
        var sut = new VolumeCurveEstimator();
        var start = Day1Start.AddHours(9);
        var end = start.AddHours(1);
        var at = start.AddMinutes(30);

        sut.RecordTrade(Sym, 500, start.AddMinutes(1)); // bucket 0 only

        var cdf = sut.CdfAt(Sym, start, end, at);
        Assert.True(cdf < 0.9, $"expected blended cdf < 0.9, got {cdf}");
        // With 1 observed bucket out of 6 elapsed and 6 remaining, the
        // run-rate extrapolation drops the cdf well below 0.5.
        Assert.True(cdf > 0, $"expected positive cdf, got {cdf}");
    }

    [Fact]
    public void CdfAt_Monotonic_NonDecreasing()
    {
        var sut = new VolumeCurveEstimator();
        var start = Day1Start.AddHours(9);
        var end = start.AddHours(1);
        sut.RecordTrade(Sym, 100, start.AddMinutes(5));
        sut.RecordTrade(Sym, 500, start.AddMinutes(35));

        double prev = 0;
        for (var m = 0; m <= 60; m += 5)
        {
            var cdf = sut.CdfAt(Sym, start, end, start.AddMinutes(m));
            Assert.True(cdf >= prev, $"cdf went down at minute {m}: {prev} → {cdf}");
            prev = cdf;
        }
    }

    [Fact]
    public void VolumeBetween_SumsAcrossBuckets()
    {
        var sut = new VolumeCurveEstimator();
        var start = Day1Start.AddHours(9);
        sut.RecordTrade(Sym, 100, start.AddMinutes(1));   // bucket 0
        sut.RecordTrade(Sym, 200, start.AddMinutes(6));   // bucket 1
        sut.RecordTrade(Sym, 300, start.AddMinutes(11));  // bucket 2

        Assert.Equal(600, sut.VolumeBetween(Sym, start, start.AddMinutes(15)));
        Assert.Equal(300, sut.VolumeBetween(Sym, start, start.AddMinutes(10)));
    }

    [Fact]
    public void VolumeBetween_DifferentSymbol_Isolated()
    {
        var sut = new VolumeCurveEstimator();
        var t = Day1Start.AddHours(9);
        sut.RecordTrade("PETR4", 100, t);
        sut.RecordTrade("VALE3", 200, t);
        Assert.Equal(100, sut.VolumeBetween("PETR4", t, t.AddMinutes(5)));
        Assert.Equal(200, sut.VolumeBetween("VALE3", t, t.AddMinutes(5)));
    }

    [Fact]
    public void VolumeBetween_SpansDayBoundary()
    {
        var sut = new VolumeCurveEstimator();
        var preMidnight = Day1Start.AddDays(1).AddMinutes(-10);
        var postMidnight = Day1Start.AddDays(1).AddMinutes(5);
        sut.RecordTrade(Sym, 100, preMidnight);
        sut.RecordTrade(Sym, 200, postMidnight);

        // Pass-1 review (#295) P1#2. VolumeBetween pro-rates the
        // boundary buckets, so the day-spanning range only captures
        // the FULL pre-midnight bucket (100) plus 1/5 of the
        // post-midnight bucket (200 * 1min/5min = 40).
        Assert.Equal(140, sut.VolumeBetween(Sym, preMidnight.AddMinutes(-1), postMidnight.AddMinutes(1)));
    }

    [Fact]
    public void VolumeBetween_ProRatesFirstBucket()
    {
        // Pass-1 review (#295) P1#2. Range starts mid-bucket: only
        // the elapsed-time fraction of the bucket's volume is
        // counted, so trades that arrived before the range start
        // do not leak in. Linear approximation assumes uniform
        // within-bucket distribution — see VolumeBetween's xmldoc.
        var sut = new VolumeCurveEstimator();
        var start = Day1Start.AddHours(9);
        sut.RecordTrade(Sym, 500, start.AddMinutes(1));  // bucket [0, 5min) qty=500
        sut.RecordTrade(Sym, 200, start.AddMinutes(6));  // bucket [5, 10min) qty=200

        // Range [start+3min, start+10min):
        //   - bucket [0,5): overlap [3,5) = 2/5 * 500 = 200
        //   - bucket [5,10): full = 200
        Assert.Equal(400, sut.VolumeBetween(Sym, start.AddMinutes(3), start.AddMinutes(10)));
    }

    [Fact]
    public void VolumeBetween_ProRatesLastBucket()
    {
        // Pass-1 review (#295) P1#2. Mirror: range ends mid-bucket
        // so post-end trades in the last bucket are not over-counted.
        var sut = new VolumeCurveEstimator();
        var start = Day1Start.AddHours(9);
        sut.RecordTrade(Sym, 500, start.AddMinutes(1));  // bucket [0, 5min) qty=500
        sut.RecordTrade(Sym, 200, start.AddMinutes(6));  // bucket [5, 10min) qty=200

        // Range [start, start+8min):
        //   - bucket [0,5): full = 500
        //   - bucket [5,10): overlap [5,8) = 3/5 * 200 = 120
        Assert.Equal(620, sut.VolumeBetween(Sym, start, start.AddMinutes(8)));
    }

    [Fact]
    public void VolumeBetween_RangeWithinSingleBucket()
    {
        // Pass-1 review (#295) P1#2. Edge case — range begins AND
        // ends inside the same bucket. The pro-rate fraction collapses
        // to the range's share of the bucket's total span.
        var sut = new VolumeCurveEstimator();
        var start = Day1Start.AddHours(9);
        sut.RecordTrade(Sym, 500, start.AddMinutes(2));  // bucket [0, 5min) qty=500

        // Range [start+1min, start+3min): 2/5 * 500 = 200
        Assert.Equal(200, sut.VolumeBetween(Sym, start.AddMinutes(1), start.AddMinutes(3)));
    }

    [Fact]
    public void RecordTrade_IgnoresNonPositiveQty()
    {
        var sut = new VolumeCurveEstimator();
        var t = Day1Start.AddHours(9);
        sut.RecordTrade(Sym, 0, t);
        sut.RecordTrade(Sym, -100, t);
        Assert.Equal(0, sut.VolumeBetween(Sym, t, t.AddMinutes(5)));
    }

    [Fact]
    public void RecordTrade_IgnoresEmptySymbol()
    {
        var sut = new VolumeCurveEstimator();
        var t = Day1Start.AddHours(9);
        sut.RecordTrade("", 100, t);
        sut.RecordTrade("   ", 100, t);
        Assert.Equal(0, sut.VolumeBetween("", t, t.AddMinutes(5)));
    }

    [Fact]
    public void BucketsPerDay_DefaultIs288()
    {
        var sut = new VolumeCurveEstimator();
        Assert.Equal(288, sut.BucketsPerDay);
    }
}
