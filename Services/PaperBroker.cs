using Microsoft.Extensions.Options;
using PaperTradingBot.Config;
using PaperTradingBot.Interfaces;
using PaperTradingBot.Models;

namespace PaperTradingBot.Services;

public class PaperBroker
{
    private readonly BotOptions _options;
    private readonly IPortfolioStateStore _portfolioStateStore;

    public List<Trade> Trades { get; } = new();

    public PaperBroker(
        IOptions<BotOptions> options,
        IPortfolioStateStore portfolioStateStore)
    {
        _options = options.Value;
        _portfolioStateStore = portfolioStateStore;
    }

    public void Reset()
    {
        Trades.Clear();
        _portfolioStateStore.Reset(_options.StartingCash);
    }

    public Portfolio CurrentPortfolio => _portfolioStateStore.GetPortfolioSnapshot();

    public bool HasPosition(string symbol)
        => _portfolioStateStore.HasPosition(symbol);

    public decimal GetPositionQuantity(string symbol)
        => _portfolioStateStore.GetPositionQuantity(symbol);

    public decimal GetPositionMarketValue(string symbol, decimal price)
        => _portfolioStateStore.GetPositionMarketValue(symbol, price);

    public decimal GetTotalEquity(IReadOnlyDictionary<string, decimal> lastPrices)
        => _portfolioStateStore.GetTotalEquity(lastPrices);

    public bool Buy(string symbol, DateTime timestamp, decimal executionPrice, decimal quantity, string note = "")
    {
        if (quantity <= 0m || executionPrice <= 0m)
            return false;

        var fillPrice  = executionPrice * (1m + _options.SlippagePercent);
        var commission = fillPrice * quantity * _options.TakerFeePercent / 100m;

        if (!_portfolioStateStore.TryApplyBuyFill(symbol, quantity, fillPrice, commission, out _))
            return false;

        Trades.Add(new Trade
        {
            Timestamp = timestamp,
            Symbol = symbol,
            Side = TradeSide.Buy,
            Quantity = quantity,
            Price = fillPrice,
            Commission = commission,
            RealizedPnL = 0m,
            Note = note
        });

        return true;
    }

    public bool Sell(string symbol, DateTime timestamp, decimal executionPrice, decimal quantity, string note = "")
    {
        if (quantity <= 0m || executionPrice <= 0m)
            return false;

        var fillPrice  = executionPrice * (1m - _options.SlippagePercent);
        var commission = fillPrice * quantity * _options.TakerFeePercent / 100m;

        if (!_portfolioStateStore.TryApplySellFill(
                symbol,
                quantity,
                fillPrice,
                commission,
                out var realizedPnL,
                out _))
        {
            return false;
        }

        Trades.Add(new Trade
        {
            Timestamp = timestamp,
            Symbol = symbol,
            Side = TradeSide.Sell,
            Quantity = quantity,
            Price = fillPrice,
            Commission = commission,
            RealizedPnL = realizedPnL,
            Note = note
        });

        return true;
    }
}