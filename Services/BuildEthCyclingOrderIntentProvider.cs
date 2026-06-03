using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PaperTradingBot.AI;
using PaperTradingBot.Config;
using PaperTradingBot.Indicators;
using PaperTradingBot.Interfaces;
using PaperTradingBot.Models;

namespace PaperTradingBot.Services;

/// <summary>
/// BuildEth + adaptive buy-the-dip cycling.
///
/// Cycling layer (auto on/off):
///   SELL trigger  — 1-min RSI > 72 AND declining from its 1-min peak (peak confirmation).
///   REBUY trigger — trailing low dipped ≥ break-even AND price bounced ≥ 0.3% off that low,
///                   OR 1-min RSI < 45 (safety valve). Buys back with all cash → more ETH than sold.
///
/// Multi-timeframe design:
///   Decision signals (RSI)  — computed on 1-minute aggregated closes to suppress 10-second noise.
///   Execution signals (EMA, ATR, price) — computed on raw 10-second bars for timing precision.
///   RSI thresholds are calibrated for 1-min bars; the 14-period window spans ~14 minutes.
///
/// Adaptive parameters (recomputed each bar from ATR, locked in at cycle sell):
///   SellPct  — 30–55 %: flat market = sell more (lower break-even threshold);
///                         volatile market = sell less (price swings do the work).
///   AbandonRise — 1.5–4.5 %: volatile market = wait longer before giving up;
///                              flat market = bail sooner and re-enter at the new level.
///
/// Post-cycle cooldown (10 bars ≈ 100 s) prevents re-selling into the same RSI spike
/// immediately after a rebuy or abandon, avoiding rapid fee-compounding micro-cycles.
///
/// After every complete cycle the strategy checks the last 5 cycles in SQLite.
/// If ≥ 3 of 5 completed with a net ETH loss (abandoned cycles don't count — price going
/// up is opportunity cost, not an actual loss), cycling is suspended until a deep
/// dip (1-min RSI < 28) signals conditions may be improving.
/// </summary>
public class BuildEthCyclingOrderIntentProvider : IOrderIntentProvider
{
    private const int FastEma = 9;
    private const int SlowEma = 21;
    private const int RsiPeriod = 14;
    private const int AtrPeriod = 14;

    // Multi-timeframe: RSI is computed on 1-minute aggregated bars to suppress 10s noise
    private const int HtfMinutes = 1;
    private static readonly TimeSpan HtfBarSpan = TimeSpan.FromMinutes(HtfMinutes);

    // Adaptive sell/abandon calibration constants (internal, not user-tunable)
    private const decimal ReferenceAtrPct = 0.0002m;  // ~0.02 % per bar = realistic ETH 10s ATR
    private const decimal AbandonAtrScale = 800m;     // tuned for 10-second bars

    // Trend filter EMA anchor (structural, not user-tunable)
    private const int TrendEma = 50;                  // ~8 min anchor at 10s bars

    // Minimum dip depth required before rebuy fires. Must exceed MinBounceFromLow + 2*fee
    // so that the effective price improvement (dip - bounce) beats round-trip cost.
    // At 1% dip, 0.1% bounce, 0.1% fee each side: margin = 1% - 0.1% - 0.2% = 0.7% ✓
    private const decimal MinCycleDipPct = 0.010m;

    private readonly BotOptions _options;
    private readonly IPortfolioStateStore _portfolio;
    private readonly DatabaseService _db;
    private readonly BotStateService _state;
    private readonly LiveSettingsService _settings;
    private readonly AccumulationTracker _accumTracker;
    private readonly ILogger<BuildEthCyclingOrderIntentProvider> _logger;

    // Per-symbol cycle state
    private readonly Dictionary<string, CycleState> _cycleState =
        new(StringComparer.OrdinalIgnoreCase);

    private bool _cyclingEnabled = true;

    public BuildEthCyclingOrderIntentProvider(
        IOptions<BotOptions> options,
        IPortfolioStateStore portfolio,
        DatabaseService db,
        BotStateService state,
        LiveSettingsService settings,
        AccumulationTracker accumTracker,
        ILogger<BuildEthCyclingOrderIntentProvider> logger)
    {
        _options       = options.Value;
        _portfolio     = portfolio;
        _db            = db;
        _state         = state;
        _settings      = settings;
        _accumTracker  = accumTracker;
        _logger        = logger;
    }

    public void ResetSessionState()
    {
        _cycleState.Clear();
        _accumTracker.ClearAll();
        _cyclingEnabled = true;
    }

    /// <summary>
    /// Restores active sell cycle state after a crash by reading open (incomplete) cycles from
    /// the database. TrailingLow is conservatively reset to the sell price; AbandonRisePct is
    /// recomputed adaptively on the first bar.
    /// </summary>
    public void RestoreFromSession(string sessionId, DatabaseService db)
    {
        var openCycles = db.GetOpenCyclesForSession(sessionId);
        foreach (var cycle in openCycles)
        {
            var cs = new CycleState
            {
                ActiveSell     = true,
                SellPrice      = cycle.SellPrice,
                SellQty        = cycle.SoldQuantity,
                OpenCycleId    = cycle.Id,
                TrailingLow    = cycle.SellPrice, // conservative: reset to sell price
                AbandonRisePct = 0.03m            // default; recomputed on first bar from ATR
            };
            _cycleState[cycle.Symbol] = cs;

            _logger.LogInformation(
                "RESUME | Restored active sell cycle for {Symbol}: SellPrice={Price:F4} Qty={Qty:F5}",
                cycle.Symbol, cycle.SellPrice, cycle.SoldQuantity);
        }
    }

    public OrderIntent GetIntent(string symbol, IReadOnlyList<Candle> history)
    {
        if (!_cycleState.TryGetValue(symbol, out var csWarm))
        {
            csWarm = new CycleState();
            _cycleState[symbol] = csWarm;
        }

        if (history.Count < _options.Runtime.WarmupBars)
        {
            var secsLeft = (_options.Runtime.WarmupBars - history.Count) * 10;
            var minsLeft = secsLeft / 60;
            var timeLeft = minsLeft >= 1 ? $"~{minsLeft}m" : $"~{secsLeft}s";

            if (csWarm.ActiveSell && history.Count > 0)
            {
                // Keep trailing low current and surface cycle state through the warmup period
                var wClose = history[^1].Close;
                if (wClose < csWarm.TrailingLow) csWarm.TrailingLow = wClose;
                var wBreakEven = 2m * _options.TakerFeePercent / 100m;
                var wMinDrop   = Math.Max(wBreakEven * 1.1m, MinCycleDipPct);
                var wDip       = csWarm.SellPrice > 0m ? (csWarm.SellPrice - csWarm.TrailingLow) / csWarm.SellPrice : 0m;

                _state.NotifyStrategyStatus(new("WarmingUp",
                    $"Warming up ({history.Count}/{_options.Runtime.WarmupBars}, {timeLeft}) — cycle active | sold {csWarm.SellQty:F3} ETH @ ${csWarm.SellPrice:F2} | dip {wDip:P2}",
                    SellPrice:    csWarm.SellPrice,
                    SellQty:      csWarm.SellQty,
                    CurrentDropPct: wDip,
                    MinDropPct:   wMinDrop));
            }
            else
            {
                _state.NotifyStrategyStatus(new("WarmingUp",
                    $"Building RSI & indicator history — {history.Count}/{_options.Runtime.WarmupBars} bars ({timeLeft} remaining)"));
            }

            return OrderIntent.None($"Warming up ({history.Count}/{_options.Runtime.WarmupBars})");
        }

        // Snapshot live settings once per bar so all logic below uses consistent values
        var rsiDipBuy          = _settings.RsiDipBuy;
        var rsiCrossoverMax    = _settings.RsiCrossoverMax;
        var rsiCycleSell       = _settings.RsiCycleSell;
        var rsiCycleRebuy      = _settings.RsiCycleRebuy;
        var cyclingReenableRsi = _settings.CyclingReenableRsi;
        var minSellPct         = _settings.MinSellPct;
        var maxSellPct         = _settings.MaxSellPct;
        var defaultSellPct     = _settings.DefaultSellPct;
        var minAbandonRise     = _settings.MinAbandonRise;
        var maxAbandonRise     = _settings.MaxAbandonRise;
        var minBounceFromLow   = _settings.MinBounceFromLow;
        var trendSpreadBlock   = _settings.TrendSpreadBlock;
        var cycleCooldownBars  = _settings.CycleCooldownBars;

        var closes     = history.Select(c => c.Close).ToList();
        var prevCloses = closes.Take(closes.Count - 1).ToList();
        var close      = closes[^1];

        // Execution signals: 10-second bars for timing precision
        var fastEma     = IndicatorMath.Ema(closes, FastEma);
        var slowEma     = IndicatorMath.Ema(closes, SlowEma);
        var fastEmaPrev = IndicatorMath.Ema(prevCloses, FastEma);
        var slowEmaPrev = IndicatorMath.Ema(prevCloses, SlowEma);
        var atr         = AtrIndicator.Calculate(history, AtrPeriod);
        var macd        = MacdIndicator.Calculate(closes);
        var trendEmaVal = IndicatorMath.Ema(closes, TrendEma);
        var spreadPct   = slowEma > 0m ? Math.Abs(fastEma - slowEma) / slowEma * 100m : 0m;

        // Decision signals: 1-minute aggregated closes to suppress 10-second noise
        var htfCloses = AggregateCloses(history, HtfBarSpan);
        var rsi       = RsiIndicator.Calculate(htfCloses, RsiPeriod);
        // rsiPrev = RSI at the previous completed 1-min bar (peak-confirmation on minute scale)
        var rsiPrev   = htfCloses.Count >= 2
            ? RsiIndicator.Calculate(htfCloses.Take(htfCloses.Count - 1).ToList(), RsiPeriod)
            : rsi;

        // True when ETH is in a clear sustained uptrend: price above medium-term EMA,
        // short EMA meaningfully above long EMA, and the spread indicates real momentum.
        var strongUptrend = close > trendEmaVal && fastEma > slowEma && spreadPct > trendSpreadBlock;

        var ctx = $"EMA9={fastEma:F4} EMA21={slowEma:F4} EMA50={trendEmaVal:F4} RSI1m={rsi:F1}(n={htfCloses.Count}) MACD_H={macd.Histogram:F6} ATR={atr:F6}";

        var (adaptiveSellPct, adaptiveAbandon) = ComputeAdaptiveParams(
            atr, close, minSellPct, maxSellPct, defaultSellPct, minAbandonRise, maxAbandonRise);

        var cs = csWarm;

        // ── Active sell cycle: waiting for rebuy ──────────────────────────────
        if (cs.ActiveSell)
        {
            // Update trailing low every bar
            if (close < cs.TrailingLow) cs.TrailingLow = close;

            var breakEvenDrop = 2m * _options.TakerFeePercent / 100m;
            var minDrop       = Math.Max(breakEvenDrop * 1.1m, MinCycleDipPct);

            // How deep the dip has gone from the sell price (uses trailing low, not current price)
            var dipDepth     = cs.SellPrice > 0m ? (cs.SellPrice - cs.TrailingLow) / cs.SellPrice : 0m;
            // How much price has bounced off the trailing low
            var bounceFromLow = cs.TrailingLow > 0m ? (close - cs.TrailingLow) / cs.TrailingLow : 0m;

            // Good rebuy: dip deep enough AND price bouncing off the low
            bool dipEnoughAndBouncing = dipDepth >= minDrop && bounceFromLow >= minBounceFromLow;
            // Safety valve: RSI deeply oversold — still require meaningful dip AND viable qty to avoid zero-gain cycles
            var viableStep = GetSymbolConfig(symbol)?.QuantityStep ?? 0.001m;
            bool rsiOversold = rsi < rsiCycleRebuy
                && dipDepth >= minDrop * 0.5m
                && cs.SellQty >= viableStep / Math.Max(dipDepth - breakEvenDrop, viableStep);

            if (dipEnoughAndBouncing || rsiOversold)
            {
                // [SM-014] Compute rebuy quantity BEFORE mutating any state.
                // If cash is zero the sell never applied to the portfolio — stay in ActiveSell
                // and retry on the next bar rather than silently abandoning the cycle.
                var rebuyCash     = _portfolio.GetCash();
                var rebuyStep     = GetSymbolConfig(symbol)?.QuantityStep ?? 0.001m;
                var feeMultiplier = 1m + _options.TakerFeePercent / 100m;
                var rebuyQty      = rebuyCash > 0m && close > 0m
                    ? RoundDownToStep(rebuyCash / (close * feeMultiplier), rebuyStep)
                    : 0m;

                if (rebuyQty <= 0m)
                {
                    _logger.LogWarning(
                        "CYCLE REBUY SKIPPED | {Symbol} rebuyQty=0 — cash={Cash:F2} USDT. " +
                        "Keeping ActiveSell=true to retry. " +
                        "If cash is always zero the sell may not have applied to the portfolio.",
                        symbol, rebuyCash);
                    // Stay in ActiveSell — do NOT reset state, do NOT close DB row.
                    _state.NotifyStrategyStatus(new("ActiveSell",
                        $"Waiting for rebuy (no cash!) — sold {cs.SellQty:F3} ETH @ ${cs.SellPrice:F2} | dip {dipDepth:P2}",
                        SellPrice: cs.SellPrice, SellQty: cs.SellQty,
                        CurrentDropPct: dipDepth, MinDropPct: minDrop));
                    return OrderIntent.None($"Rebuy skipped — cash={rebuyCash:F2} USDT | {ctx}");
                }

                // Capture before state reset so the event carries the original sell context
                var priorSellQty   = cs.SellQty;
                var priorSellPrice = cs.SellPrice;
                var priorFeatures  = cs.FeaturesAtSell;
                var priorSellTs    = cs.SellTimestamp;
                var priorCycleId   = cs.OpenCycleId;   // carry to intent for post-fill DB correction

                _logger.LogInformation(
                    "CYCLE REBUY | {Symbol} Sell={Sell:F4} Low={Low:F4} Close={Close:F4} Dip={Dip:P2} Bounce={Bounce:P2} RSI={Rsi:F1}",
                    symbol, cs.SellPrice, cs.TrailingLow, close, dipDepth, bounceFromLow, rsi);

                cs.ActiveSell  = false;

                if (cs.OpenCycleId.HasValue && !string.IsNullOrEmpty(_state.ActiveSessionId))
                    CompleteCycleAndCheckFeasibility(symbol, cs.OpenCycleId.Value, close, rsi);

                cs.OpenCycleId      = null;
                cs.SellPrice        = 0m;
                cs.SellQty          = 0m;
                cs.TrailingLow      = 0m;
                cs.FeaturesAtSell   = [];
                cs.CooldownBarsLeft = cycleCooldownBars;

                _state.NotifyStrategyStatus(new("Watching", $"Rebuy triggered | dip={dipDepth:P2} bounce={bounceFromLow:P2} RSI={rsi:F1}",
                    AdaptiveSellPct: adaptiveSellPct, AdaptiveAbandonPct: adaptiveAbandon));

                var strength = Math.Round(Math.Clamp((rsiCycleRebuy - rsi + 10) / 30m, 0m, 1m), 4);

                if (priorFeatures.Length > 0)
                    _state.NotifyCycleCompleted(new CycleCompletedEvent
                    {
                        Symbol         = symbol,
                        IsAbandoned    = false,
                        NetEthGain     = rebuyQty - priorSellQty,
                        SellPrice      = priorSellPrice,
                        BuyPrice       = close,
                        FeaturesAtSell = priorFeatures,
                        SellTimestamp  = priorSellTs,
                        CompletedAt    = DateTime.UtcNow
                    });

                return new OrderIntent
                {
                    IntentType             = OrderIntentType.Buy,
                    OrderType              = OrderType.Market,
                    TimeInForce            = _options.Orders.DefaultTimeInForce,
                    Reason                 = $"Cycle rebuy | dip={dipDepth:P2} bounce={bounceFromLow:P2} RSI={rsi:F1} | {ctx}",
                    SignalStrength         = strength,
                    IndicatorContext       = ctx,
                    TargetQuantityOverride = rebuyQty,
                    CycleId                = priorCycleId   // runtime corrects DB with actual fill qty
                };
            }

            // Cycle failed: price went up past adaptive threshold — abandon
            var risePct = close > cs.SellPrice ? (close - cs.SellPrice) / cs.SellPrice : 0m;
            if (risePct > cs.AbandonRisePct)
            {
                _logger.LogWarning(
                    "CYCLE ABANDONED | {Symbol} price rose {Rise:P2} (threshold={Threshold:P2}) — entering recovery watch at ${Sell:F2}",
                    symbol, risePct, cs.AbandonRisePct, cs.SellPrice);

                var abandonFeatures  = cs.FeaturesAtSell;
                var abandonSellPrice = cs.SellPrice;
                var abandonSellQty   = cs.SellQty;
                var abandonSellTs    = cs.SellTimestamp;

                if (cs.OpenCycleId.HasValue && _state.ActiveSessionId is not null)
                    _db.MarkCycleAbandoned(cs.OpenCycleId.Value);

                // Keep shadow state so recovery rebuy can fire if price dips back below sell price
                cs.PostAbandonSellPrice = abandonSellPrice;
                cs.PostAbandonSellQty   = abandonSellQty;
                cs.PostAbandonSellTs    = abandonSellTs;

                cs.ActiveSell       = false;
                cs.OpenCycleId      = null;
                cs.SellPrice        = 0m;
                cs.SellQty          = 0m;
                cs.TrailingLow      = 0m;
                cs.FeaturesAtSell   = [];
                cs.CooldownBarsLeft = cycleCooldownBars;

                if (abandonFeatures.Length > 0)
                    _state.NotifyCycleCompleted(new CycleCompletedEvent
                    {
                        Symbol         = symbol,
                        IsAbandoned    = true,
                        NetEthGain     = 0m,
                        SellPrice      = abandonSellPrice,
                        BuyPrice       = close,
                        FeaturesAtSell = abandonFeatures,
                        SellTimestamp  = abandonSellTs,
                        CompletedAt    = DateTime.UtcNow
                    });

                if (_state.ActiveSessionId is not null)
                    RecheckFeasibility(symbol, rsi);

                _state.NotifyStrategyStatus(new("Watching",
                    $"Cycle abandoned — recovery watch active (sell was ${abandonSellPrice:F2}) | RSI={rsi:F1}",
                    AdaptiveSellPct: adaptiveSellPct, AdaptiveAbandonPct: adaptiveAbandon));
                return OrderIntent.None($"Cycle abandoned | price rose {risePct:P2} | recovery watch at ${abandonSellPrice:F2} | {ctx}");
            }

            _state.NotifyStrategyStatus(new("ActiveSell",
                $"Waiting for rebuy — sold {cs.SellQty:F3} ETH @ ${cs.SellPrice:F2} | dip {dipDepth:P2} / bounce {bounceFromLow:P2}",
                SellPrice:          cs.SellPrice,
                SellQty:            cs.SellQty,
                CurrentDropPct:     dipDepth,
                MinDropPct:         minDrop,
                AdaptiveSellPct:    adaptiveSellPct,
                AdaptiveAbandonPct: cs.AbandonRisePct));
            return OrderIntent.None($"Waiting for rebuy | dip={dipDepth:P2} need={minDrop:P2} bounce={bounceFromLow:P2} min={minBounceFromLow:P2} | {ctx}");
        }

        // ── Post-cycle cooldown ───────────────────────────────────────────────
        if (cs.CooldownBarsLeft > 0) cs.CooldownBarsLeft--;

        // ── Post-abandon recovery rebuy ───────────────────────────────────────
        // When a cycle was abandoned (price spiked up), watch for price to fall
        // back below the original sell price. If it does, fire a full-cash rebuy
        // and record the complete cycle with real P&L instead of letting RSI
        // accumulation nibble back less ETH than was sold.
        if (cs.PostAbandonSellPrice > 0m)
        {
            var feeRate       = _options.TakerFeePercent / 100m;
            var breakEvenDrop = 2m * feeRate;
            var recoveryDip   = (cs.PostAbandonSellPrice - close) / cs.PostAbandonSellPrice;

            // Give up if price has risen another full adaptiveAbandon % above the sell price
            // (roughly double the original abandon threshold). At that point recovery is
            // unlikely and holding cash idle while blocking accumulation is worse.
            var furtherRise = (close - cs.PostAbandonSellPrice) / cs.PostAbandonSellPrice;
            if (furtherRise > adaptiveAbandon)
            {
                _logger.LogInformation(
                    "RECOVERY WATCH EXPIRED | {Symbol} price rose {Rise:P2} further above sell ${Sell:F2} — resuming normal accumulation",
                    symbol, furtherRise, cs.PostAbandonSellPrice);
                cs.PostAbandonSellPrice = 0m;
                cs.PostAbandonSellQty   = 0m;
                cs.PostAbandonSellTs    = default;
                // fall through — RSI accumulation now unblocked
            }
            else
            {
            // Fire when price is below sell by at least 2×fee (guaranteed more ETH back),
            // or RSI is deeply oversold and price is at or below the sell price.
            bool priceDipped        = recoveryDip >= breakEvenDrop;
            bool rsiOversoldAtSell  = rsi < rsiCycleRebuy && recoveryDip >= 0m;

            if (priceDipped || rsiOversoldAtSell)
            {
                var rebuyCash = _portfolio.GetCash();
                var rcvStep   = GetSymbolConfig(symbol)?.QuantityStep ?? 0.001m;
                var rcvQty    = rebuyCash > 0m && close > 0m
                    ? RoundDownToStep(rebuyCash / (close * (1m + feeRate)), rcvStep)
                    : 0m;

                // [SM-014-recovery] Check cash BEFORE clearing shadow state.
                // If cash=0 the original sell never applied — keep PostAbandon watch alive to retry.
                if (rcvQty <= 0m)
                {
                    _logger.LogWarning(
                        "RECOVERY REBUY SKIPPED | {Symbol} rcvQty=0 — cash={Cash:F2} USDT. " +
                        "Keeping PostAbandon watch active (sell may not have applied to portfolio).",
                        symbol, rebuyCash);
                    // Do NOT clear PostAbandonSellPrice — recovery watch stays active.
                    return OrderIntent.None($"Recovery rebuy skipped — no cash | {ctx}");
                }

                var priorSellPrice = cs.PostAbandonSellPrice;
                var priorSellQty   = cs.PostAbandonSellQty;
                var priorSellTs    = cs.PostAbandonSellTs;

                // Only clear shadow state now that we know we have cash to rebuy.
                cs.PostAbandonSellPrice = 0m;
                cs.PostAbandonSellQty   = 0m;
                cs.PostAbandonSellTs    = default;
                cs.CooldownBarsLeft     = cycleCooldownBars;

                if (_state.ActiveSessionId is not null)
                {
                    var netGain = rcvQty - priorSellQty;
                    _logger.LogInformation(
                        "RECOVERY REBUY | {Symbol} SellPrice={Sell:F4} Close={Close:F4} Dip={Dip:P2} RSI={Rsi:F1} Qty={Qty:F5} NetEthGain={Gain:F5}",
                        symbol, priorSellPrice, close, recoveryDip, rsi, rcvQty, netGain);

                    // Insert with estimated qty; runtime corrects with actual fill qty via CycleId
                    var recoveryRowId = _db.InsertCompletedCycle(_state.ActiveSessionId, symbol,
                        priorSellQty, priorSellPrice, priorSellTs,
                        rcvQty, close);

                    RecheckFeasibility(symbol, rsi);

                    _state.NotifyStrategyStatus(new("Watching",
                        $"Recovery rebuy | sell was ${priorSellPrice:F2} buy={close:F2} dip={recoveryDip:P2} net={netGain:+0.00000;-0.00000} ETH",
                        AdaptiveSellPct: adaptiveSellPct, AdaptiveAbandonPct: adaptiveAbandon));

                    return new OrderIntent
                    {
                        IntentType             = OrderIntentType.Buy,
                        OrderType              = OrderType.Market,
                        TimeInForce            = _options.Orders.DefaultTimeInForce,
                        Reason                 = $"Recovery rebuy | sellWas=${priorSellPrice:F2} dip={recoveryDip:P2} RSI={rsi:F1} | {ctx}",
                        SignalStrength         = Math.Round(Math.Clamp((rsiCycleRebuy - rsi + 10) / 30m, 0m, 1m), 4),
                        IndicatorContext       = ctx,
                        TargetQuantityOverride = rcvQty,
                        CycleId                = recoveryRowId  // runtime corrects DB with actual fill qty
                    };
                }
            }
            } // end else (recovery watch still active)
        }

        // ── Cycling sell trigger ──────────────────────────────────────────────
        // RSI peak confirmation + cooldown guard.
        bool sellSignal = _cyclingEnabled && rsi > rsiCycleSell && rsi < rsiPrev
                          && cs.CooldownBarsLeft == 0;

        if (sellSignal && strongUptrend)
        {
            _logger.LogDebug(
                "TREND FILTER | Sell blocked — sustained uptrend RSI={Rsi:F1} spread={Spread:F3}% close={Close:F4} ema50={Ema50:F4}",
                rsi, spreadPct, close, trendEmaVal);
        }
        else if (sellSignal)
        {
            var posQty = _portfolio.GetPositionQuantity(symbol);
            if (posQty > 0m)
            {
                var symbolConfig = GetSymbolConfig(symbol);
                var sellQty = RoundDownToStep(posQty * adaptiveSellPct, symbolConfig?.QuantityStep ?? 0.001m);
                if (sellQty > 0m)
                {
                    // Gate: skip cycling if the sell quantity is too small for NetEthGain to
                    // Effective margin = MinCycleDipPct - MinBounceFromLow - 2*fee
                    // sellQty ≥ step / effectiveMargin to clear step quantization.
                    var step = symbolConfig?.QuantityStep ?? 0.001m;
                    var feeRate = _options.TakerFeePercent / 100m;
                    var effectiveMargin = MinCycleDipPct - minBounceFromLow - 2m * feeRate;
                    var minViableSellQty = step / Math.Max(effectiveMargin, step);

                    if (sellQty < minViableSellQty)
                    {
                        _logger.LogInformation(
                            "CYCLE SELL SKIPPED | {Symbol} sellQty={Qty:F4} < minViable={Min:F4} — position too small for NetEthGain to clear step quantization",
                            symbol, sellQty, minViableSellQty);
                    }
                    else
                    {
                        // Only sell if current price clears the avg entry enough to break even after 2x commission
                        var avgEntry = _portfolio.GetAverageEntryPrice(symbol);
                        var minSellPrice = avgEntry > 0m
                            ? avgEntry * (1m + 2m * _options.TakerFeePercent / 100m)
                            : 0m;

                        if (close < minSellPrice)
                        {
                            _logger.LogDebug(
                                "CYCLE SELL SKIPPED | {Symbol} close={Close:F4} < minSell={Min:F4} (avgEntry={Avg:F4})",
                                symbol, close, minSellPrice, avgEntry);
                        }
                        else
                        {
                            // Trial mode: sell half the normal quantity after a timeout re-enable
                            if (cs.TrialMode)
                            {
                                sellQty = RoundDownToStep(sellQty * 0.5m, symbolConfig?.QuantityStep ?? 0.001m);
                                _logger.LogInformation(
                                    "TRIAL MODE SELL | {Symbol} reduced qty={Qty:F5} (50% of normal) — suspension #{Count}",
                                    symbol, sellQty, cs.SuspensionCount);
                            }

                            _logger.LogInformation(
                                "CYCLE SELL | {Symbol} RSI={Rsi:F1} SellQty={Qty:F5} SellPct={Pct:P0} Price={Price:F4} Abandon>{Abandon:P1} ATR={Atr:F6}",
                                symbol, rsi, sellQty, adaptiveSellPct, close, adaptiveAbandon, atr);

                            // Score all open accumulation lots against this sell price before
                            // resetting state — lets the NN learn whether prior buys were well-timed.
                            _accumTracker.NotifyCycleSell(symbol, close);

                            // New sell supersedes any active recovery watch (cash is now committed to this sell)
                            if (cs.PostAbandonSellPrice > 0m)
                            {
                                _logger.LogInformation("RECOVERY WATCH CANCELLED | new sell cycle starting for {Symbol}", symbol);
                                cs.PostAbandonSellPrice = 0m;
                                cs.PostAbandonSellQty   = 0m;
                                cs.PostAbandonSellTs    = default;
                            }

                            cs.ActiveSell     = true;
                            cs.SellPrice      = close;
                            cs.SellQty        = sellQty;
                            cs.AbandonRisePct = adaptiveAbandon;
                            cs.TrailingLow    = close;
                            cs.SellTimestamp  = DateTime.UtcNow;
                            cs.FeaturesAtSell = MarketFeatureExtractor.ToVector(
                                CaptureSnapshot(symbol, history, rsi, atr, close, macd.Histogram, spreadPct, trendEmaVal));

                            if (_state.ActiveSessionId is not null)
                                cs.OpenCycleId = _db.OpenCycle(_state.ActiveSessionId, symbol, sellQty, close);
                            else
                                // [SM-009] Sell fires with no active session — cycle won't be persisted.
                                // This indicates a lifecycle bug (LaunchRuntime not called, or session not started).
                                _logger.LogError(
                                    "SELL WITHOUT SESSION | {Symbol} ActiveSessionId is null — cycle will not be recorded in DB",
                                    symbol);

                            var breakEvenForStatus = 2m * _options.TakerFeePercent / 100m;
                            var minDrop = Math.Max(breakEvenForStatus * 1.1m, 0.005m);
                            _state.NotifyStrategyStatus(new("ActiveSell",
                                $"Cycle sold {sellQty:F3} ETH @ ${close:F2} ({adaptiveSellPct:P0}) — need {minDrop:P2} drop | abandon >{adaptiveAbandon:P1}",
                                SellPrice:          close,
                                SellQty:            sellQty,
                                CurrentDropPct:     0m,
                                MinDropPct:         minDrop,
                                AdaptiveSellPct:    adaptiveSellPct,
                                AdaptiveAbandonPct: adaptiveAbandon));

                            var strength = Math.Round(Math.Clamp((rsi - rsiCycleSell) / 20m, 0m, 1m), 4);
                            return new OrderIntent
                            {
                                IntentType             = OrderIntentType.Sell,
                                OrderType              = OrderType.Market,
                                TimeInForce            = _options.Orders.DefaultTimeInForce,
                                Reason                 = $"Cycle partial sell | RSI={rsi:F1} sellPct={adaptiveSellPct:P0} abandon>{adaptiveAbandon:P1} avgEntry={avgEntry:F4} | {ctx}",
                                SignalStrength         = strength,
                                IndicatorContext       = ctx,
                                TargetQuantityOverride = sellQty,
                                CycleId                = cs.OpenCycleId   // [DI-002] Allows runtime to clean up the DB row on fill rejection
                            };
                        }
                    }
                }
            }
        } // end else if (sellSignal)

        // ── Normal BuildEth accumulation ──────────────────────────────────────
        // Graduated cycling re-enable:
        //   Primary  — RSI < cyclingReenableRsi (deep dip, immediate full re-enable)
        //   Tier 1   — 3h elapsed + (RSI < 50 OR volatile ATR): soft re-enable
        //   Tier 2   — 9h elapsed: trial mode re-enable (50 % sell qty)
        //   Tier 3   — 24h elapsed: force full re-enable regardless
        // Data basis: observed max inter-cycle gap is 21h; median ~5h.
        if (!_cyclingEnabled)
        {
            var suspendedHours = cs.SuspendedAt > DateTime.MinValue
                ? (DateTime.UtcNow - cs.SuspendedAt).TotalHours
                : 0d;
            var atrPct = close > 0m ? atr / close : 0m;
            var marketIsVolatile = atrPct > ReferenceAtrPct * 1.5m;

            bool primaryReEnable = rsi < cyclingReenableRsi;
            bool tier1ReEnable   = suspendedHours >= 3d  && (rsi < 50m || marketIsVolatile);
            bool tier2ReEnable   = suspendedHours >= 9d;
            bool tier3ReEnable   = suspendedHours >= 24d;

            if (primaryReEnable || tier1ReEnable || tier2ReEnable || tier3ReEnable)
            {
                _cyclingEnabled   = true;
                cs.SuspendedAt    = DateTime.MinValue;   // reset so next suspension times correctly
                // Only Tier 2 and Tier 3 use trial mode — Tier 1 is a condition-based full re-enable
                cs.TrialMode      = tier2ReEnable || tier3ReEnable;

                var reason = tier3ReEnable   ? $"24h hard timeout (suspension #{cs.SuspensionCount})" :
                             tier2ReEnable   ? $"9h timeout — trial mode" :
                             tier1ReEnable   ? $"3h timeout — RSI={rsi:F1} volatile={marketIsVolatile}" :
                                              $"RSI={rsi:F1} deep dip";

                _logger.LogInformation(
                    "CYCLING RE-ENABLED | {Symbol} reason={Reason} trial={Trial} suspensions={Count}",
                    symbol, reason, cs.TrialMode, cs.SuspensionCount);

                if (cs.SuspensionCount >= 3)
                    _logger.LogWarning(
                        "CYCLING REPEATEDLY SUSPENDED | {Symbol} {Count} consecutive suspensions — strategy may need review",
                        symbol, cs.SuspensionCount);

                PushCyclingState(symbol);
            }
        }

        var accumStep = GetSymbolConfig(symbol)?.QuantityStep ?? 0.001m;
        var hasSufficientCash = _portfolio.GetCash() >= accumStep * close * (1m + _options.TakerFeePercent / 100m);

        // Guard: hold cash for recovery rebuy while in post-abandon watch mode
        if (hasSufficientCash && rsi < rsiDipBuy && cs.PostAbandonSellPrice == 0m)
        {
            _state.NotifyStrategyStatus(new("Watching", $"RSI dip buy signal | RSI={rsi:F1}",
                AdaptiveSellPct: adaptiveSellPct, AdaptiveAbandonPct: adaptiveAbandon));
            _accumTracker.RecordBuy(symbol,
                MarketFeatureExtractor.ToVector(
                    CaptureSnapshot(symbol, history, rsi, atr, close, macd.Histogram, spreadPct, trendEmaVal)),
                close, "RsiDip");
            var strength = Math.Round(Math.Clamp((rsiDipBuy - rsi) / rsiDipBuy, 0m, 1m), 4);
            return OrderIntent.MarketBuy(
                $"RSI dip accumulation | RSI={rsi:F1} | {ctx}",
                signalStrength: strength, indicatorContext: ctx);
        }

        bool bullCross = fastEmaPrev <= slowEmaPrev && fastEma > slowEma;
        if (hasSufficientCash && bullCross && rsi < rsiCrossoverMax && macd.Histogram > 0m && cs.PostAbandonSellPrice == 0m)
        {
            _state.NotifyStrategyStatus(new("Watching", $"EMA cross buy signal | RSI={rsi:F1}",
                AdaptiveSellPct: adaptiveSellPct, AdaptiveAbandonPct: adaptiveAbandon));
            _accumTracker.RecordBuy(symbol,
                MarketFeatureExtractor.ToVector(
                    CaptureSnapshot(symbol, history, rsi, atr, close, macd.Histogram, spreadPct, trendEmaVal)),
                close, "EmaCross");
            var strength = Math.Round(
                (Math.Clamp((rsiCrossoverMax - rsi) / rsiCrossoverMax, 0m, 1m) +
                 Math.Clamp(spreadPct / 1.5m, 0m, 1m)) / 2m, 4);

            return OrderIntent.MarketBuy(
                $"EMA{FastEma}/EMA{SlowEma} bull cross | RSI={rsi:F1} | {ctx}",
                signalStrength: strength, indicatorContext: ctx);
        }

        var idlePhase = !_cyclingEnabled         ? "CyclingSuspended"
                      : cs.CooldownBarsLeft > 0  ? "Cooldown"
                      : strongUptrend            ? "TrendFilter"
                      :                            "Watching";

        var idleSummary = cs.PostAbandonSellPrice > 0m
            ? $"Recovery watch — price must dip below ${cs.PostAbandonSellPrice:F2} | current={close:F2} | RSI={rsi:F1}"
            : idlePhase switch
            {
                "Cooldown"         => $"Cooldown — {cs.CooldownBarsLeft} bars remaining | RSI={rsi:F1}",
                "TrendFilter"      => $"Trend filter ON — cycle sells paused | RSI={rsi:F1} spread={spreadPct:F2}% ema50={trendEmaVal:F2}",
                "CyclingSuspended" => cs.SuspendedAt > DateTime.MinValue
                    ? $"Cycling suspended #{cs.SuspensionCount} ({(DateTime.UtcNow - cs.SuspendedAt).TotalHours:F1}h) — RSI={rsi:F1} | primary <{cyclingReenableRsi:F0} | tier1 3h+RSI<50 | tier2 9h | hard 24h"
                    : $"Cycling suspended — RSI={rsi:F1} | re-enables at RSI <{cyclingReenableRsi:F0}",
                _                  => $"Watching — RSI={rsi:F1} | cycle sell >{rsiCycleSell:F0}, dip buy <{rsiDipBuy:F0}, EMA cross"
            };

        _state.NotifyStrategyStatus(new(idlePhase, idleSummary,
            AdaptiveSellPct: adaptiveSellPct, AdaptiveAbandonPct: adaptiveAbandon));
        return OrderIntent.None($"No signal | cycling={_cyclingEnabled} trend={strongUptrend} | {ctx}");
    }

    private void CompleteCycleAndCheckFeasibility(string symbol, int cycleId, decimal buyPrice, decimal rsi)
    {
        // Estimate bought quantity from current available cash / price
        var cash = _portfolio.GetCash();
        var symbolConfig = GetSymbolConfig(symbol);
        var step = symbolConfig?.QuantityStep ?? 0.001m;
        var estimatedBuyQty = cash > 0m && buyPrice > 0m
            ? RoundDownToStep(cash / buyPrice, step)
            : 0m;

        if (estimatedBuyQty > 0m)
            _db.CloseCycle(cycleId, estimatedBuyQty, buyPrice);

        RecheckFeasibility(symbol, rsi);
    }

    private void RecheckFeasibility(string symbol, decimal rsi)
    {
        if (_state.ActiveSessionId is null) return;

        var cycles = _db.GetRecentCompleteCycles(_state.ActiveSessionId, limit: 5);
        if (cycles.Count >= 3 && _cycleState.TryGetValue(symbol, out var cs))
        {
            // Abandoned cycles represent opportunity cost, not actual ETH loss, so they
            // don't signal deteriorating conditions — only loss-completed cycles count.
            var failures = cycles.Count(c => !c.IsAbandoned && !c.IsProfit);

            if (failures >= 3 && _cyclingEnabled)
            {
                _cyclingEnabled    = false;
                cs.SuspendedAt     = DateTime.UtcNow;
                cs.SuspensionCount++;
                _logger.LogWarning(
                    "CYCLING SUSPENDED | {Failures}/{Total} recent cycles ended in ETH loss | suspension #{Count}",
                    failures, cycles.Count, cs.SuspensionCount);
            }
            else if (_cyclingEnabled && cycles.Any(c => c.IsProfit))
            {
                // Profitable cycle — exit trial mode and reset suspension counter regardless
                // of whether we came back via primary (RSI) or timed re-enable
                if (cs.TrialMode)
                {
                    cs.TrialMode = false;
                    _logger.LogInformation(
                        "TRIAL MODE CLEARED | {Symbol} cycle succeeded — resuming normal sell sizes", symbol);
                }
                cs.SuspensionCount = 0;
            }
        }

        PushCyclingState(symbol);
    }

    private void PushCyclingState(string symbol)
    {
        if (_state.ActiveSessionId is null) return;
        var cycles = _db.GetRecentCompleteCycles(_state.ActiveSessionId, limit: 10);
        _state.NotifyCyclingUpdate(_cyclingEnabled, cycles);
    }

    private SymbolConfig? GetSymbolConfig(string symbol) =>
        _options.Symbols.FirstOrDefault(s =>
            string.Equals(s.Symbol, symbol, StringComparison.OrdinalIgnoreCase));

    private static decimal RoundDownToStep(decimal value, decimal step) =>
        step > 0m ? Math.Floor(value / step) * step : value;

    /// <summary>
    /// Aggregates raw 10-second candles into higher-timeframe close prices.
    /// Emits the close of each COMPLETED period bucket; the current in-progress
    /// bucket is excluded so only fully-closed bars reach the indicator.
    /// </summary>
    private static IReadOnlyList<decimal> AggregateCloses(
        IReadOnlyList<Candle> history, TimeSpan period)
    {
        if (history.Count == 0) return Array.Empty<decimal>();

        var result   = new List<decimal>(history.Count / 6);
        var bucketTs = FloorTs(history[0].Timestamp, period);
        var last     = history[0].Close;

        for (var i = 1; i < history.Count; i++)
        {
            var ts = FloorTs(history[i].Timestamp, period);
            if (ts != bucketTs)
            {
                result.Add(last);   // previous bucket is now complete
                bucketTs = ts;
            }
            last = history[i].Close;
        }
        // `last` is the close of the current in-progress bucket — intentionally excluded
        return result;
    }

    private static DateTime FloorTs(DateTime dt, TimeSpan period) =>
        new(dt.Ticks / period.Ticks * period.Ticks, dt.Kind);

    private (decimal SellPct, decimal AbandonRise) ComputeAdaptiveParams(
        decimal atr, decimal close,
        decimal minSellPct, decimal maxSellPct, decimal defaultSellPct,
        decimal minAbandonRise, decimal maxAbandonRise)
    {
        var atrPct = close > 0m && atr > 0m ? atr / close : 0m;

        // Sell fraction: inverse of volatility — flat market needs larger sell to lower break-even
        var sellPct = atrPct > 0m
            ? Math.Clamp(defaultSellPct * ReferenceAtrPct / atrPct, minSellPct, maxSellPct)
            : defaultSellPct;

        // Abandon threshold: volatile = bigger swings normal = wait longer before bailing
        var abandonRise = atrPct > 0m
            ? Math.Clamp(atrPct * AbandonAtrScale, minAbandonRise, maxAbandonRise)
            : minAbandonRise + (maxAbandonRise - minAbandonRise) / 2m;

        return (sellPct, abandonRise);
    }

    /// <summary>
    /// Builds a MarketSnapshot from the values already computed in the current bar.
    /// Called at cycle sell time so the conditions that triggered the sell can be
    /// stored alongside the eventual cycle outcome for NN training.
    /// </summary>
    private MarketSnapshot CaptureSnapshot(
        string symbol, IReadOnlyList<Candle> history,
        decimal rsi, decimal atr, decimal close, decimal macdHistogram,
        decimal spreadPct, decimal trendEmaVal)
    {
        var cash        = _portfolio.GetCash();
        var ethQty      = _portfolio.GetPositionQuantity(symbol);
        var ethValue    = ethQty * close;
        var totalEquity = cash + ethValue;

        decimal momentum5 = 0m;
        if (history.Count >= 6)
        {
            var prev = history[^6].Close;
            if (prev > 0m) momentum5 = (close - prev) / prev;
        }

        decimal successRate = 0m;
        if (_state.ActiveSessionId is not null)
        {
            var recent = _db.GetRecentCompleteCycles(_state.ActiveSessionId, 5);
            if (recent.Count > 0)
                successRate = (decimal)recent.Count(c => c.IsProfit) / recent.Count;
        }

        return new MarketSnapshot
        {
            Timestamp          = DateTime.UtcNow,
            Symbol             = symbol,
            Close              = close,
            Rsi1m              = rsi,
            AtrPct             = close > 0m ? atr / close : 0m,
            MacdHistogram      = macdHistogram,
            EmaSpreadPct       = spreadPct,
            PriceMomentum5     = momentum5,
            CashPct            = totalEquity > 0m ? cash / totalEquity : 0m,
            EthPositionPct     = totalEquity > 0m ? ethValue / totalEquity : 0m,
            CycleSuccessRate   = successRate,
            AboveTrendEma      = close > trendEmaVal,
            CyclingEnabled     = _cyclingEnabled,
            BarsSinceLastTrade = 0  // populated in Sprint 2 via risk manager
        };
    }

    private class CycleState
    {
        public bool     ActiveSell       { get; set; }
        public decimal  SellPrice        { get; set; }
        public decimal  SellQty          { get; set; }
        public int?     OpenCycleId      { get; set; }
        public decimal  AbandonRisePct   { get; set; } = 0.03m;
        public decimal  TrailingLow      { get; set; }
        public int      CooldownBarsLeft { get; set; }
        public DateTime SellTimestamp    { get; set; }
        public float[]  FeaturesAtSell   { get; set; } = [];

        // Post-abandon recovery: price to watch for a full-cash rebuy after abandonment
        public decimal  PostAbandonSellPrice { get; set; }
        public decimal  PostAbandonSellQty   { get; set; }
        public DateTime PostAbandonSellTs    { get; set; }

        // Suspension circuit-breaker state
        public DateTime SuspendedAt       { get; set; }  // when cycling was last suspended
        public int      SuspensionCount   { get; set; }  // consecutive suspensions without a win
        public bool     TrialMode         { get; set; }  // sell at 50% qty after timeout re-enable
    }
}
