using PaperTradingBot.Interfaces;
using PaperTradingBot.Models;

namespace PaperTradingBot.Services;

public class InMemoryPortfolioStateStore : IPortfolioStateStore
{
    private readonly object _sync = new();
    private readonly Portfolio _portfolio = new();

    public void Reset(decimal startingCash)
    {
        lock (_sync)
        {
            _portfolio.Cash = startingCash;
            _portfolio.Positions.Clear();
        }
    }

    public void SeedPosition(string symbol, decimal quantity, decimal avgEntryPrice)
    {
        lock (_sync)
        {
            var pos = _portfolio.GetOrCreatePosition(symbol);
            pos.Quantity           = quantity;
            pos.AverageEntryPrice  = avgEntryPrice;
        }
    }

    public Portfolio GetPortfolioSnapshot()
    {
        lock (_sync)
        {
            var clone = new Portfolio
            {
                Cash = _portfolio.Cash
            };

            foreach (var kvp in _portfolio.Positions)
            {
                var pos = clone.GetOrCreatePosition(kvp.Key);
                pos.Quantity = kvp.Value.Quantity;
                pos.AverageEntryPrice = kvp.Value.AverageEntryPrice;
            }

            return clone;
        }
    }

    public decimal GetCash()
    {
        lock (_sync)
        {
            return _portfolio.Cash;
        }
    }

    public bool HasPosition(string symbol)
    {
        lock (_sync)
        {
            return _portfolio.HasPosition(symbol);
        }
    }

    public decimal GetPositionQuantity(string symbol)
    {
        lock (_sync)
        {
            return _portfolio.Positions.TryGetValue(symbol, out var pos) ? pos.Quantity : 0m;
        }
    }

    public decimal GetAverageEntryPrice(string symbol)
    {
        lock (_sync)
        {
            return _portfolio.Positions.TryGetValue(symbol, out var pos) ? pos.AverageEntryPrice : 0m;
        }
    }

    public decimal GetPositionMarketValue(string symbol, decimal price)
    {
        lock (_sync)
        {
            return _portfolio.GetPositionMarketValue(symbol, price);
        }
    }

    public decimal GetTotalMarketValue(IReadOnlyDictionary<string, decimal> lastPrices)
    {
        lock (_sync)
        {
            return _portfolio.GetMarketValue(lastPrices);
        }
    }

    public decimal GetTotalEquity(IReadOnlyDictionary<string, decimal> lastPrices)
    {
        lock (_sync)
        {
            return _portfolio.GetTotalEquity(lastPrices);
        }
    }

    public decimal GetUnrealizedPnL(IReadOnlyDictionary<string, decimal> lastPrices)
    {
        lock (_sync)
        {
            return _portfolio.GetUnrealizedPnL(lastPrices);
        }
    }

    public bool TryApplyBuyFill(
        string symbol,
        decimal quantity,
        decimal fillPrice,
        decimal commission,
        out string? rejectionReason)
    {
        lock (_sync)
        {
            rejectionReason = null;

            if (quantity <= 0m)
            {
                rejectionReason = "Buy quantity must be greater than zero.";
                return false;
            }

            if (fillPrice <= 0m)
            {
                rejectionReason = "Buy fill price must be greater than zero.";
                return false;
            }

            var totalCost = (fillPrice * quantity) + commission;
            if (_portfolio.Cash < totalCost)
            {
                rejectionReason = $"Insufficient cash. Needed={totalCost:F2}, Cash={_portfolio.Cash:F2}";
                return false;
            }

            var position = _portfolio.GetOrCreatePosition(symbol);

            var newQty = position.Quantity + quantity;
            var newCostBasis = (position.Quantity * position.AverageEntryPrice) + totalCost;
            var newAvg = newQty == 0m ? 0m : newCostBasis / newQty;

            position.Quantity = newQty;
            position.AverageEntryPrice = newAvg;
            _portfolio.Cash -= totalCost;

            return true;
        }
    }

    public bool TryApplySellFill(
        string symbol,
        decimal quantity,
        decimal fillPrice,
        decimal commission,
        out decimal realizedPnL,
        out string? rejectionReason)
    {
        lock (_sync)
        {
            realizedPnL = 0m;
            rejectionReason = null;

            if (quantity <= 0m)
            {
                rejectionReason = "Sell quantity must be greater than zero.";
                return false;
            }

            if (fillPrice <= 0m)
            {
                rejectionReason = "Sell fill price must be greater than zero.";
                return false;
            }

            var position = _portfolio.GetOrCreatePosition(symbol);

            if (!position.HasPosition || position.Quantity < quantity)
            {
                rejectionReason = $"Insufficient position quantity for symbol '{symbol}'.";
                return false;
            }

            var grossProceeds = fillPrice * quantity;
            var netProceeds = grossProceeds - commission;

            realizedPnL = netProceeds - (position.AverageEntryPrice * quantity);

            position.Quantity -= quantity;
            if (position.Quantity == 0m)
            {
                position.AverageEntryPrice = 0m;
            }

            _portfolio.Cash += netProceeds;
            return true;
        }
    }
}