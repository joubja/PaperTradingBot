namespace PaperTradingBot.Models;

public enum TradeSide
{
    Buy,
    Sell
}

public class Trade
{
    public DateTime Timestamp { get; set; }
    public string Symbol { get; set; } = string.Empty;
    public TradeSide Side { get; set; }
    public decimal Quantity { get; set; }
    public decimal Price { get; set; }
    public decimal Commission { get; set; }
    public decimal Notional => Quantity * Price;
    public decimal RealizedPnL { get; set; }
    public string Note { get; set; } = string.Empty;
}