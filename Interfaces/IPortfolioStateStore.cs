using PaperTradingBot.Models;

namespace PaperTradingBot.Interfaces;

public interface IPortfolioStateStore
{
    void Reset(decimal startingCash);

    void SeedPosition(string symbol, decimal quantity, decimal avgEntryPrice);

    Portfolio GetPortfolioSnapshot();

    decimal GetCash();

    bool HasPosition(string symbol);

    decimal GetPositionQuantity(string symbol);

    decimal GetAverageEntryPrice(string symbol);

    decimal GetPositionMarketValue(string symbol, decimal price);

    decimal GetTotalMarketValue(IReadOnlyDictionary<string, decimal> lastPrices);

    decimal GetTotalEquity(IReadOnlyDictionary<string, decimal> lastPrices);

    decimal GetUnrealizedPnL(IReadOnlyDictionary<string, decimal> lastPrices);

    bool TryApplyBuyFill(
        string symbol,
        decimal quantity,
        decimal fillPrice,
        decimal commission,
        out string? rejectionReason);

    bool TryApplySellFill(
        string symbol,
        decimal quantity,
        decimal fillPrice,
        decimal commission,
        out decimal realizedPnL,
        out string? rejectionReason);
}