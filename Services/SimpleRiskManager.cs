using Microsoft.Extensions.Options;
using PaperTradingBot.Config;
using PaperTradingBot.Interfaces;
using PaperTradingBot.Models;

namespace PaperTradingBot.Services;

public class SimpleRiskManager : IRiskManager
{
    private readonly BotOptions _options;
    private readonly LiveSettingsService _settings;
    private readonly object _sync = new();

    private DateOnly? _currentDay;
    private decimal _dayStartEquity;
    private decimal _dayStartEthPrice;  // locked in on first risk-check of each UTC day
    private int _tradesToday;

    private readonly Dictionary<string, int> _lastTradeBarBySymbol = new(StringComparer.OrdinalIgnoreCase);

    public SimpleRiskManager(IOptions<BotOptions> options, LiveSettingsService settings)
    {
        _options  = options.Value;
        _settings = settings;
    }

    public void BeginDayIfNeeded(DateOnly date, decimal currentEquity)
    {
        lock (_sync)
            BeginDayCore(date, currentEquity);
    }

    public RiskCheckResult CanQueueOrder(RiskCheckContext context)
    {
        lock (_sync)
        {
            BeginDayCore(context.Date, context.TotalEquity);

            var maxDailyLoss   = _settings.MaxDailyLossPercent;
            var maxTrades      = _settings.MaxTradesPerDay;
            var cooldownBars   = _settings.CooldownBarsAfterTrade;
            var maxPositionPct = _settings.MaxPositionValuePct;
            var targetSizePct  = _settings.TargetPositionValuePercent;

            // Lock in the start-of-day ETH price on the first risk check of each UTC day.
            // Simultaneously recalibrate _dayStartEquity at this same price so both baselines
            // are from the same instant — otherwise a price gap between midnight and the first
            // bar of the day makes the daily P&L comparison mathematically inconsistent.
            if (_dayStartEthPrice == 0m && context.CurrentPrice > 0m && context.CurrentSymbolMarketValue > 0m)
            {
                _dayStartEthPrice = context.CurrentPrice;
                var ethQtyNow = context.CurrentSymbolMarketValue / context.CurrentPrice;
                var cashNow   = context.TotalEquity - context.CurrentSymbolMarketValue;
                _dayStartEquity = ethQtyNow * _dayStartEthPrice + cashNow;
            }

            if (maxDailyLoss > 0m && _dayStartEquity > 0m && _dayStartEthPrice > 0m)
            {
                // Value current ETH at the start-of-day price so that a 7% ETH price drop
                // doesn't trip the circuit breaker — only actual trading losses do.
                var ethQty         = context.CurrentPrice > 0m
                                         ? context.CurrentSymbolMarketValue / context.CurrentPrice
                                         : 0m;
                var cash           = context.TotalEquity - context.CurrentSymbolMarketValue;
                var adjustedEquity = ethQty * _dayStartEthPrice + cash;
                var dailyPnlPercent = ((adjustedEquity - _dayStartEquity) / _dayStartEquity) * 100m;
                if (dailyPnlPercent <= -maxDailyLoss)
                    return RiskCheckResult.Deny(
                        $"Blocked by max daily loss rule ({dailyPnlPercent:F2}% <= -{maxDailyLoss:F2}%)");
            }

            if (maxTrades > 0 && _tradesToday >= maxTrades)
                return RiskCheckResult.Deny($"Blocked by max trades per day rule ({_tradesToday}/{maxTrades})");

            if (_lastTradeBarBySymbol.TryGetValue(context.Symbol, out var lastTradeBar))
            {
                var barsSinceTrade = context.SymbolBarCount - lastTradeBar;
                if (barsSinceTrade > 0 && barsSinceTrade <= cooldownBars)
                    return RiskCheckResult.Deny(
                        $"Blocked by cooldown rule ({barsSinceTrade} bars since last trade)");
            }

            if (context.IntentType == OrderIntentType.Buy && context.TotalEquity > 0m)
            {
                var positionPercent = context.CurrentSymbolMarketValue / context.TotalEquity;
                if (positionPercent >= maxPositionPct)
                    return RiskCheckResult.Deny(
                        $"Blocked by max position value rule ({positionPercent:P2} >= {maxPositionPct:P2})");

                if (_options.Risk.MinCashReservePercent > 0m)
                {
                    var cash         = context.TotalEquity - context.CurrentSymbolMarketValue;
                    var reserveFloor = _options.StartingCash * _options.Risk.MinCashReservePercent / 100m;

                    // Estimate how much this buy will spend so we deny *before* the trade
                    // pushes cash below the floor, not after.
                    var estimatedBuyValue  = Math.Min(cash, context.TotalEquity * targetSizePct);
                    var estimatedCashAfter = cash - estimatedBuyValue * (1m + _options.TakerFeePercent / 100m);

                    if (estimatedCashAfter < reserveFloor)
                        return RiskCheckResult.Deny(
                            $"Blocked by cash reserve rule (post-trade cash ~${estimatedCashAfter:F2} < ${reserveFloor:F2} floor)");
                }
            }

            return RiskCheckResult.Allow();
        }
    }

    public void RegisterExecutedTrade(DateOnly tradeDate, string symbol, int symbolBarCount)
    {
        lock (_sync)
        {
            BeginDayCore(tradeDate, _dayStartEquity);
            _tradesToday++;
            _lastTradeBarBySymbol[symbol] = symbolBarCount;
        }
    }

    private void BeginDayCore(DateOnly date, decimal currentEquity)
    {
        if (_currentDay == date)
            return;

        _currentDay = date;
        _dayStartEquity = currentEquity;
        _dayStartEthPrice = 0m;  // reset; locked in on first CanQueueOrder call of the day
        _tradesToday = 0;
    }
}
