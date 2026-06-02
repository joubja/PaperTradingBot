namespace PaperTradingBot.Models;

public class RiskCheckContext
{
    public DateOnly Date { get; set; }
    public string Symbol { get; set; } = string.Empty;
    public OrderIntentType IntentType { get; set; } = OrderIntentType.None;
    public int SymbolBarCount { get; set; }
    public decimal CurrentPrice { get; set; }
    public decimal TotalEquity { get; set; }
    public decimal CurrentSymbolMarketValue { get; set; }
}
