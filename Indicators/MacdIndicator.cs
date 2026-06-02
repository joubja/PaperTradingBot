namespace PaperTradingBot.Indicators;

public record MacdResult(decimal Line, decimal Signal, decimal Histogram);

public static class MacdIndicator
{
    /// <summary>
    /// MACD(fastPeriod, slowPeriod, signalPeriod).
    /// Builds a MACD line history then applies EMA for the signal line.
    /// Returns zero-valued result when there is insufficient history.
    /// </summary>
    public static MacdResult Calculate(
        IReadOnlyList<decimal> closes,
        int fastPeriod = 12,
        int slowPeriod = 26,
        int signalPeriod = 9)
    {
        var minRequired = slowPeriod + signalPeriod;
        if (closes.Count < minRequired)
            return new MacdResult(0m, 0m, 0m);

        // Build MACD line history over the last (signalPeriod + slowPeriod) bars so that
        // the signal EMA has enough seeds without O(n²) cost on long histories.
        var windowSize = signalPeriod + slowPeriod;
        var startOffset = Math.Max(0, closes.Count - windowSize);

        var macdHistory = new List<decimal>(windowSize);

        for (var end = startOffset + slowPeriod; end <= closes.Count; end++)
        {
            // Use a slice ending at `end` for EMA computation
            var slice = closes.Count == end
                ? closes
                : closes.Take(end).ToList();

            var fast = IndicatorMath.Ema(slice, fastPeriod);
            var slow = IndicatorMath.Ema(slice, slowPeriod);
            macdHistory.Add(fast - slow);
        }

        if (macdHistory.Count < signalPeriod)
            return new MacdResult(0m, 0m, 0m);

        var macdLine = macdHistory[^1];
        var signalLine = IndicatorMath.Ema(macdHistory, signalPeriod);
        return new MacdResult(macdLine, signalLine, macdLine - signalLine);
    }
}
