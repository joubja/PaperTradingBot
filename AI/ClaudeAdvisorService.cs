using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PaperTradingBot.Config;
using PaperTradingBot.Services;

namespace PaperTradingBot.AI;

/// <summary>
/// Hosted service that periodically calls Claude for macro-level strategy advice,
/// and also triggers on performance degradation.
///
/// Claude responds with a JSON object containing optional setting adjustments and
/// plain-English reasoning. Adjustments are validated against safe ranges and
/// max-delta-per-call limits before being applied to LiveSettingsService.
///
/// Triggers:
///   1. Scheduled — every IntervalMinutes (default 30).
///   2. Degradation — when PerformanceTracker.IsDegrading() is true after any cycle,
///      subject to a 5-minute debounce to prevent hammering the API.
///
/// Safety: never applies adjustments when StrategyStatus.Phase == "ActiveSell".
/// </summary>
public sealed class ClaudeAdvisorService : IHostedService, IDisposable
{
    private readonly ClaudeApiClient        _client;
    private readonly LiveSettingsService    _settings;
    private readonly BotStateService        _botState;
    private readonly PerformanceTracker     _perf;
    private readonly OptimizerStateService  _optimizerState;
    private readonly AdvisorContextBuilder  _contextBuilder;
    private readonly FeatureDriftDetector   _driftDetector;
    private readonly DatabaseService        _db;
    private readonly ClaudeAdvisorOptions   _options;
    private readonly OptimizerOptions       _optimizerOptions;
    private readonly ILogger<ClaudeAdvisorService> _logger;

    private Timer?           _timer;
    private DateTime         _lastCallAt = DateTime.MinValue;
    private readonly SemaphoreSlim _sem  = new(1, 1);

    // Max delta Claude may move any setting in one call (separate from clamped absolute range)
    private const decimal RsiMaxDelta = 3m;
    private const decimal PctMaxDelta = 0.05m;
    private const int     IntMaxDelta = 3;
    private const int     DebounceMins = 5;

    private const string SystemPrompt = """
        You are an ETH accumulation optimizer for an automated ETH/USDT trading bot.

        PRIMARY GOAL: Maximize ETH accumulated per hour (ETH/hr). Every decision must be
        evaluated by asking "does this setting change lead to more ETH at the end of the day?"
        Never optimize for USD profit. ETH gain is the only metric that matters.

        HOW THE BOT ACCUMULATES ETH — TWO MECHANISMS:

        1. BASE ACCUMULATION (passive, runs continuously)
           Buys ETH with available cash whenever:
           - RSI drops below RsiDipBuy (RSI dip signal)
           - OR a bullish EMA crossover occurs AND RSI < RsiCrossoverMax (EMA cross signal)
           These buys grow the ETH position steadily. Profitable when price rises after buy.

        2. CYCLING OVERLAY (active, periodic)
           When RSI spikes above RsiCycleSell AND is declining:
           - SELLS a fraction of ETH (DefaultSellPct) for USDT cash
           - Waits for price to DROP into the MinAbandonRise–MaxAbandonRise band
           - REBUYS all cash for ETH → net MORE ETH than was sold (profit from the spread)
           - If price RISES instead, the cycle is ABANDONED: no trade, no ETH change
           A completed cycle = ETH gain. An abandoned cycle = neutral (opportunity cost only).

        WHAT EACH SETTING CONTROLS:
        - RsiDipBuy: RSI threshold for dip accumulation buys. Lower = stricter entries, fewer buys.
        - RsiCrossoverMax: RSI ceiling for EMA cross buys. Lower = avoids buying into overheated price.
        - RsiCycleSell: RSI level that triggers the sell. Higher = fewer cycles, requires stronger spike.
        - RsiCycleRebuy: RSI level that forces rebuy even without full dip. Lower = more patient waiting.
        - DefaultSellPct: fraction of ETH sold per cycle. Higher = more ETH at stake per cycle.
        - MinAbandonRise: price rise needed before cycle is abandoned. Lower = bails out earlier.
        - MaxAbandonRise: maximum rise before forced rebuy. Higher = more patient, waits for bigger dip.
        - CycleCooldownBars: bars between cycles. More = fewer cycles, avoids rapid churning.

        REASONING FRAMEWORK:
        - Cycling win rate LOW → raise RsiCycleSell (need stronger RSI spike before selling)
        - Cycling abandon rate HIGH → widen abandon band (MaxAbandonRise up) or lower RsiCycleRebuy
        - RSI dip buy win rate LOW → lower RsiDipBuy (stricter entry, avoid buying into further decline)
        - EMA cross win rate LOW → lower RsiCrossoverMax (avoid buying into already-overbought price)
        - ETH/hr is positive and growing → do NOT change things; stability is valuable
        - ETH/hr near zero or negative → identify which mechanism is broken and adjust that one

        Respond with ONLY a valid JSON object — no preamble, no markdown, just the JSON:
        {
          "reasoning": "2-3 sentences: which mechanism is underperforming, what you're changing, and why it should improve ETH/hr",
          "adjustments": {
            "RsiDipBuy": <decimal or null>,
            "RsiCrossoverMax": <decimal or null>,
            "RsiCycleSell": <decimal or null>,
            "RsiCycleRebuy": <decimal or null>,
            "DefaultSellPct": <0.0-1.0 decimal or null>,
            "MinAbandonRise": <0.0-1.0 decimal or null>,
            "MaxAbandonRise": <0.0-1.0 decimal or null>,
            "CycleCooldownBars": <integer or null>
          }
        }

        Rules:
        - Omit or null any field you do not want to change.
        - If ETH/hr is positive and performance looks acceptable, recommend no changes (all nulls).
        - Keep changes small: RSI values ±3, fractions ±0.05, cooldown ±3.
        - RsiCycleSell must remain > RsiCrossoverMax + 5.
        - RsiDipBuy must remain < RsiCycleSell - 15.
        - MaxAbandonRise must remain > MinAbandonRise + 0.01.
        """;

    public ClaudeAdvisorService(
        ClaudeApiClient client,
        LiveSettingsService settings,
        BotStateService botState,
        PerformanceTracker perf,
        OptimizerStateService optimizerState,
        AdvisorContextBuilder contextBuilder,
        FeatureDriftDetector driftDetector,
        DatabaseService db,
        IOptions<ClaudeAdvisorOptions> options,
        IOptions<OptimizerOptions> optimizerOptions,
        ILogger<ClaudeAdvisorService> logger)
    {
        _client           = client;
        _settings         = settings;
        _botState         = botState;
        _perf             = perf;
        _optimizerState   = optimizerState;
        _contextBuilder   = contextBuilder;
        _driftDetector    = driftDetector;
        _db               = db;
        _options          = options.Value;
        _optimizerOptions = optimizerOptions.Value;
        _logger           = logger;
    }

    public Task StartAsync(CancellationToken ct)
    {
        if (!_options.Enabled || string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            _logger.LogInformation("CLAUDE ADVISOR | Disabled (Enabled={E}, ApiKey={K}).",
                _options.Enabled, string.IsNullOrWhiteSpace(_options.ApiKey) ? "not set" : "set");
            return Task.CompletedTask;
        }

        _botState.OnCycleCompleted += OnCycleCompleted;
        var interval = TimeSpan.FromMinutes(_options.IntervalMinutes);
        _timer = new Timer(_ => FireAsync("scheduled"), null, interval, interval);

        _logger.LogInformation("CLAUDE ADVISOR | Started. Interval={Min}min Model={M}.",
            _options.IntervalMinutes, _options.Model);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct)
    {
        _timer?.Change(Timeout.Infinite, 0);
        _botState.OnCycleCompleted -= OnCycleCompleted;
        return Task.CompletedTask;
    }

    public void Dispose() => _timer?.Dispose();

    // ── Triggers ──────────────────────────────────────────────────────────────

    private void OnCycleCompleted(CycleCompletedEvent e)
    {
        // Keep drift detector updated with the winning-regime feature distribution
        if (!e.IsAbandoned && e.NetEthGain > 0)
            _driftDetector.UpdateWinning(e.FeaturesAtSell);

        var debounced = DateTime.UtcNow - _lastCallAt >= TimeSpan.FromMinutes(DebounceMins);
        if (!debounced) return;

        if (_perf.IsDegrading())
        {
            FireAsync("degradation");
            return;
        }

        var drift = _driftDetector.DriftScore(e.FeaturesAtSell);
        if (_driftDetector.IsDrifting(e.FeaturesAtSell, _optimizerOptions.DriftThreshold))
        {
            _logger.LogInformation(
                "CLAUDE ADVISOR | Regime shift detected (drift={D:F3} > {T:F3}) — triggering unscheduled call.",
                drift, _optimizerOptions.DriftThreshold);
            FireAsync("regime-shift");
        }
    }

    private void FireAsync(string trigger)
    {
        _ = Task.Run(async () =>
        {
            if (!await _sem.WaitAsync(0)) return;
            try { await CallClaudeAsync(trigger); }
            finally { _sem.Release(); }
        });
    }

    // ── Core call ─────────────────────────────────────────────────────────────

    private async Task CallClaudeAsync(string trigger)
    {
        if (trigger == "scheduled" && !_botState.IsRunning) return;
        if (_optimizerState.InsufficientCredits) return;

        _lastCallAt = DateTime.UtcNow;
        _logger.LogInformation("CLAUDE ADVISOR | [{Trigger}] Calling API ...", trigger);

        var result = await _client.SendAsync(
            _options.ApiKey, _options.Model, SystemPrompt,
            BuildPrompt(trigger), _options.MaxTokens,
            CancellationToken.None);

        if (result.InsufficientCredits)
        {
            _logger.LogWarning("CLAUDE ADVISOR | Suspended — insufficient API credits. Top up at console.anthropic.com then press Retry on the dashboard.");
            _optimizerState.RecordCreditFailure(_lastCallAt);
            return;
        }

        if (result.Content is null) return;
        ParseAndApply(result.Content, trigger);
    }

    // ── Prompt builder ────────────────────────────────────────────────────────

    private string BuildPrompt(string trigger) => _contextBuilder.Build(trigger);

    // ── Parse and apply ───────────────────────────────────────────────────────

    private static readonly Regex JsonBlockRx = new(@"\{[\s\S]*\}", RegexOptions.Compiled);

    private void ParseAndApply(string response, string trigger)
    {
        var match = JsonBlockRx.Match(response);
        if (!match.Success)
        {
            _logger.LogWarning("CLAUDE ADVISOR | No JSON in response: {R}", response[..Math.Min(200, response.Length)]);
            return;
        }

        AdvisorResponse? dto;
        try
        {
            dto = JsonSerializer.Deserialize<AdvisorResponse>(match.Value,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "CLAUDE ADVISOR | JSON parse failed.");
            return;
        }

        if (dto is null)
        {
            _logger.LogWarning("CLAUDE ADVISOR | Null response DTO.");
            return;
        }

        _logger.LogInformation("CLAUDE ADVISOR | Reasoning: {R}", dto.Reasoning ?? "(none)");

        if (dto.Adjustments?.ValueKind != JsonValueKind.Object)
        {
            _logger.LogInformation("CLAUDE ADVISOR | No adjustments.");
            RecordRun(trigger, dto.Reasoning ?? "", "");
            return;
        }

        if (_botState.StrategyStatus?.Phase == "ActiveSell")
        {
            _logger.LogDebug("CLAUDE ADVISOR | Skipping apply — ActiveSell in progress.");
            RecordRun(trigger, dto.Reasoning ?? "", "skipped (ActiveSell)");
            return;
        }

        if (_optimizerState.IsPaused)
        {
            _logger.LogInformation("CLAUDE ADVISOR | Paused — reasoning logged, no settings applied. {R}", dto.Reasoning);
            _optimizerState.RecordClaudeCall(dto.Reasoning ?? "", "", _perf.RollingReward());
            RecordRun(trigger, dto.Reasoning ?? "", "paused");
            return;
        }

        var adj = dto.Adjustments.Value
            .EnumerateObject()
            .ToDictionary(p => p.Name, p => p.Value, StringComparer.OrdinalIgnoreCase);

        var changed = new List<string>();
        ApplyAll(adj, changed);

        var changesStr = string.Join("  ", changed);
        if (changed.Count > 0)
            _logger.LogInformation(
                "CLAUDE ADVISOR | [{Trigger}] {Count} adjustment(s): {Changes}",
                trigger, changed.Count, changesStr);
        else
            _logger.LogInformation("CLAUDE ADVISOR | [{Trigger}] No significant changes applied.", trigger);

        RecordRun(trigger, dto.Reasoning ?? "", changesStr);
        _optimizerState.RecordClaudeCall(dto.Reasoning ?? "", changesStr, _perf.RollingReward());
        if (changed.Count > 0) _settings.Save();
    }

    private void RecordRun(string trigger, string reasoning, string changes)
    {
        var sessionId = _botState.ActiveSessionId;
        if (sessionId != null)
            _db.InsertAdvisorRun(sessionId, trigger, reasoning, changes);
    }

    // ── Apply helpers ─────────────────────────────────────────────────────────

    private void ApplyAll(Dictionary<string, JsonElement> adj, List<string> changed)
    {
        if (TryGetDecimal(adj, "RsiDipBuy", out var v))
        {
            var next = Clamp(v, _settings.RsiDipBuy, RsiMaxDelta, 30m, 50m);
            if (Math.Abs(next - _settings.RsiDipBuy) >= 0.1m)
                { _settings.RsiDipBuy = next; changed.Add($"RsiDipBuy→{next:F1}"); }
        }

        if (TryGetDecimal(adj, "RsiCrossoverMax", out v))
        {
            var next = Clamp(v, _settings.RsiCrossoverMax, RsiMaxDelta, 50m, 68m);
            if (Math.Abs(next - _settings.RsiCrossoverMax) >= 0.1m)
                { _settings.RsiCrossoverMax = next; changed.Add($"RsiCrossMax→{next:F1}"); }
        }

        if (TryGetDecimal(adj, "RsiCycleSell", out v))
        {
            var next = Clamp(v, _settings.RsiCycleSell, RsiMaxDelta, 65m, 82m);
            if (Math.Abs(next - _settings.RsiCycleSell) >= 0.1m)
                { _settings.RsiCycleSell = next; changed.Add($"RsiCycleSell→{next:F1}"); }
        }

        if (TryGetDecimal(adj, "RsiCycleRebuy", out v))
        {
            var next = Clamp(v, _settings.RsiCycleRebuy, RsiMaxDelta, 35m, 55m);
            if (Math.Abs(next - _settings.RsiCycleRebuy) >= 0.1m)
                { _settings.RsiCycleRebuy = next; changed.Add($"RsiCycleRebuy→{next:F1}"); }
        }

        if (TryGetDecimal(adj, "DefaultSellPct", out v))
        {
            var next = Clamp(v, _settings.DefaultSellPct, PctMaxDelta, 0.25m, 0.60m);
            if (Math.Abs(next - _settings.DefaultSellPct) >= 0.005m)
                { _settings.DefaultSellPct = next; changed.Add($"SellPct→{next:P0}"); }
        }

        if (TryGetDecimal(adj, "MinAbandonRise", out v))
        {
            var next = Clamp(v, _settings.MinAbandonRise, PctMaxDelta, 0.008m, 0.025m);
            if (Math.Abs(next - _settings.MinAbandonRise) >= 0.0005m)
                { _settings.MinAbandonRise = next; changed.Add($"MinAbandon→{next:P2}"); }
        }

        if (TryGetDecimal(adj, "MaxAbandonRise", out v))
        {
            var next = Clamp(v, _settings.MaxAbandonRise, PctMaxDelta, 0.025m, 0.08m);
            if (Math.Abs(next - _settings.MaxAbandonRise) >= 0.0005m)
                { _settings.MaxAbandonRise = next; changed.Add($"MaxAbandon→{next:P2}"); }
        }

        if (TryGetInt(adj, "CycleCooldownBars", out var iv))
        {
            var next = Math.Clamp(iv, _settings.CycleCooldownBars - IntMaxDelta, _settings.CycleCooldownBars + IntMaxDelta);
            next = Math.Clamp(next, 5, 20);
            if (next != _settings.CycleCooldownBars)
                { _settings.CycleCooldownBars = next; changed.Add($"Cooldown→{next}bars"); }
        }

        // Re-enforce cross-invariants
        _settings.RsiCycleSell   = Math.Max(_settings.RsiCycleSell,   _settings.RsiCrossoverMax + 5m);
        _settings.RsiDipBuy      = Math.Min(_settings.RsiDipBuy,      _settings.RsiCycleSell    - 15m);
        _settings.MaxAbandonRise = Math.Max(_settings.MaxAbandonRise, _settings.MinAbandonRise  + 0.01m);
    }

    private static decimal Clamp(decimal proposed, decimal current, decimal maxDelta, decimal min, decimal max)
        => Math.Clamp(Math.Clamp(proposed, current - maxDelta, current + maxDelta), min, max);

    private static bool TryGetDecimal(Dictionary<string, JsonElement> adj, string key, out decimal value)
    {
        value = 0m;
        if (!adj.TryGetValue(key, out var el)) return false;
        if (el.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return false;
        return el.TryGetDecimal(out value);
    }

    private static bool TryGetInt(Dictionary<string, JsonElement> adj, string key, out int value)
    {
        value = 0;
        if (!adj.TryGetValue(key, out var el)) return false;
        if (el.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return false;
        return el.TryGetInt32(out value);
    }

    // ── DTO ───────────────────────────────────────────────────────────────────

    private sealed class AdvisorResponse
    {
        public string?       Reasoning   { get; set; }
        public JsonElement?  Adjustments { get; set; }
    }
}
