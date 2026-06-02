namespace PaperTradingBot.Indicators;

public static class RsiIndicator
{
    /// <summary>
    /// Wilder's RSI over the provided close prices.
    /// Returns 50 (neutral) when there is insufficient history.
    /// </summary>
    public static decimal Calculate(IReadOnlyList<decimal> closes, int period = 14)
    {
        if (closes.Count < period + 1)
            return 50m;

        // Seed: simple average of first `period` gains/losses
        decimal avgGain = 0m;
        decimal avgLoss = 0m;

        for (var i = 1; i <= period; i++)
        {
            var change = closes[i] - closes[i - 1];
            if (change > 0) avgGain += change;
            else avgLoss += Math.Abs(change);
        }

        avgGain /= period;
        avgLoss /= period;

        // Wilder smoothing for the remaining bars
        for (var i = period + 1; i < closes.Count; i++)
        {
            var change = closes[i] - closes[i - 1];
            var gain = change > 0 ? change : 0m;
            var loss = change < 0 ? Math.Abs(change) : 0m;

            avgGain = (avgGain * (period - 1) + gain) / period;
            avgLoss = (avgLoss * (period - 1) + loss) / period;
        }

        if (avgLoss == 0m)
            return avgGain > 0m ? 100m : 50m;

        var rs = avgGain / avgLoss;
        return 100m - (100m / (1m + rs));
    }
}
