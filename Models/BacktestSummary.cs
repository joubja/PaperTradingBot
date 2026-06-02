namespace PaperTradingBot.Models;

public class BacktestSummary
{
    public string Symbol { get; set; } = "ALL";
    public decimal StartingCash { get; set; }
    public decimal EndingCash { get; set; }
    public decimal FinalEquity { get; set; }
    public decimal OpenPositionQuantity { get; set; }
    public decimal AverageEntryPrice { get; set; }
    public decimal UnrealizedPnL { get; set; }
    public decimal RealizedPnL { get; set; }
    public decimal TotalReturnPercent { get; set; }
    public decimal MaxDrawdownPercent { get; set; }
    public int TotalTrades { get; set; }
    public int CompletedTrades { get; set; }
    public decimal WinRatePercent { get; set; }
    public decimal AverageWin { get; set; }
    public decimal AverageLoss { get; set; }
    public decimal ProfitFactor { get; set; }
    public decimal ExposurePercent { get; set; }

    // Risk-adjusted performance
    public decimal SharpeRatio { get; set; }
    public decimal SortinoRatio { get; set; }
    public decimal CalmarRatio { get; set; }

    // Drawdown depth and duration
    public int MaxDrawdownDurationBars { get; set; }

    // Streak analysis
    public int LongestWinStreak { get; set; }
    public int LongestLossStreak { get; set; }

    // Trade timing
    public decimal AverageTradeDurationBars { get; set; }
}
