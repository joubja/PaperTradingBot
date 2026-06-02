namespace PaperTradingBot.Models;

public class Portfolio
{
    public decimal Cash { get; set; }
    public Dictionary<string, Position> Positions { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Position GetOrCreatePosition(string symbol)
    {
        if (!Positions.TryGetValue(symbol, out var position))
        {
            position = new Position { Symbol = symbol };
            Positions[symbol] = position;
        }

        return position;
    }

    public bool HasPosition(string symbol)
        => Positions.TryGetValue(symbol, out var pos) && pos.HasPosition;

    public decimal GetMarketValue(IReadOnlyDictionary<string, decimal> lastPrices)
    {
        decimal total = 0m;

        foreach (var kvp in Positions)
        {
            if (!kvp.Value.HasPosition)
                continue;

            if (lastPrices.TryGetValue(kvp.Key, out var price))
            {
                total += kvp.Value.Quantity * price;
            }
        }

        return total;
    }

    public decimal GetUnrealizedPnL(IReadOnlyDictionary<string, decimal> lastPrices)
    {
        decimal total = 0m;

        foreach (var kvp in Positions)
        {
            var position = kvp.Value;
            if (!position.HasPosition)
                continue;

            if (lastPrices.TryGetValue(kvp.Key, out var price))
            {
                total += (price - position.AverageEntryPrice) * position.Quantity;
            }
        }

        return total;
    }

    public decimal GetPositionMarketValue(string symbol, decimal price)
    {
        if (!Positions.TryGetValue(symbol, out var position) || !position.HasPosition)
            return 0m;

        return position.Quantity * price;
    }

    public decimal GetTotalEquity(IReadOnlyDictionary<string, decimal> lastPrices)
        => Cash + GetMarketValue(lastPrices);
}