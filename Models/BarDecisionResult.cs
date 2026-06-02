namespace PaperTradingBot.Models;

public enum BarDecisionStatus
{
    NoAction,
    Rejected,
    Ready
}

public class BarDecisionResult
{
    public string Symbol { get; set; } = string.Empty;

    public Candle Candle { get; set; } = new();

    public int HistoryCount { get; set; }

    public BarDecisionStatus Status { get; set; } = BarDecisionStatus.NoAction;

    public OrderIntent? Intent { get; set; }

    public decimal CurrentEquity { get; set; }

    public decimal CurrentSymbolMarketValue { get; set; }

    public string Reason { get; set; } = string.Empty;

    public bool IsReady => Status == BarDecisionStatus.Ready && Intent is not null;
}
