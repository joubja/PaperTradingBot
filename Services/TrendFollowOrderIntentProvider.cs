using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PaperTradingBot.Config;
using PaperTradingBot.Indicators;
using PaperTradingBot.Interfaces;
using PaperTradingBot.Models;

namespace PaperTradingBot.Services;

/// <summary>
/// Trend-following, long-or-flat (spot, no shorting). Objective is TOTAL USD return, not coin:
/// hold the coin during uptrends, rotate to cash during downtrends. Benchmark = buy-and-hold.
///
/// Signal (on 1-minute aggregated closes to suppress 10s noise): dual-EMA spread with a
/// confirmation DEAD-BAND and a MIN-HOLD cooldown — the two whipsaw guards. A naive EMA cross
/// flips long↔flat constantly in chop, paying ~0.4% round-trip each time; the dead-band means
/// we only flip on a decisive separation, and min-hold blocks rapid re-flips.
///
/// State per symbol: { IsLong, LastSwitchHtfCount }. Seeded IsLong=true because BarProcessor
/// seeds the starting coin position on bar 1 (we begin long, same start state as buy-and-hold).
/// </summary>
public class TrendFollowOrderIntentProvider : IOrderIntentProvider
{
    // ── Signal parameters (v1: named consts; edit+rebuild to tune — rebuild ~5s vs 4-min run) ──
    private const int     FastPeriod       = 10;       // 1-min EMA fast (~10 min)
    private const int     SlowPeriod       = 40;       // 1-min EMA slow (~40 min). Keep ≤ ~70: history is capped at 500 raw bars.
    private const decimal ConfirmMarginPct = 0.0000m;  // dead-band (EMA spreads on slow grinds are tiny → 0 = pure sign cross; MinHoldBars is the whipsaw guard)
    private const int     MinHoldBars      = 15;       // no flip within this many completed 1-min bars of the last switch

    // ── Chop filter (Kaufman Efficiency Ratio): only flip when the market is actually trending ──
    // ER = |net move| / |total path| over ErPeriod HTF bars. ~1 = clean trend, ~0 = chop.
    // The fix for the choppy-uptrend whipsaw hole: in chop ER is low, so the false intra-trend
    // down-crosses are SUPPRESSED → we hold long through up-chop and capture the rise. A real
    // trend (up or down) lifts ER above the gate, so downtrend crash-protection survives.
    // Defaults; overridable via env (TF_ER_PERIOD / TF_ER_THRESHOLD) for sweeps without rebuild.
    private static readonly int     ErPeriod    = EnvInt("TF_ER_PERIOD", 20);      // HTF bars (~20 min) of directionality
    private static readonly decimal ErThreshold = EnvDec("TF_ER_THRESHOLD", 0.45m); // require ER ≥ this to flip (0 = off). Swept 0–0.6: 0.45–0.5 is the plateau; >0.5 erodes downtrend protection.

    // ── Asymmetric long-bias: exits are HARDER than entries ──────────────────────
    // The choppy-uptrend hole is a directional (high-ER) mid-trend DIP that clears the
    // ER gate and triggers a clean exit, after which we sit in cash and miss the rest.
    // Fix: require the bearish signal to PERSIST for ExitConfirmBars consecutive HTF
    // bars before exiting (a brief dip can't kick us out), while ENTRY stays immediate.
    // Trade-off: bigger ExitConfirmBars = more uptrend participation but slower crash exit.
    private static readonly int ExitConfirmBars = EnvInt("TF_EXIT_CONFIRM", 5); // consecutive bearish HTF bars to exit (1 = symmetric/off)
    private static readonly bool LegacyCount = EnvInt("TF_LEGACY_COUNT", 0) == 1; // diagnostic: revert to the buggy count-based hold clock

    private const int     Warmup           = SlowPeriod * 6 + 6; // ~246 raw 10s bars; must stay < 500

    private static int     EnvInt(string k, int d)     => int.TryParse(Environment.GetEnvironmentVariable(k), out var v) ? v : d;
    private static decimal EnvDec(string k, decimal d) => decimal.TryParse(Environment.GetEnvironmentVariable(k), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : d;

    private static readonly TimeSpan HtfBarSpan = TimeSpan.FromMinutes(1);

    private readonly BotOptions            _options;
    private readonly IPortfolioStateStore  _portfolio;
    private readonly ILogger<TrendFollowOrderIntentProvider> _logger;

    private sealed class TrendState
    {
        public bool     IsLong = true;            // start seeded-long
        public DateTime LastHtfBarTs = DateTime.MinValue; // start ts of the newest HTF bar already processed
        public int      BarsSinceSwitch = int.MaxValue;   // completed HTF bars since the last flip (max ⇒ first flip allowed)
        public int      BearishStreak;            // consecutive completed HTF bars the exit condition has held
        public int      LegacySwitchCount;        // diagnostic only: htfCloses.Count at last flip (the buggy clock)
    }

    private readonly Dictionary<string, TrendState> _state = new(StringComparer.OrdinalIgnoreCase);

    public TrendFollowOrderIntentProvider(
        IOptions<BotOptions> options,
        IPortfolioStateStore portfolio,
        ILogger<TrendFollowOrderIntentProvider> logger)
    {
        _options   = options.Value;
        _portfolio = portfolio;
        _logger    = logger;
    }

    public OrderIntent GetIntent(string symbol, IReadOnlyList<Candle> history)
    {
        if (history.Count < Warmup)
            return OrderIntent.None($"Warming up ({history.Count}/{Warmup})");

        var htfCloses = AggregateCloses(history, HtfBarSpan, out var lastHtfBarTs);
        if (htfCloses.Count < SlowPeriod)
            return OrderIntent.None("Warming up (HTF)");

        var emaFast = IndicatorMath.Ema(htfCloses, FastPeriod);
        var emaSlow = IndicatorMath.Ema(htfCloses, SlowPeriod);
        if (emaSlow <= 0m)
            return OrderIntent.None("EMA not ready");

        var spreadPct = (emaFast - emaSlow) / emaSlow;
        var er        = IndicatorMath.EfficiencyRatio(htfCloses, ErPeriod);

        if (!_state.TryGetValue(symbol, out var st))
        {
            st = new TrendState();
            _state[symbol] = st;
        }

        var bearish = spreadPct <= -ConfirmMarginPct;
        var bullish = spreadPct >=  ConfirmMarginPct;

        // ── Per-HTF-bar bookkeeping (advance once per newly completed HTF bar) ─────
        // Keyed on the completed-bucket timestamp (monotonic), NOT htfCloses.Count, which
        // plateaus under the rolling 500-bar history window. Only a gated bearish bar
        // (ER ≥ threshold) extends the exit-confirmation streak; anything else resets it.
        if (lastHtfBarTs > st.LastHtfBarTs)
        {
            if (st.BarsSinceSwitch != int.MaxValue) st.BarsSinceSwitch++;
            st.BearishStreak = (bearish && er >= ErThreshold) ? st.BearishStreak + 1 : 0;
            st.LastHtfBarTs  = lastHtfBarTs;
        }

        // ── DIAGNOSTIC: legacy count-based min-hold (the freeze bug) for side-by-side proof ──
        // TF_LEGACY_COUNT=1 reverts the hold clock to htfCloses.Count, which plateaus under
        // the rolling window so barsSinceSwitch sticks ⇒ the position freezes after ~83 min.
        var legacyBarsSinceSwitch = htfCloses.Count - st.LegacySwitchCount;

        // ── Flip state machine: dead-band + min-hold + chop gate + asymmetric exit ──
        // The ER gate blocks flips while the market is choppy (low directionality).
        // Exits also require a confirmed bearish streak; entries are immediate (long-bias).
        var holdOk = LegacyCount ? legacyBarsSinceSwitch >= MinHoldBars : st.BarsSinceSwitch >= MinHoldBars;
        if (holdOk && er >= ErThreshold)
        {
            if (st.IsLong && bearish && st.BearishStreak >= ExitConfirmBars)
            {
                st.IsLong = false;
                st.BarsSinceSwitch = 0;
                st.LegacySwitchCount = htfCloses.Count;
            }
            else if (!st.IsLong && bullish)
            {
                st.IsLong = true;
                st.BarsSinceSwitch = 0;
                st.LegacySwitchCount = htfCloses.Count;
            }
        }

        var close  = history[^1].Close;
        var step   = GetSymbolConfig(symbol)?.QuantityStep ?? 0.001m;
        var posQty = _portfolio.GetPositionQuantity(symbol);
        // Sub-step dust counts as flat: the seeded start position is the raw wallet balance
        // (e.g. 1.630248), not step-aligned — selling all of it fails qty validation, leaving
        // an unsellable remainder < step. Treat that remainder as "no position".
        var hasPos = posQty >= step;
        var ctx    = $"EMAf={emaFast:F2} EMAs={emaSlow:F2} spread={spreadPct:P3} er={er:F2} bearStreak={st.BearishStreak} state={(st.IsLong ? "LONG" : "FLAT")}";

        // ── Binary order emission: 100% in or 100% out (TargetQuantityOverride bypasses the sizer) ──
        if (st.IsLong && !hasPos)
        {
            var cash = _portfolio.GetCash();
            // Budget for slippage + fee so the order fits in cash (buys fill at close*(1+slippage)).
            var buyMult = (1m + _options.SlippagePercent) * (1m + _options.TakerFeePercent / 100m);
            var qty = close > 0m ? RoundDownToStep(cash / (close * buyMult), step) : 0m;
            if (qty <= 0m)
                return OrderIntent.None($"LONG but no cash | {ctx}");

            return new OrderIntent
            {
                IntentType             = OrderIntentType.Buy,
                OrderType              = OrderType.Market,
                TimeInForce            = _options.Orders.DefaultTimeInForce,
                Reason                 = $"Trend LONG entry | {ctx}",
                SignalStrength         = 1m,
                IndicatorContext       = ctx,
                TargetQuantityOverride = qty
            };
        }

        if (!st.IsLong && hasPos)
        {
            // Round down to step so the sell validates; any sub-step remainder is left as dust.
            var sellQty = RoundDownToStep(posQty, step);
            if (sellQty <= 0m)
                return OrderIntent.None($"FLAT but only dust | {ctx}");

            return new OrderIntent
            {
                IntentType             = OrderIntentType.Sell,
                OrderType              = OrderType.Market,
                TimeInForce            = _options.Orders.DefaultTimeInForce,
                Reason                 = $"Trend FLAT exit | {ctx}",
                SignalStrength         = 1m,
                IndicatorContext       = ctx,
                TargetQuantityOverride = sellQty
            };
        }

        return OrderIntent.None($"Hold {(st.IsLong ? "long" : "cash")} | {ctx}");
    }

    // ── Helpers (copied verbatim from BuildEthCyclingOrderIntentProvider) ─────────

    private SymbolConfig? GetSymbolConfig(string symbol) =>
        _options.Symbols.FirstOrDefault(s =>
            string.Equals(s.Symbol, symbol, StringComparison.OrdinalIgnoreCase));

    private static decimal RoundDownToStep(decimal value, decimal step) =>
        step > 0m ? Math.Floor(value / step) * step : value;

    // Returns the completed-bucket closes plus the START timestamp of the most recently
    // completed bucket. That timestamp is a MONOTONIC clock even though history is a rolling
    // 500-bar window (so htfCloses.Count plateaus and can't be used to count bars).
    private static IReadOnlyList<decimal> AggregateCloses(
        IReadOnlyList<Candle> history, TimeSpan period, out DateTime lastCompletedTs)
    {
        lastCompletedTs = DateTime.MinValue;
        if (history.Count == 0) return Array.Empty<decimal>();

        var result   = new List<decimal>(history.Count / 6);
        var bucketTs = FloorTs(history[0].Timestamp, period);
        var last     = history[0].Close;

        for (var i = 1; i < history.Count; i++)
        {
            var ts = FloorTs(history[i].Timestamp, period);
            if (ts != bucketTs)
            {
                result.Add(last);          // previous bucket is now complete
                lastCompletedTs = bucketTs; // its start timestamp
                bucketTs = ts;
            }
            last = history[i].Close;
        }
        return result;
    }

    private static DateTime FloorTs(DateTime dt, TimeSpan period) =>
        new(dt.Ticks / period.Ticks * period.Ticks, dt.Kind);
}
