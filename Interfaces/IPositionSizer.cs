using PaperTradingBot.Config;

namespace PaperTradingBot.Interfaces;

public interface IPositionSizer
{
    decimal GetBuyQuantity(decimal cash, decimal price, decimal totalEquity, SymbolConfig symbolConfig);
    decimal GetSellQuantity(decimal currentPositionQuantity, SymbolConfig symbolConfig);
}
