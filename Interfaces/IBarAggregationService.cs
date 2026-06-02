using PaperTradingBot.Models;

namespace PaperTradingBot.Interfaces;

public interface IBarAggregationService
{
    event Action<LiveBar>? OnBarClosed;

    void OnQuote(QuoteTick quote);
}