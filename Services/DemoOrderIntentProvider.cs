using Microsoft.Extensions.Options;
using PaperTradingBot.Config;
using PaperTradingBot.Interfaces;
using PaperTradingBot.Models;

namespace PaperTradingBot.Services;

/// <summary>
/// Demo-only order intent provider used to validate the engine.
/// Logic:
/// - If the last 3 closes are strictly increasing => create BUY intent
/// - If the last 3 closes are strictly decreasing => create SELL intent
/// - Otherwise => no action
///
/// The provider decides:
/// - intent type (Buy/Sell/None)
/// - order type (Market/Limit/Stop/StopLimit)
/// - time in force
/// - limit / stop price (if needed)
///
/// This is demo-only and should not be treated as trading advice.
/// </summary>
public class DemoOrderIntentProvider : IOrderIntentProvider
{
    private readonly BotOptions _options;

    public DemoOrderIntentProvider(IOptions<BotOptions> options)
    {
        _options = options.Value;
    }

    public OrderIntent GetIntent(string symbol, IReadOnlyList<Candle> history)
    {
        if (history.Count < 3)
            return OrderIntent.None("Not enough data");

        var a = history[^3].Close;
        var b = history[^2].Close;
        var c = history[^1].Close;

        if (a < b && b < c)
        {
            return CreateIntent(
                intentType: OrderIntentType.Buy,
                referenceClose: c,
                reason: $"Demo intent for {symbol}: 3 rising closes");
        }

        if (a > b && b > c)
        {
            return CreateIntent(
                intentType: OrderIntentType.Sell,
                referenceClose: c,
                reason: $"Demo intent for {symbol}: 3 falling closes");
        }

        return OrderIntent.None("No demo pattern");
    }

    private OrderIntent CreateIntent(OrderIntentType intentType, decimal referenceClose, string reason)
    {
        var orderType = _options.Orders.DefaultOrderType;
        var tif = _options.Orders.DefaultTimeInForce;
        var expireAfterBars = _options.Orders.DefaultExpireAfterBars;

        var limitOffsetFraction = _options.Orders.LimitOffsetPercent / 100m;
        var stopOffsetFraction = _options.Orders.StopOffsetPercent / 100m;

        return orderType switch
        {
            OrderType.Market => intentType == OrderIntentType.Buy
                ? OrderIntent.MarketBuy(reason, tif, expireAfterBars)
                : OrderIntent.MarketSell(reason, tif, expireAfterBars),

            OrderType.Limit => intentType == OrderIntentType.Buy
                ? OrderIntent.LimitBuy(
                    limitPrice: referenceClose * (1m - limitOffsetFraction),
                    reason: reason,
                    timeInForce: tif,
                    expireAfterBars: expireAfterBars)
                : OrderIntent.LimitSell(
                    limitPrice: referenceClose * (1m + limitOffsetFraction),
                    reason: reason,
                    timeInForce: tif,
                    expireAfterBars: expireAfterBars),

            OrderType.Stop => intentType == OrderIntentType.Buy
                ? OrderIntent.StopBuy(
                    stopPrice: referenceClose * (1m + stopOffsetFraction),
                    reason: reason,
                    timeInForce: tif,
                    expireAfterBars: expireAfterBars)
                : OrderIntent.StopSell(
                    stopPrice: referenceClose * (1m - stopOffsetFraction),
                    reason: reason,
                    timeInForce: tif,
                    expireAfterBars: expireAfterBars),

            OrderType.StopLimit => intentType == OrderIntentType.Buy
                ? OrderIntent.StopLimitBuy(
                    stopPrice: referenceClose * (1m + stopOffsetFraction),
                    limitPrice: referenceClose * (1m + stopOffsetFraction),
                    reason: reason,
                    timeInForce: tif,
                    expireAfterBars: expireAfterBars)
                : OrderIntent.StopLimitSell(
                    stopPrice: referenceClose * (1m - stopOffsetFraction),
                    limitPrice: referenceClose * (1m - stopOffsetFraction),
                    reason: reason,
                    timeInForce: tif,
                    expireAfterBars: expireAfterBars),

            _ => OrderIntent.None("Unsupported order type")
        };
    }
}
