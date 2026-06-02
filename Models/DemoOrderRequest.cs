namespace PaperTradingBot.Models;

public class DemoOrderRequest
{
    public string Symbol { get; set; } = string.Empty;

    /// <summary>
    /// "Buy" or "Sell"
    /// </summary>
    public string Side { get; set; } = string.Empty;

    public decimal Quantity { get; set; }

    /// <summary>
    /// The current bar/market reference price used to simulate the fill.
    /// In the current live-demo flow, this will usually be the latest bar close.
    /// </summary>
    public decimal ReferencePrice { get; set; }

    public decimal? LimitPrice { get; set; }
    public decimal? StopPrice { get; set; }

    public string Reason { get; set; } = string.Empty;
}