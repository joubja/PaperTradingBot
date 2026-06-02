using PaperTradingBot.Models;

namespace PaperTradingBot.Interfaces;

public interface IMarketDataFeed
{
    IReadOnlyDictionary<string, IReadOnlyList<Candle>> GetCandlesBySymbol();
}