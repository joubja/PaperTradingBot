using System.Text;
using PaperTradingBot.Services;

namespace PaperTradingBot.AI;

/// <summary>
/// Builds the per-call user prompt sent to Claude, giving it full context to reason
/// about which accumulation mechanism is underperforming and what to change.
/// </summary>
public sealed class AdvisorContextBuilder
{
    private readonly BotStateService          _botState;
    private readonly PerformanceTracker       _perf;
    private readonly LiveSettingsService      _settings;
    private readonly OptimizerStateService    _optimizerState;
    private readonly StrategyBanditOptimizer  _bandit;

    public AdvisorContextBuilder(
        BotStateService          botState,
        PerformanceTracker       perf,
        LiveSettingsService      settings,
        OptimizerStateService    optimizerState,
        StrategyBanditOptimizer  bandit)
    {
        _botState       = botState;
        _perf           = perf;
        _settings       = settings;
        _optimizerState = optimizerState;
        _bandit         = bandit;
    }

    public string Build(string trigger)
    {
        var sb  = new StringBuilder();
        var s   = _settings;
        var now = DateTime.UtcNow;

        // ── 1. Session coin performance ───────────────────────────────────────
        var sessionEth   = _botState.Positions.TryGetValue(_botState.PrimarySymbol, out var pos) ? pos.Quantity : 0m;
        var startingEth  = _botState.StartingEth;
        var netEth       = sessionEth - startingEth;
        var gainPct      = startingEth > 0m ? (double)netEth / (double)startingEth * 100.0 : 0.0;
        var sessionHours = _botState.SessionStartedAt == DateTime.MinValue
            ? 0.0
            : (now - _botState.SessionStartedAt).TotalHours;
        var ethPerHour = sessionHours > 0.05 ? (double)netEth / sessionHours : 0.0;

        var ccy = _botState.PrimarySymbol.Replace("USDT", "").Replace("usdt", "");
        sb.AppendLine($"## Session {ccy} Performance");
        sb.AppendLine($"Runtime:          {(sessionHours > 0.05 ? $"{sessionHours:F1}h" : "< 3 min")}");
        sb.AppendLine($"{ccy} position:   {sessionEth:F5}  (started: {startingEth:F5})");
        sb.AppendLine($"Net {ccy}:        {netEth:+0.00000;-0.00000}  ({gainPct:+0.000;-0.000}%  |  {ethPerHour:+0.00000;-0.00000} {ccy}/hr)");
        sb.AppendLine($"Cash:             ${_botState.Cash:F2}");

        // Current market state from strategy status summary (contains price, RSI, phase detail)
        var statusSummary = _botState.StrategyStatus?.Summary;
        if (!string.IsNullOrWhiteSpace(statusSummary))
            sb.AppendLine($"Market state:     {statusSummary}");
        sb.AppendLine();

        // ── 2. Cycling mechanism ──────────────────────────────────────────────
        var cycles    = _perf.GetRecent(20);
        // Settled abandons (NetEthGain != 0) are real coin losses — include them in all stats.
        // Unsettled abandon stubs (NetEthGain == 0, still in recovery watch) are excluded.
        var settled   = cycles.Where(c => !c.IsAbandoned || c.NetEthGain != 0m).ToList();
        var completed = cycles.Where(c => !c.IsAbandoned).ToList();
        var abandoned = cycles.Where(c => c.IsAbandoned).ToList();
        var settledAbandons = abandoned.Where(c => c.NetEthGain != 0m).ToList();
        var winners   = settled.Where(c => c.NetEthGain > 0).ToList();

        sb.AppendLine($"## Cycling Mechanism (last {cycles.Count} events)");
        if (cycles.Count == 0)
        {
            sb.AppendLine("No cycles recorded yet.");
        }
        else
        {
            var totalCyclingEth = settled.Sum(c => c.NetEthGain);
            var avgEthPerCycle  = settled.Count > 0 ? settled.Average(c => (double)c.NetEthGain) : 0.0;
            var winRate         = settled.Count > 0 ? (double)winners.Count / settled.Count : 0.0;
            var abandonRate     = cycles.Count > 0 ? (double)abandoned.Count / cycles.Count : 0.0;

            sb.AppendLine($"Settled: {settled.Count}  Abandoned (settled): {settledAbandons.Count}  " +
                          $"Win rate: {winRate:P0}  Abandon rate: {abandonRate:P0}");
            sb.AppendLine($"Avg {ccy}/cycle (all settled): {avgEthPerCycle:+0.00000;-0.00000}  " +
                          $"Total from cycling: {(double)totalCyclingEth:+0.00000;-0.00000} {ccy}");
            sb.AppendLine($"Rolling reward (last 5): {_perf.RollingReward():+0.000;-0.000}");

            sb.AppendLine("Recent cycles (WIN=clean gain, LOSS=settled abandon or negative rebuy, PENDING=recovery watch active):");
            foreach (var c in cycles.Take(5))
            {
                string outcome;
                if (c.IsAbandoned && c.NetEthGain == 0m) outcome = "PEND";
                else if (c.NetEthGain >= 0m)              outcome = " WIN";
                else                                       outcome = "LOSS";
                var durMins = (c.CompletedAt - c.SellTimestamp).TotalMinutes;
                sb.AppendLine($"  [{outcome}] {c.NetEthGain:+0.00000;-0.00000} {ccy}  " +
                              $"sell={c.SellPrice:F2} buy={c.BuyPrice:F2}  " +
                              $"dur={durMins:F0}min  {c.CompletedAt:HH:mm dd-MMM}");
            }
        }
        sb.AppendLine();

        // ── 3. Base accumulation signal stats ─────────────────────────────────
        var accumOutcomes = _perf.GetRecentAccumulationOutcomes(30);
        var dipOutcomes   = accumOutcomes.Where(a => a.Trigger == "RsiDip").ToList();
        var crossOutcomes = accumOutcomes.Where(a => a.Trigger == "EmaCross").ToList();

        sb.AppendLine($"## Base Accumulation Signals (last {accumOutcomes.Count} scored lots)");
        if (accumOutcomes.Count == 0)
        {
            sb.AppendLine("No accumulation lots scored yet (lots are scored at each cycle sell).");
        }
        else
        {
            AppendAccumStats(sb, "RSI Dip buys ", dipOutcomes);
            AppendAccumStats(sb, "EMA Cross buys", crossOutcomes);
        }
        sb.AppendLine();

        // ── 4. Settings with descriptions ─────────────────────────────────────
        sb.AppendLine("## Current Settings (current → default | what it controls)");
        sb.AppendLine($"RsiDipBuy:         {s.RsiDipBuy:F1} → 40.0  | lower = fewer RSI dip buys, stricter entry");
        sb.AppendLine($"RsiCrossoverMax:   {s.RsiCrossoverMax:F1} → 60.0  | RSI ceiling for EMA cross buys; too high = buys into momentum");
        sb.AppendLine($"RsiCycleSell:      {s.RsiCycleSell:F1} → 72.0  | higher = fewer cycles, requires stronger RSI spike to sell");
        sb.AppendLine($"RsiCycleRebuy:     {s.RsiCycleRebuy:F1} → 45.0  | lower = more patient rebuy, waits for bigger dip");
        sb.AppendLine($"DefaultSellPct:    {s.DefaultSellPct:P0} → 40%   | fraction of ETH sold per cycle; higher = more ETH at stake");
        sb.AppendLine($"MinAbandonRise:    {s.MinAbandonRise:P2} → 1.50% | abandon if price rises above this; lower = bails earlier");
        sb.AppendLine($"MaxAbandonRise:    {s.MaxAbandonRise:P2} → 4.50% | forced abandon at this rise; higher = more patient");
        sb.AppendLine($"CycleCooldownBars: {s.CycleCooldownBars} → 10     | bars between cycles; more = fewer cycles, avoids churn");
        sb.AppendLine();

        // ── 5. Recent optimizer adjustments (avoid flip-flopping) ─────────────
        var recentAdj = _optimizerState.GetHistory()
            .Where(a => a.Source == "Claude")
            .Take(3)
            .ToList();
        if (recentAdj.Count > 0)
        {
            sb.AppendLine("## Recent Claude Adjustments (avoid reversing these without new evidence)");
            foreach (var adj in recentAdj)
            {
                sb.AppendLine($"  {adj.Timestamp.ToLocalTime():HH:mm dd-MMM} — {adj.Changes}");
                if (!string.IsNullOrWhiteSpace(adj.Reasoning))
                    sb.AppendLine($"    → {adj.Reasoning}");
            }
            sb.AppendLine();
        }

        // ── 6. Bandit arm convergence ─────────────────────────────────────────
        var armStatus = _bandit.GetArmStatus();
        if (armStatus.Any(a => a.TotalPulls > 0))
        {
            sb.AppendLine("## Bandit Learning (active arm* | reward/pulls per candidate)");
            sb.AppendLine("The bandit has tried these arms — avoid reversing its conclusions without new evidence.");
            foreach (var a in armStatus)
            {
                sb.Append($"  {a.Name,-18}: ");
                for (var i = 0; i < a.Arms.Length; i++)
                {
                    var arm = a.Arms[i];
                    sb.Append($"{arm.Value}:{arm.MeanReward:+0.00;-0.00}/{arm.Pulls}");
                    if (i == a.ActiveArmIndex) sb.Append('*');
                    if (i < a.Arms.Length - 1) sb.Append("  ");
                }
                sb.AppendLine();
            }
            sb.AppendLine();
        }

        // ── 7. Bot state ──────────────────────────────────────────────────────
        sb.AppendLine("## Bot Status");
        sb.AppendLine($"Phase:          {_botState.StrategyStatus?.Phase ?? "unknown"}");
        sb.AppendLine($"CyclingEnabled: {_botState.CyclingEnabled}");
        sb.AppendLine($"Trigger:        {trigger}");

        return sb.ToString();
    }

    private static void AppendAccumStats(StringBuilder sb, string label, List<AccumulationOutcome> outcomes)
    {
        if (outcomes.Count == 0)
        {
            sb.AppendLine($"{label}: no data");
            return;
        }
        var avgReturn = outcomes.Average(o => o.BuyPrice > 0m
            ? (double)((o.SellPrice - o.BuyPrice) / o.BuyPrice)
            : 0.0);
        var winRate   = (double)outcomes.Count(o => o.Reward > 0f) / outcomes.Count;
        var avgReward = outcomes.Average(o => (double)o.Reward);
        sb.AppendLine($"{label}: {outcomes.Count} lots  avg return={avgReturn:+0.00%;-0.00%}  " +
                      $"win rate={winRate:P0}  avg reward={avgReward:+0.000;-0.000}");
    }
}
