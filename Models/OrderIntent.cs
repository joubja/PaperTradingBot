namespace PaperTradingBot.Models;

public class OrderIntent
{
    public OrderIntentType IntentType { get; init; } = OrderIntentType.None;
    public OrderType OrderType { get; init; } = OrderType.Market;
    public TimeInForce TimeInForce { get; init; } = TimeInForce.Gtc;

    /// <summary>
    /// Optional per-intent override for maximum lifetime in bars.
    /// Null means use the engine default from configuration.
    /// </summary>
    public int? ExpireAfterBars { get; init; }

    public decimal? LimitPrice { get; init; }
    public decimal? StopPrice { get; init; }
    public string Reason { get; init; } = "None";

    /// <summary>0–1 score produced by the strategy. 0 when not set.</summary>
    public decimal SignalStrength { get; init; } = 0m;

    /// <summary>Human-readable snapshot of indicator values at signal time.</summary>
    public string IndicatorContext { get; init; } = string.Empty;

    /// <summary>
    /// When set, overrides the position sizer's quantity calculation.
    /// Used by strategies that need partial fills (e.g. sell 35% of position).
    /// </summary>
    public decimal? TargetQuantityOverride { get; init; }

    /// <summary>
    /// When set, the CyclingCycles DB row with this ID will be updated with the
    /// actual fill quantity and price after execution, correcting the pre-execution
    /// estimate written by CompleteCycleAndCheckFeasibility.
    /// </summary>
    public int? CycleId { get; init; }

    public bool IsActionable =>
        IntentType == OrderIntentType.Buy || IntentType == OrderIntentType.Sell;

    public static OrderIntent None(string reason = "None")
        => new()
        {
            IntentType = OrderIntentType.None,
            Reason = reason
        };

    public static OrderIntent MarketBuy(
        string reason,
        TimeInForce timeInForce = TimeInForce.Gtc,
        int? expireAfterBars = null,
        decimal signalStrength = 0m,
        string indicatorContext = "")
        => new()
        {
            IntentType = OrderIntentType.Buy,
            OrderType = OrderType.Market,
            TimeInForce = timeInForce,
            ExpireAfterBars = expireAfterBars,
            Reason = reason,
            SignalStrength = signalStrength,
            IndicatorContext = indicatorContext
        };

    public static OrderIntent MarketSell(
        string reason,
        TimeInForce timeInForce = TimeInForce.Gtc,
        int? expireAfterBars = null,
        decimal signalStrength = 0m,
        string indicatorContext = "")
        => new()
        {
            IntentType = OrderIntentType.Sell,
            OrderType = OrderType.Market,
            TimeInForce = timeInForce,
            ExpireAfterBars = expireAfterBars,
            Reason = reason,
            SignalStrength = signalStrength,
            IndicatorContext = indicatorContext
        };

    public static OrderIntent LimitBuy(
        decimal limitPrice,
        string reason,
        TimeInForce timeInForce = TimeInForce.Gtc,
        int? expireAfterBars = null,
        decimal signalStrength = 0m,
        string indicatorContext = "")
        => new()
        {
            IntentType = OrderIntentType.Buy,
            OrderType = OrderType.Limit,
            TimeInForce = timeInForce,
            ExpireAfterBars = expireAfterBars,
            LimitPrice = limitPrice,
            Reason = reason,
            SignalStrength = signalStrength,
            IndicatorContext = indicatorContext
        };

    public static OrderIntent LimitSell(
        decimal limitPrice,
        string reason,
        TimeInForce timeInForce = TimeInForce.Gtc,
        int? expireAfterBars = null,
        decimal signalStrength = 0m,
        string indicatorContext = "")
        => new()
        {
            IntentType = OrderIntentType.Sell,
            OrderType = OrderType.Limit,
            TimeInForce = timeInForce,
            ExpireAfterBars = expireAfterBars,
            LimitPrice = limitPrice,
            Reason = reason,
            SignalStrength = signalStrength,
            IndicatorContext = indicatorContext
        };

    public static OrderIntent StopBuy(
        decimal stopPrice,
        string reason,
        TimeInForce timeInForce = TimeInForce.Gtc,
        int? expireAfterBars = null,
        decimal signalStrength = 0m,
        string indicatorContext = "")
        => new()
        {
            IntentType = OrderIntentType.Buy,
            OrderType = OrderType.Stop,
            TimeInForce = timeInForce,
            ExpireAfterBars = expireAfterBars,
            StopPrice = stopPrice,
            Reason = reason,
            SignalStrength = signalStrength,
            IndicatorContext = indicatorContext
        };

    public static OrderIntent StopSell(
        decimal stopPrice,
        string reason,
        TimeInForce timeInForce = TimeInForce.Gtc,
        int? expireAfterBars = null,
        decimal signalStrength = 0m,
        string indicatorContext = "")
        => new()
        {
            IntentType = OrderIntentType.Sell,
            OrderType = OrderType.Stop,
            TimeInForce = timeInForce,
            ExpireAfterBars = expireAfterBars,
            StopPrice = stopPrice,
            Reason = reason,
            SignalStrength = signalStrength,
            IndicatorContext = indicatorContext
        };

    public static OrderIntent StopLimitBuy(
        decimal stopPrice,
        decimal limitPrice,
        string reason,
        TimeInForce timeInForce = TimeInForce.Gtc,
        int? expireAfterBars = null,
        decimal signalStrength = 0m,
        string indicatorContext = "")
        => new()
        {
            IntentType = OrderIntentType.Buy,
            OrderType = OrderType.StopLimit,
            TimeInForce = timeInForce,
            ExpireAfterBars = expireAfterBars,
            StopPrice = stopPrice,
            LimitPrice = limitPrice,
            Reason = reason,
            SignalStrength = signalStrength,
            IndicatorContext = indicatorContext
        };

    public static OrderIntent StopLimitSell(
        decimal stopPrice,
        decimal limitPrice,
        string reason,
        TimeInForce timeInForce = TimeInForce.Gtc,
        int? expireAfterBars = null,
        decimal signalStrength = 0m,
        string indicatorContext = "")
        => new()
        {
            IntentType = OrderIntentType.Sell,
            OrderType = OrderType.StopLimit,
            TimeInForce = timeInForce,
            ExpireAfterBars = expireAfterBars,
            StopPrice = stopPrice,
            LimitPrice = limitPrice,
            Reason = reason,
            SignalStrength = signalStrength,
            IndicatorContext = indicatorContext
        };
}