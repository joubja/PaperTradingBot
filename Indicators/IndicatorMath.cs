namespace PaperTradingBot.Indicators;

public static class IndicatorMath
{
    /// <summary>
    /// Exponential moving average over the full values list.
    /// Seeded with the SMA of the first <paramref name="period"/> values.
    /// Returns 0 if there are fewer values than the period.
    /// </summary>
    public static decimal Ema(IReadOnlyList<decimal> values, int period)
    {
        if (period <= 0 || values.Count < period)
            return 0m;

        var k = 2m / (period + 1);
        var ema = values.Take(period).Average();

        for (var i = period; i < values.Count; i++)
            ema = values[i] * k + ema * (1m - k);

        return ema;
    }

    /// <summary>
    /// Simple moving average of the last <paramref name="period"/> values.
    /// Returns 0 if there are fewer values than the period.
    /// </summary>
    public static decimal Sma(IReadOnlyList<decimal> values, int period)
    {
        if (period <= 0 || values.Count < period)
            return 0m;

        return values.Skip(values.Count - period).Average();
    }

    /// <summary>
    /// Kaufman Efficiency Ratio over the last <paramref name="period"/> values:
    /// |net change| / Σ|bar-to-bar change|. Range 0..1 — near 1 = clean directional
    /// trend, near 0 = choppy back-and-forth. Used as a "trend vs chop" gate.
    /// Returns 0 if there are fewer than period+1 values or the path length is 0.
    /// </summary>
    public static decimal EfficiencyRatio(IReadOnlyList<decimal> values, int period)
    {
        if (period <= 0 || values.Count < period + 1)
            return 0m;

        var start = values.Count - period - 1;
        var netChange = Math.Abs(values[^1] - values[start]);

        var pathLength = 0m;
        for (var i = start + 1; i < values.Count; i++)
            pathLength += Math.Abs(values[i] - values[i - 1]);

        return pathLength > 0m ? netChange / pathLength : 0m;
    }

    public static decimal StdDev(IReadOnlyList<decimal> values)
    {
        if (values.Count < 2)
            return 0m;

        var mean = values.Average();
        var variance = values.Select(v => (v - mean) * (v - mean)).Average();
        return (decimal)Math.Sqrt((double)variance);
    }

    /// <summary>
    /// Downside semi-deviation of values below <paramref name="threshold"/> (default 0).
    /// Used for the Sortino ratio.
    /// </summary>
    public static decimal DownsideDeviation(IReadOnlyList<decimal> values, decimal threshold = 0m)
    {
        if (values.Count < 2)
            return 0m;

        var squaredDownside = values
            .Select(v => v < threshold ? (v - threshold) * (v - threshold) : 0m)
            .Average();

        return (decimal)Math.Sqrt((double)squaredDownside);
    }
}
