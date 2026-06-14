using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PaperTradingBot.Config;
using PaperTradingBot.Interfaces;

namespace PaperTradingBot.Services;

/// <summary>
/// Backtest replay: feeds historical 10s candles through the SAME BarProcessor the live
/// paper bot uses (so results are faithful), strictly sequentially (correct ordering), and
/// prints the edge decomposition (EV per cycle) at the end. Runs in a sandbox — isolated
/// DB + frozen settings + bandit/advisor dormant — configured by Program's backtest branch.
/// </summary>
public sealed class ReplayRuntime : ITradingRuntime
{
    private readonly BotOptions             _options;
    private readonly ILogger<ReplayRuntime> _logger;
    private readonly IMarketDataFeed         _marketDataFeed;
    private readonly BarProcessor            _barProcessor;
    private readonly IPortfolioStateStore    _portfolio;
    private readonly ISessionResultRecorder  _recorder;
    private readonly IBarDecisionPipeline    _pipeline;
    private readonly ILocalPaperExecutionGateway _gateway;
    private readonly DatabaseService         _db;
    private readonly BotStateService         _botState;

    public string Name => "Replay";

    public ReplayRuntime(
        IOptions<BotOptions> options,
        ILogger<ReplayRuntime> logger,
        IMarketDataFeed marketDataFeed,
        BarProcessor barProcessor,
        IPortfolioStateStore portfolio,
        ISessionResultRecorder recorder,
        IBarDecisionPipeline pipeline,
        ILocalPaperExecutionGateway gateway,
        DatabaseService db,
        BotStateService botState)
    {
        _options        = options.Value;
        _logger         = logger;
        _marketDataFeed = marketDataFeed;
        _barProcessor   = barProcessor;
        _portfolio      = portfolio;
        _recorder       = recorder;
        _pipeline       = pipeline;
        _gateway        = gateway;
        _db             = db;
        _botState       = botState;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var symbol     = _options.Symbols.First().Symbol;
        var startQty   = _options.StartingQuantity;
        var ccy        = symbol.Replace("USDT", "");

        var series = _marketDataFeed.GetCandlesBySymbol();
        if (!series.TryGetValue(symbol, out var candles) || candles.Count == 0)
        {
            _logger.LogError("No candles loaded for {Symbol}. Check FilePath.", symbol);
            return;
        }

        var sessionId = _db.StartSession("BuildEthCycling", 0m, startQty);
        // Set ActiveSessionId exactly as the live path does — the strategy's cycle-DB lifecycle
        // (mark-abandoned, settle, suspension reads) is guarded on _state.ActiveSessionId.
        _botState.NotifyStarted("BuildEthCycling", sessionId, 0m, startQty, symbol);
        _portfolio.Reset(0m);
        _recorder.Reset(0m);
        _pipeline.Reset(new[] { symbol });

        var session = new BarSession
        {
            Gateway            = _gateway,
            SessionId          = sessionId,
            LastPriceBySymbol  = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase),
            PositionSeeded     = false,
            SessionStartingEth = startQty,
            PersistEquityPoints = false   // backtest: skip 190k/run equity inserts (unused for the edge report)
        };

        Console.WriteLine(
            $"REPLAY START | {symbol} | {candles.Count:N0} bars | {candles[0].Timestamp:u} … {candles[^1].Timestamp:u} | " +
            $"start={startQty} {ccy} | slippage={_options.SlippagePercent:P3} fee={_options.TakerFeePercent / 100m:P3}");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var n = 0;
        foreach (var candle in candles)
        {
            if (cancellationToken.IsCancellationRequested) break;
            await _barProcessor.ProcessBarAsync(candle, symbol, session, cancellationToken);
            if (++n % 20000 == 0)
                Console.WriteLine($"  … {n:N0}/{candles.Count:N0} bars");
        }
        sw.Stop();

        var lastClose = candles[^1].Close;
        session.LastPriceBySymbol[symbol] = lastClose;
        var finalQty   = _portfolio.GetPositionQuantity(symbol);
        var finalCash  = _portfolio.GetCash();
        _db.EndSession(sessionId, _portfolio.GetTotalEquity(session.LastPriceBySymbol));

        PrintEdgeReport(sessionId, symbol, ccy, startQty, finalQty, finalCash, lastClose, candles.Count, sw.Elapsed);
    }

    private void PrintEdgeReport(
        string sessionId, string symbol, string ccy, decimal startQty,
        decimal finalQty, decimal finalCash, decimal lastClose, int bars, TimeSpan elapsed)
    {
        // Every completed cycle for this session (includes abandons; abandons are real coin losses).
        var cycles    = _db.GetRecentCompleteCycles(sessionId, int.MaxValue);
        var settled   = cycles.Where(c => c.IsAbandoned || c.NetEthGain != 0m).ToList();
        var wins      = settled.Where(c => !c.IsAbandoned && c.NetEthGain > 0m).ToList();
        var losses    = settled.Where(c => !c.IsAbandoned && c.NetEthGain < 0m).ToList();
        var abandons  = settled.Where(c => c.IsAbandoned).ToList();

        var winRate     = settled.Count > 0 ? (decimal)wins.Count / settled.Count : 0m;
        var avgWin      = wins.Count     > 0 ? wins.Average(c => c.NetEthGain)     : 0m;
        var avgAbandon  = abandons.Count > 0 ? abandons.Average(c => c.NetEthGain) : 0m;
        var evPerCycle  = settled.Count  > 0 ? settled.Average(c => c.NetEthGain)  : 0m;
        var cycleSum    = settled.Sum(c => c.NetEthGain);

        // Coin-equivalent truth: held coin PLUS parked cash converted at last price. A run that
        // ends mid-cycle holds the sold fraction as cash — counting only finalQty would massively
        // overstate the loss (e.g. BTC uptrend looked −51% but is ~−3% once cash is included).
        var cashAsCoin  = lastClose > 0m ? finalCash / lastClose : 0m;
        var netCoin     = (finalQty + cashAsCoin) - startQty;
        var netPosOnly  = finalQty - startQty;

        Console.WriteLine();
        Console.WriteLine("══════════════════════════════════════════════════════════════");
        Console.WriteLine($"  EDGE REPORT — {symbol}   ({bars:N0} bars, replayed in {elapsed.TotalSeconds:F1}s)");
        Console.WriteLine("══════════════════════════════════════════════════════════════");
        Console.WriteLine($"  Cycles (settled) : {settled.Count}   wins {wins.Count} / losses {losses.Count} / abandons {abandons.Count}");
        Console.WriteLine($"  Win rate         : {winRate:P1}   (abandons counted as losses)");
        Console.WriteLine($"  Avg win          : {avgWin:+0.000000;-0.000000} {ccy}");
        Console.WriteLine($"  Avg abandon      : {avgAbandon:+0.000000;-0.000000} {ccy}");
        Console.WriteLine($"  ► EV / cycle     : {evPerCycle:+0.000000;-0.000000} {ccy}   ◄ the edge");
        Console.WriteLine($"  Σ cycle coin     : {cycleSum:+0.000000;-0.000000} {ccy}");
        Console.WriteLine("  ──────────────────────────────────────────────────────────");
        Console.WriteLine($"  Start held       : {startQty:0.000000} {ccy}");
        Console.WriteLine($"  Final held       : {finalQty:0.000000} {ccy}   (+ ${finalCash:0.00} cash ≈ {cashAsCoin:0.000000} {ccy})");
        Console.WriteLine($"  Net (pos only)   : {netPosOnly:+0.000000;-0.000000} {ccy}   (mid-cycle cash NOT counted)");
        Console.WriteLine($"  ► Net coin-equiv : {netCoin:+0.000000;-0.000000} {ccy}   ({(startQty > 0 ? netCoin / startQty : 0m):+0.00%;-0.00%})   ◄ includes parked cash");
        Console.WriteLine("══════════════════════════════════════════════════════════════");
        _logger.LogInformation("REPLAY DONE | session={Session} EV/cycle={EV:+0.000000;-0.000000} {Ccy} netCoin={Net:+0.000000;-0.000000}",
            sessionId, evPerCycle, ccy, netCoin);
    }
}
