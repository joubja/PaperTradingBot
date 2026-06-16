using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PaperTradingBot.Config;
using PaperTradingBot.Indicators;
using PaperTradingBot.Interfaces;
using PaperTradingBot.Models;

namespace PaperTradingBot.Services;

/// <summary>
/// Long-biased EXPOSURE controller (spot, long-or-flat). Objective is total USD return.
///
/// Premise (hard-won from the cycling + trend-follow dead ends): you cannot beat buy-and-hold
/// by predicting price direction on this data. The only durable spot levers are (1) how much
/// coin vs cash you hold over time and (2) execution cost. So this strategy does NOT emit
/// buy/sell signals — it computes a TARGET EXPOSURE (fraction of equity in coin) each bar and
/// rebalances toward it, defaulting LONG (to keep the bull drift) and stepping aside only on a
/// confirmed crash. AI is intentionally absent here: this is the non-AI chassis. If it has no
/// out-of-sample edge vs B&H-after-costs, no AI layer can manufacture one — so we prove the
/// chassis first, then let the bandit/advisor tune the regime calls and sizing.
///
/// Two untested-until-now mechanisms (everything we killed before was MA-cross binary):
///   • Volatility targeting — scale exposure ∝ TargetVol / realizedVol (vol clusters and is far
///     more predictable than returns). 0 = off.
///   • Drawdown circuit-breaker — de-risk to MinExposure when price is ≥ ExitDD below a rolling
///     high; re-enter when back within ReentryDD. Hysteresis (ExitDD > ReentryDD) blocks churn.
///
/// Self-contained HTF history: the bar pipeline only keeps a 500-bar (~83 min) rolling window —
/// far too short for regime/crash detection. So we accumulate our OWN 1-min close buffer in state
/// (capped, ~days) and derive every signal from it. This also avoids the count-plateau bug class.
/// </summary>
public class ExposureControllerOrderIntentProvider : IOrderIntentProvider
{
    // ── Params (env-overridable for sweeps without rebuild; EC_* ) ────────────────
    private static readonly int     EmaFast     = EnvInt("EC_EMA_FAST", 10);
    private static readonly int     EmaSlow     = EnvInt("EC_EMA_SLOW", 40);
    private static readonly int     VolWindow   = EnvInt("EC_VOL_WINDOW", 60);     // HTF bars for realized vol
    private static readonly int     HighWindow  = EnvInt("EC_HIGH_WINDOW", 720);   // HTF bars for the rolling high (720 = 12h)
    private static readonly decimal ExitDD      = EnvDec("EC_EXIT_DD", 0.10m);      // de-risk when ≥ this far below rolling high
    private static readonly decimal ReentryDD   = EnvDec("EC_REENTRY_DD", 0.05m);  // re-enter when back within this of the high
    private static readonly decimal TargetVol   = EnvDec("EC_TARGET_VOL", 0m);     // per-HTF-bar pct stdev target (0 = vol-targeting off)
    private static readonly decimal MinExposure = EnvDec("EC_MIN_EXPOSURE", 0m);   // exposure when de-risked (0 = full cash)
    private static readonly decimal RebalBand   = EnvDec("EC_REBAL_BAND", 0.08m);  // only rebalance when |target−current| ≥ this × equity
    private static readonly int     MinHoldBars = EnvInt("EC_MIN_HOLD", 6);        // min HTF bars between rebalances
    private const int               MaxBuffer   = 6000;                            // HTF closes kept (~100h); bounds memory
    private const int               WarmupRaw   = 60;                              // raw 10s bars before we bother aggregating

    private static readonly TimeSpan HtfBarSpan = TimeSpan.FromMinutes(1);

    private readonly BotOptions           _options;
    private readonly IPortfolioStateStore _portfolio;
    private readonly ILogger<ExposureControllerOrderIntentProvider> _logger;

    private sealed class State
    {
        public DateTime      LastHtfBarTs    = DateTime.MinValue;
        public List<decimal> Closes          = new(MaxBuffer);
        public bool          DeRisked;                 // circuit-breaker latched
        public int           BarsSinceTrade  = int.MaxValue; // max ⇒ first rebalance allowed immediately
        public decimal       TargetExposure  = 1m;     // cached; recomputed once per HTF bar
        public string        Ctx             = "";
    }

    private readonly Dictionary<string, State> _state = new(StringComparer.OrdinalIgnoreCase);

    public ExposureControllerOrderIntentProvider(
        IOptions<BotOptions> options,
        IPortfolioStateStore portfolio,
        ILogger<ExposureControllerOrderIntentProvider> logger)
    {
        _options   = options.Value;
        _portfolio = portfolio;
        _logger    = logger;
    }

    public OrderIntent GetIntent(string symbol, IReadOnlyList<Candle> history)
    {
        if (history.Count < WarmupRaw)
            return OrderIntent.None($"Warming up ({history.Count}/{WarmupRaw})");

        var htfCloses = AggregateCloses(history, HtfBarSpan, out var lastHtfBarTs);
        if (htfCloses.Count == 0)
            return OrderIntent.None("No completed HTF bar yet");

        if (!_state.TryGetValue(symbol, out var st))
        {
            st = new State();
            _state[symbol] = st;
        }

        // ── On each newly completed HTF bar: extend our own buffer and recompute the target ──
        if (lastHtfBarTs > st.LastHtfBarTs)
        {
            if (st.Closes.Count == 0)
                st.Closes.AddRange(htfCloses);   // seed once from the window so we start warm
            else
                st.Closes.Add(htfCloses[^1]);    // contiguous 10s data ⇒ exactly one new completed bar

            if (st.Closes.Count > MaxBuffer)
                st.Closes.RemoveRange(0, st.Closes.Count - MaxBuffer);

            if (st.BarsSinceTrade != int.MaxValue) st.BarsSinceTrade++;
            st.LastHtfBarTs = lastHtfBarTs;
            RecomputeTarget(st);
        }

        if (st.Closes.Count < EmaSlow)
            return OrderIntent.None($"Warming up (HTF {st.Closes.Count}/{EmaSlow})");

        // ── Translate target exposure → rebalance order (runs every raw bar; cheap) ──
        var close  = history[^1].Close;
        var step   = GetSymbolConfig(symbol)?.QuantityStep ?? 0.001m;
        var posQty = _portfolio.GetPositionQuantity(symbol);
        var cash   = _portfolio.GetCash();
        var equity = cash + posQty * close;
        if (equity <= 0m || close <= 0m)
            return OrderIntent.None($"No equity | {st.Ctx}");

        var curExposure  = posQty * close / equity;
        var desiredQty   = st.TargetExposure * equity / close;
        var deltaQty     = desiredQty - posQty;
        var deltaNotional = Math.Abs(deltaQty) * close;
        var ctx = $"{st.Ctx} cur={curExposure:P0}→tgt={st.TargetExposure:P0}";

        if (st.BarsSinceTrade < MinHoldBars)
            return OrderIntent.None($"Hold (min-hold) | {ctx}");
        if (deltaNotional < RebalBand * equity)
            return OrderIntent.None($"Hold (within band) | {ctx}");

        if (deltaQty > 0m)
        {
            // Buy toward target; budget for slippage + fee so the order fits in cash.
            var buyMult     = (1m + _options.SlippagePercent) * (1m + _options.TakerFeePercent / 100m);
            var affordable  = cash / (close * buyMult);
            var qty         = RoundDownToStep(Math.Min(deltaQty, affordable), step);
            if (qty <= 0m)
                return OrderIntent.None($"Buy but no cash | {ctx}");
            st.BarsSinceTrade = 0;
            return new OrderIntent
            {
                IntentType             = OrderIntentType.Buy,
                OrderType              = OrderType.Market,
                TimeInForce            = _options.Orders.DefaultTimeInForce,
                Reason                 = $"Rebalance ↑ | {ctx}",
                SignalStrength         = 1m,
                IndicatorContext       = ctx,
                TargetQuantityOverride = qty
            };
        }

        var sellQty = RoundDownToStep(Math.Min(-deltaQty, posQty), step);
        if (sellQty <= 0m)
            return OrderIntent.None($"Sell but only dust | {ctx}");
        st.BarsSinceTrade = 0;
        return new OrderIntent
        {
            IntentType             = OrderIntentType.Sell,
            OrderType              = OrderType.Market,
            TimeInForce            = _options.Orders.DefaultTimeInForce,
            Reason                 = $"Rebalance ↓ | {ctx}",
            SignalStrength         = 1m,
            IndicatorContext       = ctx,
            TargetQuantityOverride = sellQty
        };
    }

    // ── Signal + target-exposure computation (once per completed HTF bar) ─────────
    private void RecomputeTarget(State st)
    {
        var closes = st.Closes;
        var last   = closes[^1];

        // Drawdown circuit-breaker (hysteresis).
        var highLook   = Math.Min(HighWindow, closes.Count);
        var rollingHigh = 0m;
        for (var i = closes.Count - highLook; i < closes.Count; i++)
            if (closes[i] > rollingHigh) rollingHigh = closes[i];
        var ddFromHigh = rollingHigh > 0m ? (rollingHigh - last) / rollingHigh : 0m;

        if (!st.DeRisked && ddFromHigh >= ExitDD)      st.DeRisked = true;
        else if (st.DeRisked && ddFromHigh <= ReentryDD) st.DeRisked = false;

        // Volatility targeting (per-HTF-bar pct-return stdev).
        var volScale = 1m;
        decimal realizedVol = 0m;
        if (TargetVol > 0m)
        {
            var w = Math.Min(VolWindow, closes.Count - 1);
            if (w >= 2)
            {
                var rets = new List<decimal>(w);
                for (var i = closes.Count - w; i < closes.Count; i++)
                    if (closes[i - 1] > 0m) rets.Add((closes[i] - closes[i - 1]) / closes[i - 1]);
                realizedVol = IndicatorMath.StdDev(rets);
                if (realizedVol > 0m)
                    volScale = Math.Clamp(TargetVol / realizedVol, MinExposure, 1m);
            }
        }

        st.TargetExposure = st.DeRisked ? MinExposure : volScale;
        st.Ctx = $"dd={ddFromHigh:P1} {(st.DeRisked ? "DERISK" : "risk-on")}"
               + (TargetVol > 0m ? $" rv={realizedVol:P2} vs={volScale:F2}" : "");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────
    private SymbolConfig? GetSymbolConfig(string symbol) =>
        _options.Symbols.FirstOrDefault(s =>
            string.Equals(s.Symbol, symbol, StringComparison.OrdinalIgnoreCase));

    private static decimal RoundDownToStep(decimal value, decimal step) =>
        step > 0m ? Math.Floor(value / step) * step : value;

    private static int     EnvInt(string k, int d)     => int.TryParse(Environment.GetEnvironmentVariable(k), out var v) ? v : d;
    private static decimal EnvDec(string k, decimal d) => decimal.TryParse(Environment.GetEnvironmentVariable(k), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : d;

    // Completed-bucket closes + the START timestamp of the most recently completed bucket
    // (monotonic clock, unlike the count which plateaus under the rolling history window).
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
                result.Add(last);
                lastCompletedTs = bucketTs;
                bucketTs = ts;
            }
            last = history[i].Close;
        }
        return result;
    }

    private static DateTime FloorTs(DateTime dt, TimeSpan period) =>
        new(dt.Ticks / period.Ticks * period.Ticks, dt.Kind);
}
