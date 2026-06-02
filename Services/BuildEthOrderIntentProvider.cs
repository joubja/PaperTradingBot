using Microsoft.Extensions.Options;
using PaperTradingBot.Config;
using PaperTradingBot.Indicators;
using PaperTradingBot.Interfaces;
using PaperTradingBot.Models;

namespace PaperTradingBot.Services;

/// <summary>
/// BuildEth: pure ETH accumulation strategy. Only ever generates Buy intents.
///
/// Buys on two conditions:
///   1. RSI dip   — RSI(14) drops below 40, price is weak, good to accumulate.
///   2. Bull cross — EMA9 crosses above EMA21 with MACD histogram positive.
///
/// Never sells. The goal is to continuously grow ETH quantity using available cash.
///
/// Recommended config changes when using this strategy:
///   Risk.MaxPositionValuePercentPerSymbol: 0.90  (allow up to 90% of equity in ETH)
///   PositionSizing.TargetPositionValuePercent: 0.10  (buy 10% of equity per signal)
/// </summary>
public class BuildEthOrderIntentProvider : IOrderIntentProvider
{
    private const int Warmup = 25;
    private const int FastEmaPeriod = 9;
    private const int SlowEmaPeriod = 21;
    private const int RsiPeriod = 14;
    private const int AtrPeriod = 14;

    private readonly BotOptions _options;
    private readonly LiveSettingsService _settings;

    public BuildEthOrderIntentProvider(IOptions<BotOptions> options, LiveSettingsService settings)
    {
        _options  = options.Value;
        _settings = settings;
    }

    public OrderIntent GetIntent(string symbol, IReadOnlyList<Candle> history)
    {
        if (history.Count < Warmup)
            return OrderIntent.None($"Warming up ({history.Count}/{Warmup})");

        var closes = history.Select(c => c.Close).ToList();
        var prevCloses = closes.Take(closes.Count - 1).ToList();

        var fastEma = IndicatorMath.Ema(closes, FastEmaPeriod);
        var slowEma = IndicatorMath.Ema(closes, SlowEmaPeriod);
        var fastEmaPrev = IndicatorMath.Ema(prevCloses, FastEmaPeriod);
        var slowEmaPrev = IndicatorMath.Ema(prevCloses, SlowEmaPeriod);

        var rsi = RsiIndicator.Calculate(closes, RsiPeriod);
        var atr = AtrIndicator.Calculate(history, AtrPeriod);
        var macd = MacdIndicator.Calculate(closes);

        var close = closes[^1];
        var emaSpreadPct = slowEma > 0m ? Math.Abs(fastEma - slowEma) / slowEma * 100m : 0m;

        var indicatorContext =
            $"EMA9={fastEma:F4} EMA21={slowEma:F4} Spread={emaSpreadPct:F3}% " +
            $"RSI={rsi:F1} MACD_H={macd.Histogram:F6} ATR={atr:F6}";

        var rsiDipThreshold = _settings.RsiDipBuy;
        var rsiCrossoverMax = _settings.RsiCrossoverMax;

        // Signal 1: RSI dip — accumulate on weakness
        if (rsi < rsiDipThreshold)
        {
            var strength = Math.Round(Math.Clamp((rsiDipThreshold - rsi) / rsiDipThreshold, 0m, 1m), 4);
            var reason = $"RSI dip accumulation | RSI={rsi:F1} < {rsiDipThreshold:F0} | {indicatorContext}";
            return CreateBuyIntent(close, atr, strength, reason, indicatorContext);
        }

        // Signal 2: EMA bullish crossover with MACD confirmation
        bool bullishCross = fastEmaPrev <= slowEmaPrev && fastEma > slowEma;
        if (bullishCross && rsi < rsiCrossoverMax && macd.Histogram > 0m)
        {
            var rsiHeadroom = Math.Clamp((rsiCrossoverMax - rsi) / rsiCrossoverMax, 0m, 1m);
            var spreadScore = Math.Clamp(emaSpreadPct / 1.5m, 0m, 1m);
            var strength = Math.Round((rsiHeadroom + spreadScore) / 2m, 4);
            var reason = $"EMA{FastEmaPeriod}/EMA{SlowEmaPeriod} bull cross | RSI={rsi:F1} | {indicatorContext}";
            return CreateBuyIntent(close, atr, strength, reason, indicatorContext);
        }

        return OrderIntent.None($"No accumulation signal | {indicatorContext}");
    }

    private OrderIntent CreateBuyIntent(
        decimal close,
        decimal atr,
        decimal strength,
        string reason,
        string indicatorContext)
    {
        var orderType = _options.Orders.DefaultOrderType;
        var tif = _options.Orders.DefaultTimeInForce;
        var expireAfterBars = _options.Orders.DefaultExpireAfterBars;
        var useAtr = atr > 0m;
        var limitOffsetFraction = _options.Orders.LimitOffsetPercent / 100m;
        var stopOffsetFraction = _options.Orders.StopOffsetPercent / 100m;

        return orderType switch
        {
            OrderType.Market =>
                OrderIntent.MarketBuy(reason, tif, expireAfterBars, strength, indicatorContext),

            OrderType.Limit =>
                OrderIntent.LimitBuy(
                    limitPrice: useAtr ? close - atr : close * (1m - limitOffsetFraction),
                    reason: reason,
                    timeInForce: tif,
                    expireAfterBars: expireAfterBars,
                    signalStrength: strength,
                    indicatorContext: indicatorContext),

            OrderType.Stop =>
                OrderIntent.StopBuy(
                    stopPrice: useAtr ? close + atr : close * (1m + stopOffsetFraction),
                    reason: reason,
                    timeInForce: tif,
                    expireAfterBars: expireAfterBars,
                    signalStrength: strength,
                    indicatorContext: indicatorContext),

            OrderType.StopLimit =>
                OrderIntent.StopLimitBuy(
                    stopPrice: useAtr ? close + atr : close * (1m + stopOffsetFraction),
                    limitPrice: useAtr ? close + atr * 1.3m : close * (1m + stopOffsetFraction),
                    reason: reason,
                    timeInForce: tif,
                    expireAfterBars: expireAfterBars,
                    signalStrength: strength,
                    indicatorContext: indicatorContext),

            _ => OrderIntent.None("Unsupported order type")
        };
    }
}
