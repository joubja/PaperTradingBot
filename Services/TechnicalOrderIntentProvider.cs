using Microsoft.Extensions.Options;
using PaperTradingBot.Config;
using PaperTradingBot.Indicators;
using PaperTradingBot.Interfaces;
using PaperTradingBot.Models;

namespace PaperTradingBot.Services;

/// <summary>
/// Strategy: EMA9/21 crossover with RSI(14) filter and MACD(12,26,9) confirmation.
///
/// Buy  signal: EMA9 crosses above EMA21, RSI below 65, MACD histogram positive.
/// Sell signal: EMA9 crosses below EMA21, RSI above 35, MACD histogram negative.
///
/// Stop/limit prices are derived from ATR(14) so they adapt to current volatility.
/// SignalStrength reflects EMA spread width and RSI headroom from extremes.
/// </summary>
public class TechnicalOrderIntentProvider : IOrderIntentProvider
{
    // Warmup: EMA26 needs 26 bars + 9 more for signal EMA = 35 minimum
    private const int Warmup = 40;

    private const int FastEmaPeriod = 9;
    private const int SlowEmaPeriod = 21;
    private const int AtrPeriod = 14;

    // ATR multipliers for stop and limit offset
    private const decimal StopAtrMultiplier  = 1.5m;
    private const decimal LimitAtrMultiplier = 1.0m;

    private readonly BotOptions _options;
    private readonly LiveSettingsService _settings;

    public TechnicalOrderIntentProvider(IOptions<BotOptions> options, LiveSettingsService settings)
    {
        _options  = options.Value;
        _settings = settings;
    }

    public OrderIntent GetIntent(string symbol, IReadOnlyList<Candle> history)
    {
        if (history.Count < Warmup)
            return OrderIntent.None($"Warming up ({history.Count}/{Warmup})");

        var rsiBuyMax  = _settings.RsiBuyMax;
        var rsiSellMin = _settings.RsiSellMin;
        var rsiPeriod  = _settings.RsiPeriod;

        var closes = history.Select(c => c.Close).ToList();
        var prevCloses = closes.Take(closes.Count - 1).ToList();

        var fastEma = IndicatorMath.Ema(closes, FastEmaPeriod);
        var slowEma = IndicatorMath.Ema(closes, SlowEmaPeriod);
        var fastEmaPrev = IndicatorMath.Ema(prevCloses, FastEmaPeriod);
        var slowEmaPrev = IndicatorMath.Ema(prevCloses, SlowEmaPeriod);

        var rsi = RsiIndicator.Calculate(closes, rsiPeriod);
        var atr = AtrIndicator.Calculate(history, AtrPeriod);
        var macd = MacdIndicator.Calculate(closes);

        var close = closes[^1];
        var emaSpreadPct = slowEma > 0m ? Math.Abs(fastEma - slowEma) / slowEma * 100m : 0m;

        bool bullishCross = fastEmaPrev <= slowEmaPrev && fastEma > slowEma;
        bool bearishCross = fastEmaPrev >= slowEmaPrev && fastEma < slowEma;

        var indicatorContext =
            $"EMA9={fastEma:F4} EMA21={slowEma:F4} Spread={emaSpreadPct:F3}% " +
            $"RSI={rsi:F1} MACD_H={macd.Histogram:F6} ATR={atr:F6}";

        if (bullishCross && rsi < rsiBuyMax && macd.Histogram > 0m)
        {
            var strength = CalculateBuyStrength(rsi, emaSpreadPct, rsiBuyMax);
            var reason = $"EMA{FastEmaPeriod}/EMA{SlowEmaPeriod} bull cross | {indicatorContext}";
            return CreateIntent(OrderIntentType.Buy, close, atr, strength, reason, indicatorContext);
        }

        if (bearishCross && rsi > rsiSellMin && macd.Histogram < 0m)
        {
            var strength = CalculateSellStrength(rsi, emaSpreadPct, rsiSellMin);
            var reason = $"EMA{FastEmaPeriod}/EMA{SlowEmaPeriod} bear cross | {indicatorContext}";
            return CreateIntent(OrderIntentType.Sell, close, atr, strength, reason, indicatorContext);
        }

        return OrderIntent.None($"No signal | {indicatorContext}");
    }

    private OrderIntent CreateIntent(
        OrderIntentType intentType,
        decimal close,
        decimal atr,
        decimal strength,
        string reason,
        string indicatorContext)
    {
        var orderType = _options.Orders.DefaultOrderType;
        var tif = _options.Orders.DefaultTimeInForce;
        var expireAfterBars = _options.Orders.DefaultExpireAfterBars;

        // Use ATR-based offsets when ATR is available; fall back to config percentages
        var useAtr = atr > 0m;
        var limitOffsetFraction = _options.Orders.LimitOffsetPercent / 100m;
        var stopOffsetFraction = _options.Orders.StopOffsetPercent / 100m;

        decimal limitBuyPrice = useAtr
            ? close - LimitAtrMultiplier * atr
            : close * (1m - limitOffsetFraction);

        decimal limitSellPrice = useAtr
            ? close + LimitAtrMultiplier * atr
            : close * (1m + limitOffsetFraction);

        decimal stopBuyPrice = useAtr
            ? close + StopAtrMultiplier * atr
            : close * (1m + stopOffsetFraction);

        decimal stopSellPrice = useAtr
            ? close - StopAtrMultiplier * atr
            : close * (1m - stopOffsetFraction);

        return orderType switch
        {
            OrderType.Market => intentType == OrderIntentType.Buy
                ? OrderIntent.MarketBuy(reason, tif, expireAfterBars, strength, indicatorContext)
                : OrderIntent.MarketSell(reason, tif, expireAfterBars, strength, indicatorContext),

            OrderType.Limit => intentType == OrderIntentType.Buy
                ? OrderIntent.LimitBuy(limitBuyPrice, reason, tif, expireAfterBars, strength, indicatorContext)
                : OrderIntent.LimitSell(limitSellPrice, reason, tif, expireAfterBars, strength, indicatorContext),

            OrderType.Stop => intentType == OrderIntentType.Buy
                ? OrderIntent.StopBuy(stopBuyPrice, reason, tif, expireAfterBars, strength, indicatorContext)
                : OrderIntent.StopSell(stopSellPrice, reason, tif, expireAfterBars, strength, indicatorContext),

            OrderType.StopLimit => intentType == OrderIntentType.Buy
                ? OrderIntent.StopLimitBuy(
                    stopPrice: stopBuyPrice,
                    limitPrice: stopBuyPrice + 0.3m * atr,
                    reason: reason,
                    timeInForce: tif,
                    expireAfterBars: expireAfterBars,
                    signalStrength: strength,
                    indicatorContext: indicatorContext)
                : OrderIntent.StopLimitSell(
                    stopPrice: stopSellPrice,
                    limitPrice: stopSellPrice - 0.3m * atr,
                    reason: reason,
                    timeInForce: tif,
                    expireAfterBars: expireAfterBars,
                    signalStrength: strength,
                    indicatorContext: indicatorContext),

            _ => OrderIntent.None("Unsupported order type")
        };
    }

    private static decimal CalculateBuyStrength(decimal rsi, decimal emaSpreadPct, decimal rsiBuyMax)
    {
        var rsiHeadroom = Math.Clamp((rsiBuyMax - rsi) / (rsiBuyMax - 50m), 0m, 1m);
        var spreadScore = Math.Clamp(emaSpreadPct / 1.5m, 0m, 1m);
        return Math.Round((rsiHeadroom + spreadScore) / 2m, 4);
    }

    private static decimal CalculateSellStrength(decimal rsi, decimal emaSpreadPct, decimal rsiSellMin)
    {
        var rsiHeadroom = Math.Clamp((rsi - rsiSellMin) / (50m - rsiSellMin), 0m, 1m);
        var spreadScore = Math.Clamp(emaSpreadPct / 1.5m, 0m, 1m);
        return Math.Round((rsiHeadroom + spreadScore) / 2m, 4);
    }
}
