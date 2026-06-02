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

            if (maxDailyLoss > 0m && _dayStartEquity > 0m)
            {
                var dailyPnlPercent = ((context.TotalEquity - _dayStartEquity) / _dayStartEquity) * 100m;
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
        _tradesToday = 0;
    }
}
