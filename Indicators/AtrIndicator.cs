using PaperTradingBot.Models;

namespace PaperTradingBot.Indicators;

public static class AtrIndicator
{
    /// <summary>
    /// Average True Range using Wilder's smoothing.
    /// Returns 0 when there is insufficient history.
    /// </summary>
    public static decimal Calculate(IReadOnlyList<Candle> candles, int period = 14)
    {
        if (candles.Count < period + 1)
            return 0m;

        // True range for each bar: max(H-L, |H-prevC|, |L-prevC|)
        static decimal TrueRange(Candle cur, Candle prev) =>
            Math.Max(cur.High - cur.Low,
                Math.Max(Math.Abs(cur.High - prev.Close), Math.Abs(cur.Low - prev.Close)));

        // Seed ATR with simple average of first `period` true ranges
        var atr = 0m;
        for (var i = 1; i <= period; i++)
            atr += TrueRange(candles[i], candles[i - 1]);
        atr /= period;

        // Wilder smoothing for the remaining bars
        for (var i = period + 1; i < candles.Count; i++)
            atr = (atr * (period - 1) + TrueRange(candles[i], candles[i - 1])) / period;

        return atr;
    }
}
