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
