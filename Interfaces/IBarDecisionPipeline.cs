using PaperTradingBot.Models;

namespace PaperTradingBot.Interfaces;

public interface IBarDecisionPipeline
{
    void Reset(IEnumerable<string> symbols);
    void SetStrategy(IOrderIntentProvider provider);

    IReadOnlyList<Candle> GetHistory(string symbol);

    Task<BarDecisionResult> ProcessClosedBarAsync(
        string symbol,
        Candle candle,
        IReadOnlyDictionary<string, decimal> lastPrices,
        decimal currentEquity,
        decimal currentSymbolMarketValue,
        CancellationToken cancellationToken);
}
