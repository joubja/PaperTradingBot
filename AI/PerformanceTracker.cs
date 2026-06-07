namespace PaperTradingBot.AI;

public sealed record AccumulationOutcome(
    string   Trigger,
    decimal  BuyPrice,
    decimal  SellPrice,
    float    Reward,
    DateTime RecordedAt);

/// <summary>
/// Emitted by BuildEthCyclingOrderIntentProvider when a buy-the-dip cycle finishes
/// (either by a successful rebuy or by being abandoned due to price rising too far).
/// Carries the feature vector captured at the original sell signal so the NN can
/// attribute the outcome to the conditions that triggered the sell.
/// </summary>
public sealed class CycleCompletedEvent
{
    public string   Symbol         { get; init; } = "";
    public bool     IsAbandoned    { get; init; }
    public decimal  NetEthGain     { get; init; }
    public decimal  SellPrice      { get; init; }
    public decimal  BuyPrice       { get; init; }
    public float[]  FeaturesAtSell { get; init; } = [];
    public DateTime SellTimestamp  { get; init; }
    public DateTime CompletedAt    { get; init; }

    /// <summary>
    /// Reward in [-1, 1] for bandit/NN training.
    /// Settled abandons carry real negative NetEthGain and produce negative reward.
    /// Immediate abandon events (NetEthGain=0) produce 0 — the real cost is recognised at settle time.
    /// </summary>
    public float Reward => (float)Math.Tanh((double)NetEthGain * 150.0);
}

/// <summary>
/// Singleton store of recent cycle outcomes with their pre-sell feature vectors.
/// Sprint 2 (NN optimizer) trains on these after each cycle; Sprint 3 (Claude advisor)
/// summarises them for macro reasoning.
/// </summary>
public sealed class PerformanceTracker
{
    private const int MaxHistory      = 30;
    private const int MaxAccumHistory = 50;

    private readonly List<CycleCompletedEvent> _history      = [];
    private readonly List<AccumulationOutcome>  _accumHistory = [];
    private readonly object _lock = new();

    public void Record(CycleCompletedEvent e)
    {
        lock (_lock)
        {
            _history.Insert(0, e);
            if (_history.Count > MaxHistory) _history.RemoveAt(_history.Count - 1);
        }
    }

    public IReadOnlyList<CycleCompletedEvent> GetRecent(int count = 10)
    {
        lock (_lock) return _history.Take(count).ToList();
    }

    public int TotalRecorded
    {
        get { lock (_lock) return _history.Count; }
    }

    /// <summary>Rolling average reward over the last N actionable cycles (excludes zero-NetEthGain abandon stubs).</summary>
    public float RollingReward(int window = 5)
    {
        lock (_lock)
        {
            // Include settled abandons (real negative NetEthGain); exclude immediate abandon stubs (NetEthGain=0)
            var recent = _history.Take(window).Where(e => !e.IsAbandoned || e.NetEthGain != 0m).ToList();
            return recent.Count == 0 ? 0f : recent.Average(e => e.Reward);
        }
    }

    public void RecordAccumulationResult(AccumulationFeedbackEvent e)
    {
        lock (_lock)
        {
            _accumHistory.Insert(0, new AccumulationOutcome(
                e.Trigger, e.BuyPrice, e.SellPrice, e.Reward, DateTime.UtcNow));
            if (_accumHistory.Count > MaxAccumHistory) _accumHistory.RemoveAt(_accumHistory.Count - 1);
        }
    }

    public IReadOnlyList<AccumulationOutcome> GetRecentAccumulationOutcomes(int count = 30)
    {
        lock (_lock) return _accumHistory.Take(count).ToList();
    }

    /// <summary>
    /// Seeds history from persisted cycle records on startup so IsDegrading and
    /// RollingReward reflect real history after a deploy or crash recovery.
    /// Feature vectors are not stored in the DB, so FeaturesAtSell is left empty —
    /// that only affects drift detection, not the reward/degradation logic.
    /// </summary>
    public void Seed(IEnumerable<Services.CycleResult> cycles)
    {
        lock (_lock)
        {
            _history.Clear();
            foreach (var c in cycles.Take(MaxHistory))
            {
                _history.Add(new CycleCompletedEvent
                {
                    Symbol         = c.Symbol,
                    IsAbandoned    = c.IsAbandoned,
                    NetEthGain     = c.NetEthGain,
                    SellPrice      = c.SellPrice,
                    BuyPrice       = c.BuyPrice,
                    FeaturesAtSell = [],
                    SellTimestamp  = c.SellTimestamp,
                    CompletedAt    = c.BuyTimestamp,
                });
            }
        }
    }

    /// <summary>True when recent cycles (including settled abandons) are mostly losing coin.</summary>
    public bool IsDegrading(int window = 5)
    {
        lock (_lock)
        {
            var completed = _history.Where(e => !e.IsAbandoned || e.NetEthGain != 0m).Take(window).ToList();
            return completed.Count >= 3 && completed.Count(e => e.Reward < 0) >= 3;
        }
    }
}
