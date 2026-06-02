namespace PaperTradingBot.Models;

public class PendingOrder
{
    public string Symbol { get; set; } = string.Empty;
    public OrderIntentType IntentType { get; set; } = OrderIntentType.None;
    public OrderType OrderType { get; set; } = OrderType.Market;
    public TimeInForce TimeInForce { get; set; } = TimeInForce.Gtc;

    public string Reason { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public int CreatedBarCount { get; set; }
    public int? ExpireAfterBars { get; set; }

    public decimal? LimitPrice { get; set; }
    public decimal? StopPrice { get; set; }

    /// <summary>
    /// Used by stop-limit orders to indicate that the stop trigger has fired
    /// and the order should now behave like a limit order.
    /// </summary>
    public bool StopTriggered { get; set; }

    public TradeSide Side => IntentType switch
    {
        OrderIntentType.Buy => TradeSide.Buy,
        OrderIntentType.Sell => TradeSide.Sell,
        _ => throw new InvalidOperationException("PendingOrder has no actionable side.")
    };
}