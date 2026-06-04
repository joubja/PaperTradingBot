using PaperTradingBot.Models;

namespace PaperTradingBot.Interfaces;

public interface IOrderIntentProvider
{
    OrderIntent GetIntent(string symbol, IReadOnlyList<Candle> history);

    // Called by LiveDemoRuntime when a sell intent was rejected by the pipeline
    // after the strategy already committed in-memory state (ActiveSell=true, DB row opened).
    // Default no-op so strategies that don't use cycle state don't need to implement it.
    void RollbackSell(string symbol) { }
}