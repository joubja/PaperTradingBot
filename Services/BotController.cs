using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PaperTradingBot.AI;
using PaperTradingBot.Config;
using PaperTradingBot.Interfaces;
using PaperTradingBot.Models;

namespace PaperTradingBot.Services;

/// <summary>
/// Controls the bot lifecycle. Fresh starts read the real wallet balance as the ETH seed
/// (with config StartingEth as fallback if the API is unavailable). Resumes rebuild from
/// persisted trades using the ETH recorded when the session was originally created.
///
/// Lifecycle contract:
///   Stop             = pause. Next Start resumes the same session.
///   Reset            = archive current + all old stopped/crashed sessions, auto-restart fresh from wallet.
///   Resync to Wallet = same as Reset but uses the wallet balance already confirmed on screen.
///   Crash / Deploy   = MarkCrashedSessions → auto-resumes on next startup.
/// </summary>
public class BotController
{
    private readonly IServiceProvider          _sp;
    private readonly BotStateService           _state;
    private readonly DatabaseService           _db;
    private readonly PerformanceTracker        _perf;
    private readonly IBarDecisionPipeline      _pipeline;
    private readonly IPortfolioStateStore      _portfolio;
    private readonly ISessionResultRecorder    _recorder;
    private readonly BinanceAccountService     _wallet;
    private readonly BotOptions                _options;
    private readonly ILogger<BotController>    _logger;

    private CancellationTokenSource? _cts;
    private Task?                    _botTask;

    private string PrimarySymbol => _options.Symbols.FirstOrDefault()?.Symbol ?? "ETHUSDT";
    private LiveDemoRuntime?         _activeRuntime;
    private readonly SemaphoreSlim   _guard = new(1, 1);

    public bool IsRunning => _botTask is { IsCompleted: false };

    public BotController(
        IServiceProvider       sp,
        BotStateService        state,
        DatabaseService        db,
        PerformanceTracker     perf,
        IBarDecisionPipeline   pipeline,
        IPortfolioStateStore   portfolio,
        ISessionResultRecorder recorder,
        BinanceAccountService  wallet,
        IOptions<BotOptions>   options,
        ILogger<BotController> logger)
    {
        _sp        = sp;
        _state     = state;
        _db        = db;
        _perf      = perf;
        _pipeline  = pipeline;
        _portfolio = portfolio;
        _recorder  = recorder;
        _wallet    = wallet;
        _options   = options.Value;
        _logger    = logger;
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// BotAutoStartService with AutoResumeOnCrash=false — genuinely fresh, no resume checks.
    /// </summary>
    public async Task StartAsync(string strategyName)
    {
        await _guard.WaitAsync();
        try
        {
            if (IsRunning) return;
            await StartCoreAsync(strategyName);
        }
        finally { _guard.Release(); }
    }

    /// <summary>
    /// Tries to resume a crashed / stopped session before falling back to fresh start.
    /// Priority: Crashed → Stopped+open-cycle → Any-stopped → Fresh from wallet.
    /// </summary>
    public async Task ResumeOrStartAsync(string strategyName)
    {
        await _guard.WaitAsync();
        try
        {
            if (IsRunning) return;

            var crashed = _db.GetLastCrashedSession(strategyName);
            if (crashed != null)
            {
                ResumeCore(crashed.Id, strategyName, (decimal)crashed.StartingEth);
                return;
            }

            var withOpenCycle = _db.GetLastSessionWithOpenCycle(strategyName);
            if (withOpenCycle != null)
            {
                ResumeCore(withOpenCycle.Id, strategyName, (decimal)withOpenCycle.StartingEth);
                return;
            }

            // Stop means "pause" — resume the same session on next Start.
            var stopped = _db.GetLastStoppedSession(strategyName);
            if (stopped != null)
            {
                ResumeCore(stopped.Id, strategyName, (decimal)stopped.StartingEth);
                return;
            }

            await StartCoreAsync(strategyName);
        }
        finally { _guard.Release(); }
    }

    /// <summary>Pause the bot — session stays Stopped and will be resumed on next Start.</summary>
    public async Task StopAsync()
    {
        await _guard.WaitAsync();
        try
        {
            if (!IsRunning) return;
            _cts?.Cancel();
            if (_botTask != null)
                try { await _botTask; } catch { }
        }
        finally { _guard.Release(); }
    }

    /// <summary>
    /// Archive the current session and all old stopped/crashed sessions, then
    /// immediately auto-start a fresh session from the real wallet balance.
    /// This is the full Reset: stop → clean slate → restart.
    /// </summary>
    public async Task StopAndResetAsync()
    {
        var strategy = _state.ActiveStrategy;
        await ArchiveAndRestartAsync(string.IsNullOrEmpty(strategy) ? _options.Runtime.StrategyName : strategy);
    }

    /// <summary>
    /// Archive current + all old sessions, then immediately start fresh.
    /// If walletEth is provided (value already confirmed on screen), uses it directly.
    /// Otherwise reads the wallet via API.
    /// </summary>
    public async Task ArchiveAndRestartAsync(string strategyName, decimal? walletEth = null)
    {
        await _guard.WaitAsync();
        try
        {
            // Stop running bot and force-rebuy any open cycle first
            if (IsRunning)
            {
                if (_activeRuntime != null)
                    try { await _activeRuntime.ForceRebuyCyclesAsync(); }
                    catch (Exception ex)
                    {
                        // [OBS-007] Do not silently swallow — orphaned open cycle rows will persist in DB.
                        _logger.LogError(ex,
                            "FORCE REBUY FAILED | Open cycle rows may remain unclosed. " +
                            "Proceeding with archive — check DB for IsComplete=0 rows.");
                    }

                _cts?.Cancel();
                if (_botTask != null)
                    try { await _botTask; } catch { }
            }

            // Archive current session
            if (_state.ActiveSessionId != null)
                _db.UpdateSessionStatus(_state.ActiveSessionId, "Archived");

            // Archive ALL old stopped+crashed sessions so none can be accidentally resumed
            if (!string.IsNullOrEmpty(strategyName))
                _db.ArchiveAllStoppedSessionsForStrategy(strategyName);

            // Start fresh — use confirmed wallet value if provided, else fetch from API
            if (walletEth.HasValue && walletEth.Value > 0m)
                StartCoreWithEth(strategyName, walletEth.Value);
            else
                await StartCoreAsync(strategyName);
        }
        finally { _guard.Release(); }
    }

    // ── Private core ─────────────────────────────────────────────────────────

    private async Task StartCoreAsync(string strategyName)
    {
        var snapshot = await _wallet.GetBalancesAsync();
        decimal startingEth;

        if (snapshot.Success && snapshot.BaseFree > 0m)
        {
            startingEth = snapshot.BaseFree;
            _logger.LogInformation("WALLET | Starting fresh with real balance: {Qty:F5} {Currency}", startingEth, snapshot.BaseCurrency);
        }
        else if (!snapshot.Success && !_options.Runtime.LocalPaperExecutionOnly)
        {
            // [WALLET-GUARD] API unreachable — refuse to start for testnet/live bots.
            // A config fallback risks trading with the wrong seed when the exchange is down.
            // HALT and require manual restart after the Binance API recovers.
            _logger.LogCritical(
                "WALLET | Balance check failed ({Error}) — LocalPaperExecutionOnly=false. " +
                "REFUSING to start: real-order execution requires a confirmed balance. " +
                "Restart the service once the Binance API is reachable.",
                snapshot.Error ?? "unknown");
            return;
        }
        else
        {
            // Either: paper-only mode, OR exchange is reachable but 0 balance in this currency
            // (e.g. BTC bot on a testnet account that has no BTC). Fall back to config.
            startingEth = _options.StartingQuantity;
            var reason  = snapshot.Success
                ? $"no {snapshot.BaseCurrency} balance in testnet wallet"
                : $"balance check failed: {snapshot.Error ?? "unknown"}";
            _logger.LogWarning(
                "WALLET | Using config StartingQuantity={Qty:F5} {Currency} ({Reason})",
                startingEth, snapshot.BaseCurrency, reason);
        }

        StartCoreWithEth(strategyName, startingEth);
    }

    private void StartCoreWithEth(string strategyName, decimal startingEth)
    {
        var strategy = _sp.GetRequiredKeyedService<IOrderIntentProvider>(strategyName);
        if (strategy is BuildEthCyclingOrderIntentProvider cycling)
            cycling.ResetSessionState();
        _pipeline.SetStrategy(strategy);

        _portfolio.Reset(_options.StartingCash);

        // [H-12] Do NOT seed ETH here with avgEntry=0 — that taints the sell-price break-even check
        // and overstates PnL. ProcessClosedBarAsync seeds from the real first-bar market price instead.

        _recorder.Reset(_options.StartingCash);
        _pipeline.Reset(_options.Symbols.Select(s => s.Symbol));

        var sessionId = _db.StartSession(strategyName, _options.StartingCash, startingEth);
        LaunchRuntime(sessionId, strategyName, startingEth, resumeMode: false);
    }

    private void ResumeCore(string sessionId, string strategyName, decimal startingEth)
    {
        _portfolio.Reset(_options.StartingCash);
        if (startingEth > 0m)
            _portfolio.SeedPosition(PrimarySymbol, startingEth, 0m);

        var trades = _db.GetSessionTrades(sessionId);
        foreach (var trade in trades)
        {
            if (trade.Side == TradeSide.Buy)
                _portfolio.TryApplyBuyFill(trade.Symbol, trade.Quantity, trade.Price, trade.Commission, out _);
            else
                _portfolio.TryApplySellFill(trade.Symbol, trade.Quantity, trade.Price, trade.Commission, out _, out _);
        }

        var strategy = _sp.GetRequiredKeyedService<IOrderIntentProvider>(strategyName);
        if (strategy is BuildEthCyclingOrderIntentProvider cycling)
        {
            cycling.ResetSessionState();
            cycling.RestoreFromSession(sessionId, _db);

            // [RESUME-GUARD] Detect the "phantom sell" scenario (Trace 1 / DI-001):
            // An open cycle in DB means the sell fired in GetIntent(), but if no sell trade
            // exists in the Trades table, the sell never applied to the portfolio.
            // The crash window was between ApplyFillAndRecordTrade and InsertTrade (now fixed by C-3,
            // but may still occur for sessions that crashed before C-3 was deployed).
            //
            // Symptoms: cs.ActiveSell=true, portfolio.Cash ≈ StartingCash (no sell credit),
            // but DB has an open cycle row. If left uncorrected the rebuy path would buy
            // using ALL cash (not just the sell proceeds), creating a phantom profit record.
            var openCycles = _db.GetOpenCyclesForSession(sessionId);
            foreach (var oc in openCycles)
            {
                var hasSellTrade = trades.Any(t =>
                    t.Side == TradeSide.Sell &&
                    string.Equals(t.Symbol, oc.Symbol, StringComparison.OrdinalIgnoreCase));

                if (!hasSellTrade)
                {
                    _logger.LogCritical(
                        "RESUME INCONSISTENCY | Open cycle {CycleId} for {Symbol} (sell @ {Price:F2}) " +
                        "has NO corresponding sell trade in DB — sell did not apply to portfolio. " +
                        "Marking cycle abandoned to prevent phantom rebuy with wrong cash amount.",
                        oc.Id, oc.Symbol, oc.SellPrice);

                    _db.MarkCycleAbandoned(oc.Id);

                    // Clear the ActiveSell that RestoreFromSession just set — the cycle is abandoned.
                    cycling.ResetSessionState();
                }
            }
        }
        _pipeline.SetStrategy(strategy);

        _recorder.Reset(_options.StartingCash);
        _pipeline.Reset(_options.Symbols.Select(s => s.Symbol));

        _db.ResumeSession(sessionId);
        LaunchRuntime(sessionId, strategyName, startingEth, resumeMode: true);

        // Seed dashboard with recent history — re-use the already-loaded trades list
        foreach (var t in trades.TakeLast(20))
            _state.NotifyTrade(t);
        _state.NotifyCyclingUpdate(true, _db.GetRecentCompleteCycles(sessionId, limit: 20));
    }

    private void LaunchRuntime(string sessionId, string strategyName, decimal startingEth, bool resumeMode)
    {
        var recentCycles = _db.GetRecentCompletedCyclesAllSessions(30);
        _perf.Seed(recentCycles);
        _state.NotifyStarted(strategyName, sessionId, _options.StartingCash, startingEth, PrimarySymbol);
        if (!resumeMode)
            _state.NotifyCyclingUpdate(true, _db.GetRecentCompleteCycles(sessionId, limit: 20));
        // [C-4-partial] Dispose previous CTS before replacing — prevents kernel handle leak across stop/start cycles.
        _cts?.Dispose();
        _cts           = new CancellationTokenSource();
        _activeRuntime = _sp.GetRequiredService<LiveDemoRuntime>();
        _activeRuntime.SetSessionId(sessionId);
        _activeRuntime.SetStartingEth(startingEth);
        if (resumeMode) _activeRuntime.SetResumeMode();
        _botTask = Task.Run(() => _activeRuntime.RunAsync(_cts.Token));
    }
}
