using System.Globalization;
using System.Text;
using PaperTradingBot.Models;

namespace PaperTradingBot.Services;

public static class CsvExporter
{
    public static void ExportTrades(string filePath, IReadOnlyList<Trade> trades)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Timestamp,Symbol,Side,Quantity,Price,Commission,Notional,RealizedPnL,Note");

        foreach (var trade in trades)
        {
            sb.AppendLine(string.Join(",",
                trade.Timestamp.ToString("o", CultureInfo.InvariantCulture),
                Escape(trade.Symbol),
                trade.Side.ToString(),
                trade.Quantity.ToString(CultureInfo.InvariantCulture),
                trade.Price.ToString(CultureInfo.InvariantCulture),
                trade.Commission.ToString(CultureInfo.InvariantCulture),
                trade.Notional.ToString(CultureInfo.InvariantCulture),
                trade.RealizedPnL.ToString(CultureInfo.InvariantCulture),
                Escape(trade.Note)));
        }

        File.WriteAllText(filePath, sb.ToString());
    }

    public static void ExportEquityCurve(string filePath, IReadOnlyList<EquityPoint> equityCurve)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Timestamp,Equity");

        foreach (var point in equityCurve)
        {
            sb.AppendLine(string.Join(",",
                point.Timestamp.ToString("o", CultureInfo.InvariantCulture),
                point.Equity.ToString(CultureInfo.InvariantCulture)));
        }

        File.WriteAllText(filePath, sb.ToString());
    }

    public static void ExportSummaries(string filePath, IReadOnlyList<BacktestSummary> summaries)
    {
        var sb = new StringBuilder();
        sb.AppendLine(
            "Symbol,FinalEquity,OpenPositionQuantity,AverageEntryPrice," +
            "UnrealizedPnL,RealizedPnL,TotalReturnPercent," +
            "TotalTrades,CompletedTrades,WinRatePercent,AverageWin,AverageLoss,ProfitFactor," +
            "ExposurePercent,SharpeRatio,SortinoRatio,CalmarRatio," +
            "MaxDrawdownDurationBars,LongestWinStreak,LongestLossStreak,AverageTradeDurationBars");

        foreach (var s in summaries)
        {
            sb.AppendLine(string.Join(",",
                Escape(s.Symbol),
                s.FinalEquity.ToString(CultureInfo.InvariantCulture),
                s.OpenPositionQuantity.ToString(CultureInfo.InvariantCulture),
                s.AverageEntryPrice.ToString(CultureInfo.InvariantCulture),
                s.UnrealizedPnL.ToString(CultureInfo.InvariantCulture),
                s.RealizedPnL.ToString(CultureInfo.InvariantCulture),
                s.TotalReturnPercent.ToString(CultureInfo.InvariantCulture),
                s.TotalTrades.ToString(CultureInfo.InvariantCulture),
                s.CompletedTrades.ToString(CultureInfo.InvariantCulture),
                s.WinRatePercent.ToString(CultureInfo.InvariantCulture),
                s.AverageWin.ToString(CultureInfo.InvariantCulture),
                s.AverageLoss.ToString(CultureInfo.InvariantCulture),
                s.ProfitFactor.ToString(CultureInfo.InvariantCulture),
                s.ExposurePercent.ToString(CultureInfo.InvariantCulture),
                s.SharpeRatio.ToString(CultureInfo.InvariantCulture),
                s.SortinoRatio.ToString(CultureInfo.InvariantCulture),
                s.CalmarRatio.ToString(CultureInfo.InvariantCulture),
                s.MaxDrawdownDurationBars.ToString(CultureInfo.InvariantCulture),
                s.LongestWinStreak.ToString(CultureInfo.InvariantCulture),
                s.LongestLossStreak.ToString(CultureInfo.InvariantCulture),
                s.AverageTradeDurationBars.ToString(CultureInfo.InvariantCulture)));
        }

        File.WriteAllText(filePath, sb.ToString());
    }

    private static string Escape(string value)
    {
        if (string.IsNullOrEmpty(value))
            return "";

        if (value.Contains(',') || value.Contains('"') || value.Contains('\n'))
        {
            return $"\"{value.Replace("\"", "\"\"")}\"";
        }

        return value;
    }
}