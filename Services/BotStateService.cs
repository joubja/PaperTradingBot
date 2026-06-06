using PaperTradingBot.AI;
using PaperTradingBot.Models;

namespace PaperTradingBot.Services;

/// <summary>
/// Singleton that holds current bot state and fires events so Blazor
/// components can re-render without polling.
/// </summary>
public class BotStateService
{
    private readonly object _lock = new();
    private readonly DatabaseService _db;

    private string   _lastPhase          = "";
    private string   _lastSummary        = "";
    private DateTime _lastPhaseStartedAt = DateTime.UtcNow;

    public BotStateService(DatabaseService db) => _db = db;

    // ── Status ────────────────────────────────────────────────────────────────
    public bool IsRunning { get; private set; }
    public string ActiveStrategy { get; private set; } = "";
    public string? ActiveSessionId { get; private set; }

    // ── Portfolio snapshot ────────────────────────────────────────────────────
    public decimal Cash { get; private set; }
    public decimal TotalEquity { get; private set; }
    public Dictionary<string, (decimal Quantity, decimal AvgEntry)> Positions { get; private set; } = new();

    // ── Live series (capped) — always replaced by reference, never mutated ───
    public IReadOnlyList<EquityPoint> EquityCurve { get; private set; } = Array.Empty<EquityPoint>();
    public IReadOnlyList<EthQuantityPoint> EthQuantityCurve { get; private set; } = Array.Empty<EthQuantityPoint>();
    public IReadOnlyList<Candle> RecentCandles { get; private set; } = Array.Empty<Candle>();
    public List<Trade> RecentTrades { get; private set; } = new();

    private readonly List<Candle> _completedMinuteCandles = new();
    private Candle? _liveMinuteCandle;
    private DateTime _liveMinuteStart = DateTime.MinValue;

    // ── Session stats ─────────────────────────────────────────────────────────
    public decimal SessionCommission { get; private set; }
    public decimal StartingEth { get; private set; }
    public DateTime SessionStartedAt { get; private set; } = DateTime.MinValue;
    public string PrimarySymbol { get; private set; } = "ETHUSDT";

    // ── Cycling ───────────────────────────────────────────────────────────────
    public bool CyclingEnabled { get; private set; } = true;
    public List<CycleResult> RecentCycles { get; private set; } = new();

    // ── Strategy status (live state machine readout) ──────────────────────────
    public StrategyStatusInfo? StrategyStatus { get; private set; }

    // ── Log ───────────────────────────────────────────────────────────────────
    public IReadOnlyList<string> LogMessages { get; private set; } = Array.Empty<string>();

    // ── Events ────────────────────────────────────────────────────────────────
    public event Action? OnStateChanged;

    /// <summary>Fired when a buy-the-dip cycle completes or is abandoned. Consumed by the AI optimizer.</summary>
    public event Action<CycleCompletedEvent>? OnCycleCompleted;

    public void NotifyCycleCompleted(CycleCompletedEvent e) => OnCycleCompleted?.Invoke(e);

    // ── Bot lifecycle ─────────────────────────────────────────────────────────

    public void NotifyStarted(string strategy, string sessionId, decimal startingCash, decimal startingEth = 0m, string symbol = "ETHUSDT")
    {
        lock (_lock)
        {
            IsRunning           = true;
            ActiveStrategy      = strategy;
            ActiveSessionId     = sessionId;
            PrimarySymbol       = symbol;
            _lastPhase          = "";
            _lastSummary        = "";
            _lastPhaseStartedAt = DateTime.UtcNow;
            SessionStartedAt = DateTime.UtcNow;
            Cash             = startingCash;
            TotalEquity      = startingCash;
            Positions        = startingEth > 0m
                ? new() { [symbol] = (startingEth, 0m) }
                : new();
            EquityCurve      = Array.Empty<EquityPoint>();
            EthQuantityCurve = Array.Empty<EthQuantityPoint>();
            _completedMinuteCandles.Clear();
            _liveMinuteCandle = null;
            _liveMinuteStart  = DateTime.MinValue;
            RecentCandles     = Array.Empty<Candle>();
            RecentTrades      = new();
            RecentCycles      = new();
            LogMessages       = Array.Empty<string>();
            CyclingEnabled    = true;
            SessionCommission = 0m;
            StartingEth       = startingEth;
            StrategyStatus    = null;
        }
        OnStateChanged?.Invoke();
    }

    public void NotifyStopped()
    {
        lock (_lock) { IsRunning = false; }
        OnStateChanged?.Invoke();
    }

    public void NotifyBar(Candle candle)
    {
        lock (_lock)
        {
            var ts = candle.Timestamp.Kind == DateTimeKind.Utc
                ? candle.Timestamp
                : candle.Timestamp.ToUniversalTime();
            var minuteStart = new DateTime(ts.Year, ts.Month, ts.Day, ts.Hour, ts.Minute, 0, DateTimeKind.Utc);

            if (minuteStart != _liveMinuteStart)
            {
                if (_liveMinuteCandle != null)
                {
                    _completedMinuteCandles.Add(_liveMinuteCandle);
                    if (_completedMinuteCandles.Count > 79)
                        _completedMinuteCandles.RemoveAt(0);
                }
                _liveMinuteStart   = minuteStart;
                _liveMinuteCandle  = new Candle { Timestamp = minuteStart, Open = candle.Open, High = candle.High, Low = candle.Low, Close = candle.Close };
            }
            else
            {
                _liveMinuteCandle = new Candle
                {
                    Timestamp = _liveMinuteCandle!.Timestamp,
                    Open      = _liveMinuteCandle.Open,
                    High      = Math.Max(_liveMinuteCandle.High, candle.High),
                    Low       = Math.Min(_liveMinuteCandle.Low,  candle.Low),
                    Close     = candle.Close
                };
            }

            var display = new List<Candle>(_completedMinuteCandles);
            if (_liveMinuteCandle != null) display.Add(_liveMinuteCandle);
            RecentCandles = display;
        }
        // OnStateChanged fires via the subsequent NotifyBarUpdate call
    }

    // ── Per-bar update — single atomic call replaces both curves and portfolio ─

    public void NotifyBarUpdate(
        EquityPoint equityPoint,
        decimal ethQty,
        decimal cash,
        decimal equity,
        Dictionary<string, (decimal Qty, decimal AvgEntry)> positions)
    {
        lock (_lock)
        {
            var eq = new List<EquityPoint>(EquityCurve) { equityPoint };
            if (eq.Count > 600) eq.RemoveAt(0);
            EquityCurve = eq;

            var eth = new List<EthQuantityPoint>(EthQuantityCurve)
            {
                new() { Timestamp = equityPoint.Timestamp, Quantity = ethQty }
            };
            if (eth.Count > 600) eth.RemoveAt(0);
            EthQuantityCurve = eth;

            Cash        = cash;
            TotalEquity = equity;
            Positions   = positions;
        }
        OnStateChanged?.Invoke();
    }

    public DateTime LastTradeAt { get; private set; } = DateTime.MinValue;

    public void NotifyTrade(Trade trade)
    {
        lock (_lock)
        {
            RecentTrades.Insert(0, trade);
            if (RecentTrades.Count > 50) RecentTrades.RemoveAt(50);
            SessionCommission += trade.Commission;
            LastTradeAt        = trade.Timestamp;
        }
        OnStateChanged?.Invoke();
    }

    public void NotifyStrategyStatus(StrategyStatusInfo status)
    {
        string? sessionId = null;
        string prevPhase = "", prevSummary = "";
        int durationSec = 0;

        lock (_lock)
        {
            if (status.Phase != _lastPhase && !string.IsNullOrEmpty(_lastPhase))
            {
                sessionId   = ActiveSessionId;
                prevPhase   = _lastPhase;
                prevSummary = _lastSummary;
                durationSec = (int)(DateTime.UtcNow - _lastPhaseStartedAt).TotalSeconds;
            }
            if (status.Phase != _lastPhase)
            {
                _lastPhase          = status.Phase;
                _lastSummary        = status.Summary;
                _lastPhaseStartedAt = DateTime.UtcNow;
            }
            StrategyStatus = status;
        }

        if (sessionId != null)
            _db.InsertBotEvent(sessionId, prevPhase, prevSummary, durationSec);

        OnStateChanged?.Invoke();
    }

    public void NotifyCyclingUpdate(bool enabled, List<CycleResult> cycles)
    {
        lock (_lock)
        {
            CyclingEnabled = enabled;
            RecentCycles   = cycles;
        }
        OnStateChanged?.Invoke();
    }

    public void NotifyLog(string message)
    {
        lock (_lock)
        {
            var next = new string[Math.Min(LogMessages.Count + 1, 25)];
            next[0] = $"{DateTime.Now:HH:mm:ss} {message}";
            for (var i = 1; i < next.Length; i++) next[i] = LogMessages[i - 1];
            LogMessages = next;
        }
        OnStateChanged?.Invoke();
    }
}

public record StrategyStatusInfo(
    string Phase,            // WarmingUp | ActiveSell | Watching | CyclingSuspended
    string Summary,
    decimal? SellPrice        = null,
    decimal? SellQty          = null,
    decimal? CurrentDropPct   = null,
    decimal? MinDropPct       = null,
    decimal? AdaptiveSellPct  = null,
    decimal? AdaptiveAbandonPct = null
);

public class EthQuantityPoint
{
    public DateTime Timestamp { get; set; }
    public decimal Quantity { get; set; }
}
