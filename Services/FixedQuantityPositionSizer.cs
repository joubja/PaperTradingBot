using PaperTradingBot.Config;
using PaperTradingBot.Interfaces;

namespace PaperTradingBot.Services;

public class FixedQuantityPositionSizer : IPositionSizer
{
    private readonly decimal _fixedQuantity;

    public FixedQuantityPositionSizer(decimal fixedQuantity)
    {
        if (fixedQuantity <= 0m)
            throw new ArgumentOutOfRangeException(nameof(fixedQuantity), "Fixed quantity must be greater than zero.");

        _fixedQuantity = fixedQuantity;
    }

    public decimal GetBuyQuantity(decimal cash, decimal price, decimal totalEquity, SymbolConfig symbolConfig)
    {
        if (price <= 0m)
            return 0m;

        var rounded = RoundDownToStep(_fixedQuantity, symbolConfig.QuantityStep);

        if (rounded <= 0m)
            return 0m;

        // Approximate affordability check; final exact check still happens in PaperBroker.
        return cash >= rounded * price ? rounded : 0m;
    }

    public decimal GetSellQuantity(decimal currentPositionQuantity, SymbolConfig symbolConfig)
    {
        if (currentPositionQuantity <= 0m)
            return 0m;

        var qty = Math.Min(currentPositionQuantity, _fixedQuantity);
        return RoundDownToStep(qty, symbolConfig.QuantityStep);
    }

    private static decimal RoundDownToStep(decimal value, decimal step)
    {
        if (step <= 0m)
            return value;

        var units = Math.Floor(value / step);
        return units * step;
    }
}
