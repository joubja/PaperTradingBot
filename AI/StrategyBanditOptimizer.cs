using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PaperTradingBot.Config;
using PaperTradingBot.Services;

namespace PaperTradingBot.AI;

/// <summary>
/// Replaces the neural-network optimizer with per-setting UCB1 bandits.
///
/// Each of the 8 tunable settings has its own bandit managing 5 discrete candidate values.
/// After every completed cycle all bandits record the cycle reward, then UCB selects the
/// next arm (setting value) to try. For accumulation lots (RSI dip, EMA cross), only the
/// RsiDipBuy and RsiCrossoverMax bandits update — those are the only settings causally
/// linked to accumulation entry quality.
///
/// Why this is better than the NN:
///   - UCB has an explicit exploration strategy; NN amplification was direction-blind.
///   - Each bandit independently tracks its setting's reward history.
///   - Fully interpretable: the arm stats show exactly what has been tried and what worked.
///   - Converges to the best discrete arm; NN had no convergence guarantee.
/// </summary>
public sealed class StrategyBanditOptimizer : IHostedService
{
    // ── Candidate arms: 5 values per setting, centred on the default ──────────
    private static readonly decimal[] RsiDipBuyArms       = [33m, 37m, 40m, 43m, 47m];
    private static readonly decimal[] RsiCrossoverMaxArms = [52m, 56m, 60m, 64m, 68m];
    private static readonly decimal[] RsiCycleSellArms    = [55m, 61m, 66m, 72m, 79m];
    private static readonly decimal[] RsiCycleRebuyArms   = [37m, 42m, 47m, 52m, 57m];
    private static readonly decimal[] DefaultSellPctArms  = [0.28m, 0.34m, 0.40m, 0.46m, 0.52m];
    private static readonly decimal[] MinAbandonRiseArms  = [0.009m, 0.012m, 0.015m, 0.019m, 0.023m];
    private static readonly decimal[] MaxAbandonRiseArms  = [0.028m, 0.036m, 0.045m, 0.057m, 0.070m];
    private static readonly decimal[] CooldownBarsArms    = [5m, 7m, 10m, 13m, 17m];

    private readonly SettingBandit _dipBuy;
    private readonly SettingBandit _crossMax;
    private readonly SettingBandit _cycleSell;
    private readonly SettingBandit _cycleRebuy;
    private readonly SettingBandit _sellPct;
    private readonly SettingBandit _minAbandon;
    private readonly SettingBandit _maxAbandon;
    private readonly SettingBandit _cooldown;

    private SettingBandit[] AllBandits =>
        [_dipBuy, _crossMax, _cycleSell, _cycleRebuy, _sellPct, _minAbandon, _maxAbandon, _cooldown];

    private readonly LiveSettingsService   _settings;
    private readonly BotStateService       _botState;
    private readonly PerformanceTracker    _perf;
    private readonly OptimizerStateService _optimizerState;
    private readonly AccumulationTracker   _accumTracker;
    private readonly OptimizerOptions      _options;
    private readonly ILogger<StrategyBanditOptimizer> _logger;

    public StrategyBanditOptimizer(
        LiveSettingsService   settings,
        BotStateService       botState,
        PerformanceTracker    perf,
        OptimizerStateService optimizerState,
        AccumulationTracker   accumTracker,
        IOptions<OptimizerOptions> options,
        ILogger<StrategyBanditOptimizer> logger)
    {
        _settings       = settings;
        _botState       = botState;
        _perf           = perf;
        _optimizerState = optimizerState;
        _accumTracker   = accumTracker;
        _options        = options.Value;
        _logger         = logger;

        var c = _options.ExplorationC;
        _dipBuy     = new SettingBandit("RsiDipBuy",       RsiDipBuyArms,       c);
        _crossMax   = new SettingBandit("RsiCrossoverMax", RsiCrossoverMaxArms, c);
        _cycleSell  = new SettingBandit("RsiCycleSell",    RsiCycleSellArms,    c);
        _cycleRebuy = new SettingBandit("RsiCycleRebuy",   RsiCycleRebuyArms,   c);
        _sellPct    = new SettingBandit("DefaultSellPct",  DefaultSellPctArms,  c);
        _minAbandon = new SettingBandit("MinAbandonRise",  MinAbandonRiseArms,  c);
        _maxAbandon = new SettingBandit("MaxAbandonRise",  MaxAbandonRiseArms,  c);
        _cooldown   = new SettingBandit("CooldownBars",    CooldownBarsArms,    c);
    }

    public Task StartAsync(CancellationToken ct)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("BANDIT | Disabled via config.");
            return Task.CompletedTask;
        }

        var stateLoaded    = TryLoadState();
        var settingsLoaded = _settings.TryLoad(_options.SettingsPersistPath);

        _botState.OnCycleCompleted += HandleCycleCompleted;
        _accumTracker.OnFeedback   += HandleAccumulationFeedback;

        _logger.LogInformation(
            "BANDIT | Started. State {S}. Settings {L}. ExplorationC={C}",
            stateLoaded    ? $"loaded from {_options.BanditPersistPath}"   : "fresh",
            settingsLoaded ? $"loaded from {_options.SettingsPersistPath}" : "defaults",
            _options.ExplorationC);

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct)
    {
        _botState.OnCycleCompleted -= HandleCycleCompleted;
        _accumTracker.OnFeedback   -= HandleAccumulationFeedback;
        if (_options.Enabled) TrySaveState();
        return Task.CompletedTask;
    }

    // ── Public status for dashboard / Claude context ──────────────────────────

    public IReadOnlyList<BanditArmStatus> GetArmStatus()
        => AllBandits.Select(b => new BanditArmStatus(
            b.Name, b.CurrentValue, b.ActiveArm, b.TotalPulls,
            Enumerable.Range(0, b.ArmCount)
                .Select(i => (b.GetArm(i), b.GetPulls(i), b.GetMeanReward(i)))
                .ToArray()
        )).ToList();

    // ── Cycle handler ─────────────────────────────────────────────────────────

    private void HandleCycleCompleted(CycleCompletedEvent e)
    {
        if (!_options.Enabled) return;

        // Only settings causally linked to cycle outcome get cycle reward
        _cycleSell.RecordReward(e.Reward);
        _cycleRebuy.RecordReward(e.Reward);
        _sellPct.RecordReward(e.Reward);
        _cooldown.RecordReward(e.Reward);
        // Abandon settings only updated when cycle was actually abandoned
        if (e.IsAbandoned)
        {
            _minAbandon.RecordReward(e.Reward);
            _maxAbandon.RecordReward(e.Reward);
        }
        // _dipBuy, _crossMax: causal reward only from accumulation feedback

        if (_botState.StrategyStatus?.Phase == "ActiveSell") { TrySaveState(); return; }

        if (_optimizerState.IsPaused)
        {
            _logger.LogDebug("BANDIT | Paused — reward recorded, skipping apply.");
            TrySaveState();
            return;
        }

        _cycleSell.SelectNext();
        _cycleRebuy.SelectNext();
        _sellPct.SelectNext();
        _cooldown.SelectNext();
        if (e.IsAbandoned)
        {
            _minAbandon.SelectNext();
            _maxAbandon.SelectNext();
        }
        ApplyAll(e.Reward);
        TrySaveState();

        if (_dipBuy.TotalPulls % 5 == 0 && _dipBuy.TotalPulls > 0)
            _logger.LogInformation(
                "BANDIT | Arm snapshot after {N} pulls: RsiDipBuy={D}  RsiCycleSell={S}  SellPct={P}  Cooldown={C}",
                _dipBuy.TotalPulls, _dipBuy.Describe(), _cycleSell.Describe(),
                _sellPct.Describe(), _cooldown.Describe());
    }

    // ── Accumulation feedback ─────────────────────────────────────────────────

    private void HandleAccumulationFeedback(AccumulationFeedbackEvent e)
    {
        if (!_options.Enabled) return;

        _perf.RecordAccumulationResult(e);

        // Only RsiDipBuy and RsiCrossoverMax are causally linked to accumulation quality
        _dipBuy.RecordReward(e.Reward);
        _crossMax.RecordReward(e.Reward);

        if (_optimizerState.IsPaused || _botState.StrategyStatus?.Phase == "ActiveSell")
        {
            TrySaveState();
            return;
        }

        _dipBuy.SelectNext();
        _crossMax.SelectNext();
        ApplyAccumulation(e);
        TrySaveState();
    }

    // ── Apply helpers ─────────────────────────────────────────────────────────

    private void ApplyAll(float reward)
    {
        var dipBuy      = _dipBuy.CurrentValue;
        var crossMax    = _crossMax.CurrentValue;
        var cycleSell   = _cycleSell.CurrentValue;
        var cycleRebuy  = _cycleRebuy.CurrentValue;
        var sellPct     = _sellPct.CurrentValue;
        var minAbandon  = _minAbandon.CurrentValue;
        var maxAbandon  = _maxAbandon.CurrentValue;
        var cooldown    = (int)Math.Round((double)_cooldown.CurrentValue);

        // Enforce cross-invariants before applying
        cycleSell  = Math.Max(cycleSell, crossMax + 5m);
        dipBuy     = Math.Min(dipBuy,    cycleSell - 15m);
        maxAbandon = Math.Max(maxAbandon, minAbandon + 0.01m);

        var changed = new List<string>();
        if (Math.Abs(dipBuy     - _settings.RsiDipBuy)        >= 0.5m) { _settings.RsiDipBuy        = dipBuy;     changed.Add($"RsiDipBuy→{dipBuy:F1}"); }
        if (Math.Abs(crossMax   - _settings.RsiCrossoverMax)  >= 0.5m) { _settings.RsiCrossoverMax  = crossMax;   changed.Add($"RsiCrossMax→{crossMax:F1}"); }
        if (Math.Abs(cycleSell  - _settings.RsiCycleSell)     >= 0.5m) { _settings.RsiCycleSell     = cycleSell;  changed.Add($"RsiCycleSell→{cycleSell:F1}"); }
        if (Math.Abs(cycleRebuy - _settings.RsiCycleRebuy)    >= 0.5m) { _settings.RsiCycleRebuy    = cycleRebuy; changed.Add($"RsiCycleRebuy→{cycleRebuy:F1}"); }
        if (Math.Abs(sellPct    - _settings.DefaultSellPct)   >= 0.01m){ _settings.DefaultSellPct   = sellPct;    changed.Add($"SellPct→{sellPct:P0}"); }
        if (Math.Abs(minAbandon - _settings.MinAbandonRise)   >= 0.001m){ _settings.MinAbandonRise  = minAbandon; changed.Add($"MinAbandon→{minAbandon:P2}"); }
        if (Math.Abs(maxAbandon - _settings.MaxAbandonRise)   >= 0.001m){ _settings.MaxAbandonRise  = maxAbandon; changed.Add($"MaxAbandon→{maxAbandon:P2}"); }
        if (cooldown != _settings.CycleCooldownBars)                    { _settings.CycleCooldownBars = cooldown;  changed.Add($"Cooldown→{cooldown}bars"); }

        if (changed.Count > 0)
        {
            var changesStr = string.Join("  ", changed);
            _logger.LogInformation(
                "BANDIT | {Count} change(s) | Reward={R:+0.000;-0.000} Rolling={Roll:+0.000;-0.000} | {Changes}",
                changed.Count, reward, _perf.RollingReward(), changesStr);
            _optimizerState.RecordBanditAdjustment(changesStr, reward, _perf.RollingReward());
            _settings.Save();
        }
        else
        {
            _logger.LogDebug("BANDIT | Reward={R:F3} — arm unchanged or invariant-pinned, no apply.", reward);
        }
    }

    private void ApplyAccumulation(AccumulationFeedbackEvent e)
    {
        var dipBuy   = Math.Min(_dipBuy.CurrentValue, _settings.RsiCycleSell - 15m);
        var crossMax = _crossMax.CurrentValue;

        var changed = new List<string>();
        if (Math.Abs(dipBuy   - _settings.RsiDipBuy)       >= 0.5m) { _settings.RsiDipBuy      = dipBuy;   changed.Add($"RsiDipBuy→{dipBuy:F1}"); }
        if (Math.Abs(crossMax - _settings.RsiCrossoverMax) >= 0.5m) { _settings.RsiCrossoverMax = crossMax; changed.Add($"RsiCrossMax→{crossMax:F1}"); }

        if (changed.Count > 0)
        {
            var changesStr = string.Join("  ", changed);
            _logger.LogInformation(
                "BANDIT | [accum:{T}] Reward={R:+0.000;-0.000} | {Changes}",
                e.Trigger, e.Reward, changesStr);
            _optimizerState.RecordBanditAdjustment($"[accum:{e.Trigger}] {changesStr}", e.Reward, _perf.RollingReward());
            _settings.Save();
        }
    }

    // ── Persistence ───────────────────────────────────────────────────────────

    private bool TryLoadState()
    {
        try
        {
            if (!File.Exists(_options.BanditPersistPath)) return false;
            var json   = File.ReadAllText(_options.BanditPersistPath);
            var states = JsonSerializer.Deserialize<Dictionary<string, BanditState>>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (states is null) return false;
            foreach (var b in AllBandits)
                if (states.TryGetValue(b.Name, out var s)) b.ApplyState(s);
            return true;
        }
        catch { return false; }
    }

    private void TrySaveState()
    {
        try
        {
            var dir = Path.GetDirectoryName(_options.BanditPersistPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var states = AllBandits.ToDictionary(b => b.Name, b => b.ToState());
            File.WriteAllText(_options.BanditPersistPath,
                JsonSerializer.Serialize(states, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "BANDIT | Failed to save state to {Path}", _options.BanditPersistPath);
        }
    }
}
