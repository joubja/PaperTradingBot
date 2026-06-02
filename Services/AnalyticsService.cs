using PaperTradingBot.Models;
using PaperTradingBot.Utilities;

namespace PaperTradingBot.Services;

public class AnalyticsService
{
    public BacktestSummary BuildOverallSummary(BacktestResult result)
        => PerformanceMetrics.BuildOverallSummary(result);

    public List<BacktestSummary> BuildPerSymbolSummaries(BacktestResult result)
        => PerformanceMetrics.BuildPerSymbolSummaries(result);

    public void PrintOverallSummary(BacktestSummary summary)
    {
        Console.WriteLine("===== OVERALL SUMMARY =====");
        Console.WriteLine($"Starting Cash:      {summary.StartingCash,12:F2}");
        Console.WriteLine($"Ending Cash:        {summary.EndingCash,12:F2}");
        Console.WriteLine($"Final Equity:       {summary.FinalEquity,12:F2}");
        Console.WriteLine($"Unrealized PnL:     {summary.UnrealizedPnL,12:F2}");
        Console.WriteLine($"Realized PnL:       {summary.RealizedPnL,12:F2}");
        Console.WriteLine($"Total Return:       {summary.TotalReturnPercent,11:F2}%");
        Console.WriteLine($"Max Drawdown:       {summary.MaxDrawdownPercent,11:F2}%");
        Console.WriteLine($"Max DD Duration:    {summary.MaxDrawdownDurationBars,9} bars");
        Console.WriteLine($"Exposure:           {summary.ExposurePercent,11:F2}%");
        Console.WriteLine($"--- Risk-Adjusted ---");
        Console.WriteLine($"Sharpe Ratio:       {summary.SharpeRatio,12:F4}");
        Console.WriteLine($"Sortino Ratio:      {summary.SortinoRatio,12:F4}");
        Console.WriteLine($"Calmar Ratio:       {summary.CalmarRatio,12:F4}");
        Console.WriteLine($"--- Trades ---");
        Console.WriteLine($"Trades:             {summary.TotalTrades,12}");
        Console.WriteLine($"Completed Trades:   {summary.CompletedTrades,12}");
        Console.WriteLine($"Win Rate:           {summary.WinRatePercent,11:F2}%");
        Console.WriteLine($"Average Win:        {summary.AverageWin,12:F2}");
        Console.WriteLine($"Average Loss:       {summary.AverageLoss,12:F2}");
        Console.WriteLine($"Profit Factor:      {summary.ProfitFactor,12:F4}");
        Console.WriteLine($"Longest Win Streak: {summary.LongestWinStreak,12}");
        Console.WriteLine($"Longest Loss Streak:{summary.LongestLossStreak,12}");
        Console.WriteLine($"Avg Trade Duration: {summary.AverageTradeDurationBars,9:F1} bars");
        Console.WriteLine();
    }

    public void PrintPerSymbolSummaries(IReadOnlyList<BacktestSummary> summaries)
    {
        Console.WriteLine("===== PER-SYMBOL SUMMARY =====");

        foreach (var summary in summaries)
        {
            Console.WriteLine(
                $"{summary.Symbol,-10} | Trades={summary.TotalTrades,3} | " +
                $"Closed={summary.CompletedTrades,3} | " +
                $"RealizedPnL={summary.RealizedPnL,10:F2} | " +
                $"UnrealizedPnL={summary.UnrealizedPnL,10:F2} | " +
                $"WinRate={summary.WinRatePercent,6:F2}% | " +
                $"ProfitFactor={summary.ProfitFactor,8:F2} | " +
                $"Exposure={summary.ExposurePercent,6:F2}%");
        }

        Console.WriteLine();
    }

    public void PrintTrades(IReadOnlyList<Trade> trades)
    {
        Console.WriteLine("===== TRADES =====");

        if (trades.Count == 0)
        {
            Console.WriteLine("No trades executed.");
            Console.WriteLine();
            return;
        }

        foreach (var trade in trades)
        {
            Console.WriteLine(
                $"{trade.Timestamp:u} | {trade.Symbol,-8} | {trade.Side,-4} | " +
                $"Qty={trade.Quantity,10:F4} | " +
                $"Price={trade.Price,10:F4} | " +
                $"Commission={trade.Commission,8:F2} | " +
                $"RealizedPnL={trade.RealizedPnL,10:F2} | " +
                $"Note={trade.Note}");
        }

        Console.WriteLine();
    }
}