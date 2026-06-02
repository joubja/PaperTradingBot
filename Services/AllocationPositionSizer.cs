using Microsoft.Extensions.Options;
using PaperTradingBot.Config;
using PaperTradingBot.Interfaces;

namespace PaperTradingBot.Services;

public class AllocationPositionSizer : IPositionSizer
{
    private readonly LiveSettingsService _settings;

    public AllocationPositionSizer(IOptions<BotOptions> options, LiveSettingsService settings)
    {
        _settings = settings;
    }

    public decimal GetBuyQuantity(decimal cash, decimal price, decimal totalEquity, SymbolConfig symbolConfig)
    {
        if (price <= 0m)
            return 0m;

        var targetValue = totalEquity * _settings.TargetPositionValuePercent;
        var spendable = Math.Min(cash, targetValue);

        if (spendable <= 0m)
            return 0m;

        var rawQuantity = spendable / price;
        return RoundDownToStep(rawQuantity, symbolConfig.QuantityStep);
    }

    public decimal GetSellQuantity(decimal currentPositionQuantity, SymbolConfig symbolConfig)
    {
        if (currentPositionQuantity <= 0m)
            return 0m;

        return RoundDownToStep(currentPositionQuantity, symbolConfig.QuantityStep);
    }

    private static decimal RoundDownToStep(decimal value, decimal step)
    {
        if (step <= 0m)
            return value;

        var units = Math.Floor(value / step);
        return units * step;
    }
}