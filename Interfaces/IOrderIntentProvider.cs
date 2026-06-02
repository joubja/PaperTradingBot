using PaperTradingBot.Models;

namespace PaperTradingBot.Interfaces;

public interface IOrderIntentProvider
{
    OrderIntent GetIntent(string symbol, IReadOnlyList<Candle> history);
}