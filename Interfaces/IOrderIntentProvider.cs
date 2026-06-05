using PaperTradingBot.Models;

namespace PaperTradingBot.Interfaces;

public interface IOrderIntentProvider
{
    OrderIntent GetIntent(string symbol, IReadOnlyList<Candle> history);

    // Called by LiveDemoRuntime after pipeline + qty validation both approve a sell intent,
    // immediately before submitting to the execution gateway. Inserts the cycle DB row at
    // this point rather than in GetIntent() so pipeline/validation rejections don't leave
    // phantom cycle rows. Default no-op for strategies that don't track cycles.
    int? CommitSellToDB(string symbol, string sessionId) => null;

    // Called when a sell intent was rejected (pipeline, qty validation, or gateway) after
    // GetIntent() already committed in-memory state. Resets that state so the strategy
    // is not stuck. Default no-op for strategies that don't use cycle state.
    void RollbackSell(string symbol, string? reason = null) { }
}