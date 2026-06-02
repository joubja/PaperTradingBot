namespace PaperTradingBot.Services;

public sealed record AdjustmentRecord(
    DateTime Timestamp,
    string   Source,    // "Bandit" | "Claude"
    string   Changes,   // e.g. "RsiDipBuy→41.0  SellPct→39%"
    float    Reward,
    float    RollingReward,
    string?  Reasoning  // Claude only
);

/// <summary>
/// Singleton that accumulates optimizer adjustment history and broadcasts
/// changes to the Blazor UI via OnChanged.
/// </summary>
public sealed class OptimizerStateService
{
    private const int MaxHistory = 25;

    private readonly List<AdjustmentRecord> _history = [];
    private readonly object _lock = new();

    public event Action? OnChanged;

    public bool      IsPaused            { get; private set; }
    public bool      InsufficientCredits { get; private set; }
    public DateTime  LastAttemptAt       { get; private set; } = DateTime.MinValue;
    public string?   LastClaudeReasoning { get; private set; }
    public DateTime  LastClaudeCallAt    { get; private set; } = DateTime.MinValue;

    /// <summary>
    /// Pause or resume the optimizer. When paused both the NN and Claude advisor
    /// continue to train/reason but do not apply setting changes.
    /// </summary>
    public void SetPaused(bool paused)
    {
        IsPaused = paused;
        OnChanged?.Invoke();
    }

    public void RecordCreditFailure(DateTime at)
    {
        LastAttemptAt       = at;
        InsufficientCredits = true;
        OnChanged?.Invoke();
    }

    public void ClearCreditFailure()
    {
        InsufficientCredits = false;
        OnChanged?.Invoke();
    }

    public void RecordBanditAdjustment(string changes, float reward, float rollingReward)
    {
        lock (_lock)
        {
            _history.Insert(0, new AdjustmentRecord(
                DateTime.UtcNow, "Bandit", changes, reward, rollingReward, null));
            if (_history.Count > MaxHistory) _history.RemoveAt(_history.Count - 1);
        }
        OnChanged?.Invoke();
    }

    public void RecordClaudeCall(string reasoning, string changes, float rollingReward)
    {
        lock (_lock)
        {
            LastClaudeReasoning = reasoning;
            LastClaudeCallAt    = DateTime.UtcNow;
            LastAttemptAt       = DateTime.UtcNow;
            InsufficientCredits = false;

            if (!string.IsNullOrWhiteSpace(changes))
            {
                _history.Insert(0, new AdjustmentRecord(
                    DateTime.UtcNow, "Claude", changes, 0f, rollingReward, reasoning));
                if (_history.Count > MaxHistory) _history.RemoveAt(_history.Count - 1);
            }
        }
        OnChanged?.Invoke();
    }

    public IReadOnlyList<AdjustmentRecord> GetHistory()
    {
        lock (_lock) return _history.ToList();
    }
}
