namespace PaperTradingBot.AI;

/// <summary>
/// Fired by AccumulationTracker when a cycle sell allows us to score a prior
/// accumulation buy. Reward = tanh((sellPrice/buyPrice - 1) * 50), so a 2 %
/// gain → ~+0.76 and a 2 % loss → ~-0.76.
/// </summary>
public sealed class AccumulationFeedbackEvent
{
    public string  Symbol        { get; init; } = "";
    public string  Trigger       { get; init; } = "";   // "RsiDip" | "EmaCross"
    public float[] FeaturesAtBuy { get; init; } = [];
    public decimal BuyPrice      { get; init; }
    public decimal SellPrice     { get; init; }
    public float   Reward        { get; init; }
}

/// <summary>
/// Singleton that records accumulation buy signals (RSI dip / EMA cross) and,
/// when a cycle sell fires, emits AccumulationFeedbackEvents so the NN can
/// learn whether each accumulation entry was well-timed.
///
/// Lots are cleared per-symbol when:
///   • A cycle sell fires (lots scored against that sell price).
///   • Session is reset (ResetSessionState on the strategy).
/// A ring buffer caps memory at MaxLotsPerSymbol most-recent buys.
/// </summary>
public sealed class AccumulationTracker
{
    private const int MaxLotsPerSymbol = 20;

    private readonly Dictionary<string, List<AccumulationLot>> _lots =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    public event Action<AccumulationFeedbackEvent>? OnFeedback;

    public void RecordBuy(string symbol, float[] features, decimal buyPrice, string trigger)
    {
        lock (_lock)
        {
            if (!_lots.TryGetValue(symbol, out var list))
                _lots[symbol] = list = [];

            list.Add(new AccumulationLot(symbol, features, buyPrice, DateTime.UtcNow, trigger));
            if (list.Count > MaxLotsPerSymbol) list.RemoveAt(0);
        }
    }

    /// <summary>
    /// Called when a cycle sell fires. Scores each open lot against sellPrice,
    /// fires feedback events, then clears lots for the symbol.
    /// </summary>
    public void NotifyCycleSell(string symbol, decimal sellPrice)
    {
        List<AccumulationLot> lots;
        lock (_lock)
        {
            if (!_lots.TryGetValue(symbol, out var list) || list.Count == 0) return;
            lots = [.. list];
            list.Clear();
        }

        foreach (var lot in lots)
        {
            if (lot.FeaturesAtBuy.Length == 0) continue;

            var returnPct = lot.BuyPrice > 0m ? (sellPrice - lot.BuyPrice) / lot.BuyPrice : 0m;
            var reward    = (float)Math.Tanh((double)returnPct * 50.0);

            OnFeedback?.Invoke(new AccumulationFeedbackEvent
            {
                Symbol        = lot.Symbol,
                Trigger       = lot.Trigger,
                FeaturesAtBuy = lot.FeaturesAtBuy,
                BuyPrice      = lot.BuyPrice,
                SellPrice     = sellPrice,
                Reward        = reward
            });
        }
    }

    public void ClearAll()
    {
        lock (_lock) _lots.Clear();
    }

    private sealed record AccumulationLot(
        string   Symbol,
        float[]  FeaturesAtBuy,
        decimal  BuyPrice,
        DateTime BuyTime,
        string   Trigger);
}
