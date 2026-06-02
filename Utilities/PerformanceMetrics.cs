using PaperTradingBot.Indicators;
using PaperTradingBot.Models;
using PaperTradingBot.Services;

namespace PaperTradingBot.Utilities;

public static class PerformanceMetrics
{
    public static BacktestSummary BuildOverallSummary(BacktestResult result)
    {
        var realizedPnL = result.Trades
            .Where(t => t.Side == TradeSide.Sell)
            .Sum(t => t.RealizedPnL);

        var unrealizedPnL = result.Portfolio.GetUnrealizedPnL(result.LastPrices);
        var finalEquity = result.Portfolio.GetTotalEquity(result.LastPrices);

        var closedPnls = result.Trades
            .Where(t => t.Side == TradeSide.Sell)
            .Select(t => t.RealizedPnL)
            .ToList();

        var wins = closedPnls.Where(x => x > 0m).ToList();
        var losses = closedPnls.Where(x => x < 0m).ToList();

        var grossProfit = wins.Sum();
        var grossLossAbs = Math.Abs(losses.Sum());

        var totalReturnPct = result.StartingCash == 0m
            ? 0m
            : ((finalEquity - result.StartingCash) / result.StartingCash) * 100m;

        var maxDrawdownPct = CalculateMaxDrawdownPercent(result.EquityCurve);

        return new BacktestSummary
        {
            Symbol = "ALL",
            StartingCash = result.StartingCash,
            EndingCash = result.Portfolio.Cash,
            FinalEquity = finalEquity,
            OpenPositionQuantity = result.Portfolio.Positions.Values.Sum(x => x.Quantity),
            AverageEntryPrice = 0m,
            UnrealizedPnL = unrealizedPnL,
            RealizedPnL = realizedPnL,
            TotalReturnPercent = totalReturnPct,
            MaxDrawdownPercent = maxDrawdownPct,
            TotalTrades = result.Trades.Count,
            CompletedTrades = closedPnls.Count,
            WinRatePercent = closedPnls.Count == 0
                ? 0m
                : (decimal)wins.Count / closedPnls.Count * 100m,
            AverageWin = wins.Count == 0 ? 0m : wins.Average(),
            AverageLoss = losses.Count == 0 ? 0m : losses.Average(),
            ProfitFactor = grossLossAbs == 0m
                ? (grossProfit > 0m ? 999999m : 0m)
                : grossProfit / grossLossAbs,
            ExposurePercent = result.ExposurePercent,
            SharpeRatio = CalculateSharpeRatio(result.EquityCurve),
            SortinoRatio = CalculateSortinoRatio(result.EquityCurve),
            CalmarRatio = maxDrawdownPct == 0m ? 0m : Math.Round(totalReturnPct / maxDrawdownPct, 4),
            MaxDrawdownDurationBars = CalculateMaxDrawdownDurationBars(result.EquityCurve),
            LongestWinStreak = CalculateLongestStreak(closedPnls, win: true),
            LongestLossStreak = CalculateLongestStreak(closedPnls, win: false),
            AverageTradeDurationBars = CalculateAverageTradeDurationBars(result)
        };
    }

    public static List<BacktestSummary> BuildPerSymbolSummaries(BacktestResult result)
    {
        var summaries = new List<BacktestSummary>();

        foreach (var symbol in result.CandlesBySymbol.Keys.OrderBy(x => x))
        {
            result.Portfolio.Positions.TryGetValue(symbol, out var position);
            position ??= new Position { Symbol = symbol };

            var symbolTrades = result.Trades
                .Where(t => t.Symbol.Equals(symbol, StringComparison.OrdinalIgnoreCase))
                .ToList();

            var realizedPnL = symbolTrades
                .Where(t => t.Side == TradeSide.Sell)
                .Sum(t => t.RealizedPnL);

            var lastPrice = result.LastPrices.TryGetValue(symbol, out var price) ? price : 0m;
            var unrealizedPnL = position.HasPosition
                ? (lastPrice - position.AverageEntryPrice) * position.Quantity
                : 0m;

            var closedPnls = symbolTrades
                .Where(t => t.Side == TradeSide.Sell)
                .Select(t => t.RealizedPnL)
                .ToList();

            var wins = closedPnls.Where(x => x > 0m).ToList();
            var losses = closedPnls.Where(x => x < 0m).ToList();
            var grossProfit = wins.Sum();
            var grossLossAbs = Math.Abs(losses.Sum());

            var exposurePercent = CalculateSymbolExposurePercent(result, symbol);

            summaries.Add(new BacktestSummary
            {
                Symbol = symbol,
                StartingCash = result.StartingCash,
                EndingCash = 0m,
                FinalEquity = position.Quantity * lastPrice,
                OpenPositionQuantity = position.Quantity,
                AverageEntryPrice = position.AverageEntryPrice,
                UnrealizedPnL = unrealizedPnL,
                RealizedPnL = realizedPnL,
                TotalReturnPercent = result.StartingCash == 0m
                    ? 0m
                    : ((realizedPnL + unrealizedPnL) / result.StartingCash) * 100m,
                MaxDrawdownPercent = 0m,
                TotalTrades = symbolTrades.Count,
                CompletedTrades = closedPnls.Count,
                WinRatePercent = closedPnls.Count == 0
                    ? 0m
                    : (decimal)wins.Count / closedPnls.Count * 100m,
                AverageWin = wins.Count == 0 ? 0m : wins.Average(),
                AverageLoss = losses.Count == 0 ? 0m : losses.Average(),
                ProfitFactor = grossLossAbs == 0m
                    ? (grossProfit > 0m ? 999999m : 0m)
                    : grossProfit / grossLossAbs,
                ExposurePercent = exposurePercent,
                LongestWinStreak = CalculateLongestStreak(closedPnls, win: true),
                LongestLossStreak = CalculateLongestStreak(closedPnls, win: false)
            });
        }

        return summaries;
    }

    public static decimal CalculateMaxDrawdownPercent(IReadOnlyList<EquityPoint> equityCurve)
    {
        if (equityCurve.Count == 0)
            return 0m;

        decimal peak = equityCurve[0].Equity;
        decimal maxDrawdown = 0m;

        foreach (var point in equityCurve)
        {
            if (point.Equity > peak)
                peak = point.Equity;

            if (peak <= 0m)
                continue;

            var drawdown = (peak - point.Equity) / peak;
            if (drawdown > maxDrawdown)
                maxDrawdown = drawdown;
        }

        return maxDrawdown * 100m;
    }

    /// <summary>
    /// Annualised Sharpe ratio computed from bar-to-bar equity returns.
    /// Annualisation uses the median bar duration inferred from the equity curve timestamps.
    /// </summary>
    public static decimal CalculateSharpeRatio(IReadOnlyList<EquityPoint> equityCurve)
    {
        var returns = BarReturns(equityCurve);
        if (returns.Count < 2) return 0m;

        var mean = returns.Average();
        var stdev = IndicatorMath.StdDev(returns);
        if (stdev == 0m) return 0m;

        var annFactor = AnnualisationFactor(equityCurve, returns.Count);
        return Math.Round(mean / stdev * (decimal)Math.Sqrt(annFactor), 4);
    }

    /// <summary>
    /// Annualised Sortino ratio — uses downside semi-deviation in place of full std dev.
    /// </summary>
    public static decimal CalculateSortinoRatio(IReadOnlyList<EquityPoint> equityCurve)
    {
        var returns = BarReturns(equityCurve);
        if (returns.Count < 2) return 0m;

        var mean = returns.Average();
        var downside = IndicatorMath.DownsideDeviation(returns);
        if (downside == 0m) return mean > 0m ? 999m : 0m;

        var annFactor = AnnualisationFactor(equityCurve, returns.Count);
        return Math.Round(mean / downside * (decimal)Math.Sqrt(annFactor), 4);
    }

    /// <summary>
    /// Number of consecutive bars the equity curve stays below its running peak.
    /// </summary>
    public static int CalculateMaxDrawdownDurationBars(IReadOnlyList<EquityPoint> equityCurve)
    {
        if (equityCurve.Count == 0) return 0;

        decimal peak = equityCurve[0].Equity;
        int currentDuration = 0;
        int maxDuration = 0;

        foreach (var point in equityCurve)
        {
            if (point.Equity >= peak)
            {
                peak = point.Equity;
                currentDuration = 0;
            }
            else
            {
                currentDuration++;
                if (currentDuration > maxDuration)
                    maxDuration = currentDuration;
            }
        }

        return maxDuration;
    }

    /// <summary>
    /// Longest consecutive run of wins (win=true) or losses (win=false) in closed trade PnLs.
    /// </summary>
    public static int CalculateLongestStreak(IReadOnlyList<decimal> closedPnls, bool win)
    {
        int current = 0;
        int longest = 0;

        foreach (var pnl in closedPnls)
        {
            bool isWin = pnl > 0m;
            if (isWin == win)
            {
                current++;
                if (current > longest) longest = current;
            }
            else
            {
                current = 0;
            }
        }

        return longest;
    }

    /// <summary>
    /// Average number of candles between a buy trade and its matching sell trade, per symbol.
    /// Uses candle history from the result to count bars.
    /// </summary>
    public static decimal CalculateAverageTradeDurationBars(BacktestResult result)
    {
        var durations = new List<int>();

        foreach (var symbol in result.CandlesBySymbol.Keys)
        {
            if (!result.CandlesBySymbol.TryGetValue(symbol, out var candles) || candles.Count == 0)
                continue;

            var symbolTrades = result.Trades
                .Where(t => t.Symbol.Equals(symbol, StringComparison.OrdinalIgnoreCase))
                .OrderBy(t => t.Timestamp)
                .ToList();

            var buys = new Queue<DateTime>(
                symbolTrades.Where(t => t.Side == TradeSide.Buy).Select(t => t.Timestamp));

            foreach (var sell in symbolTrades.Where(t => t.Side == TradeSide.Sell))
            {
                if (buys.Count == 0) break;
                var buyTime = buys.Dequeue();

                var buyBar = IndexOf(candles, c => c.Timestamp >= buyTime);
                var sellBar = IndexOf(candles, c => c.Timestamp >= sell.Timestamp);

                if (buyBar >= 0 && sellBar >= buyBar)
                    durations.Add(sellBar - buyBar);
            }
        }

        return durations.Count == 0 ? 0m : (decimal)durations.Average();
    }

    private static List<decimal> BarReturns(IReadOnlyList<EquityPoint> equityCurve)
    {
        var returns = new List<decimal>(equityCurve.Count);

        for (var i = 1; i < equityCurve.Count; i++)
        {
            var prev = equityCurve[i - 1].Equity;
            if (prev == 0m) continue;
            returns.Add((equityCurve[i].Equity - prev) / prev);
        }

        return returns;
    }

    private static double AnnualisationFactor(IReadOnlyList<EquityPoint> equityCurve, int barCount)
    {
        if (equityCurve.Count < 2 || barCount == 0)
            return 252; // default to trading days

        var totalSeconds = (equityCurve[^1].Timestamp - equityCurve[0].Timestamp).TotalSeconds;
        var secondsPerBar = totalSeconds / barCount;

        return secondsPerBar > 0
            ? 365.25 * 24 * 3600 / secondsPerBar
            : 252;
    }

    public static decimal CalculateSymbolExposurePercent(BacktestResult result, string symbol)
    {
        var symbolTrades = result.Trades
            .Where(t => t.Symbol.Equals(symbol, StringComparison.OrdinalIgnoreCase))
            .OrderBy(t => t.Timestamp)
            .ToList();

        if (!result.CandlesBySymbol.TryGetValue(symbol, out var candles) || candles.Count == 0)
            return 0m;

        int exposedBars = 0;
        decimal runningQty = 0m;

        var tradesByTimestamp = symbolTrades
            .GroupBy(t => t.Timestamp)
            .ToDictionary(g => g.Key, g => g.ToList());

        foreach (var candle in candles)
        {
            if (tradesByTimestamp.TryGetValue(candle.Timestamp, out var tradesAtTime))
            {
                foreach (var trade in tradesAtTime)
                {
                    runningQty += trade.Side == TradeSide.Buy ? trade.Quantity : -trade.Quantity;
                }
            }

            if (runningQty > 0m)
                exposedBars++;
        }

        return candles.Count == 0
            ? 0m
            : (decimal)exposedBars / candles.Count * 100m;
    }

    private static int IndexOf<T>(IReadOnlyList<T> list, Func<T, bool> predicate)
    {
        for (var i = 0; i < list.Count; i++)
            if (predicate(list[i])) return i;
        return -1;
    }
}