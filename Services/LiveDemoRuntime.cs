using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PaperTradingBot.Config;
using PaperTradingBot.Interfaces;
using PaperTradingBot.Models;

namespace PaperTradingBot.Services;

public class LiveDemoRuntime : ITradingRuntime
{
    private readonly BotOptions _options;
    private readonly ILogger<LiveDemoRuntime> _logger;
    private readonly ITradingProviderFactory _providerFactory;
    private readonly IBarAggregationService _barAggregationService;
    private ILocalPaperExecutionGateway? _executionGateway;
    private readonly IBarDecisionPipeline _barDecisionPipeline;
    private readonly IRiskManager _riskManager;
    private readonly IPositionSizer _positionSizer;
    private readonly IOrderValidationService _orderValidationService;
    private readonly AnalyticsService _analyticsService;
    private readonly ISessionResultRecorder _sessionResultRecorder;
    private readonly IPortfolioStateStore _portfolioStateStore;

    private readonly BotStateService _botState;
    private readonly DatabaseService _db;

    private ILiveMarketDataFeed? _marketDataFeed;
    private readonly SemaphoreSlim _stateLock = new(1, 1);
    private string _sessionId = "";

    private readonly Dictionary<string, decimal> _lastPriceBySymbol =
        new(StringComparer.OrdinalIgnoreCase);

    private bool    _positionSeeded;
    private bool    _resumeMode;
    private decimal _sessionStartingEth;

    public string Name => "LiveDemo";

    /// <summary>Timestamp of the last processed bar — used externally for health checks.</summary>
    public DateTime LastBarAt { get; private set; } = DateTime.MinValue;

    public void SetSessionId(string sessionId) => _sessionId = sessionId;

    /// <summary>
    /// Call before RunAsync when resuming a crashed session. Skips the portfolio reset so the
    /// state rebuilt by BotController from persisted trades is preserved.
    /// </summary>
    public void SetResumeMode() => _resumeMode = true;
    public void SetStartingEth(decimal eth) => _sessionStartingEth = eth;

    public LiveDemoRuntime(
        IOptions<BotOptions> options,
        ILogger<LiveDemoRuntime> logger,
        ITradingProviderFactory providerFactory,
        IBarAggregationService barAggregationService,
        IBarDecisionPipeline barDecisionPipeline,
        IRiskManager riskManager,
        IPositionSizer positionSizer,
        IOrderValidationService orderValidationService,
        AnalyticsService analyticsService,
        ISessionResultRecorder sessionResultRecorder,
        IPortfolioStateStore portfolioStateStore,
        BotStateService botState,
        DatabaseService db)
    {
        _options = options.Value;
        _logger = logger;
        _providerFactory = providerFactory;
        _barAggregationService = barAggregationService;
        _barDecisionPipeline = barDecisionPipeline;
        _riskManager = riskManager;
        _positionSizer = positionSizer;
        _orderValidationService = orderValidationService;
        _analyticsService = analyticsService;
        _sessionResultRecorder = sessionResultRecorder;
        _portfolioStateStore = portfolioStateStore;
        _botState = botState;
        _db = db;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        _executionGateway = _providerFactory.CreateExecutionGateway();

        _logger.LogInformation("Execution gateway: {Type}", _executionGateway.GetType().Name);

        InitialiseState();

        // _positionSeeded is set here rather than inside InitialiseState so the intent is
        // explicit: resume mode means BotController already rebuilt the portfolio from DB trades,
        // so the first-bar seed must be skipped. Fresh-start mode seeds on the first live bar.
        _positionSeeded = _resumeMode;
        if (_resumeMode)
        {
            _resumeMode = false;
            _logger.LogInformation("RESUME | Skipping portfolio reset — state restored from persisted trades.");
        }
        else
        {
            _portfolioStateStore.Reset(_options.StartingCash);
        }

        _sessionResultRecorder.Reset(_options.StartingCash);
        _barDecisionPipeline.Reset(_options.Symbols.Select(s => s.Symbol));

        var subscriptions = _options.Symbols
            .Select(s => new MarketDataSubscription
            {
                Symbol = s.Symbol,
                ProviderSymbol = string.IsNullOrWhiteSpace(s.ProviderSymbol) ? s.Symbol : s.ProviderSymbol!
            })
            .ToList();

        _marketDataFeed = _providerFactory.CreateMarketDataFeed();

        // Store delegates so they can be unsubscribed in the finally block.
        // Without this, each Reset adds a new handler to these singleton instances
        // and quotes/bars would be processed multiple times.
        Action<string>    onInfo      = info => _logger.LogInformation("[{Provider}] {Info}", _marketDataFeed.ProviderName, info);
        Action<Exception> onError     = ex   => _logger.LogError(ex, "[{Provider}] market-data error", _marketDataFeed.ProviderName);
        Action<QuoteTick> onQuote     = quote =>
        {
            var resolvedSymbol = ResolveInternalSymbol(quote.Symbol);
            var px = quote.Last ?? Mid(quote.Bid, quote.Ask) ?? 0m;
            _lastPriceBySymbol[resolvedSymbol] = px;
            _logger.LogDebug(
                "QUOTE | Provider={Provider} ProviderSymbol={ProviderSymbol} Symbol={Symbol} Px={Px:F4} Ts={Ts:u}",
                quote.Provider, quote.Symbol, resolvedSymbol, px, quote.TimestampUtc);
            _barAggregationService.OnQuote(quote);
        };
        Action<LiveBar>   onBarClosed = bar =>
        {
            _logger.LogDebug(
                "BAR CLOSED EVENT | Provider={Provider} ProviderSymbol={ProviderSymbol} O={Open:F4} H={High:F4} L={Low:F4} C={Close:F4} Start={Start:u} End={End:u}",
                bar.Provider, bar.Symbol, bar.Open, bar.High, bar.Low, bar.Close, bar.StartUtc, bar.EndUtc);
            _ = Task.Run(async () =>
            {
                try   { await ProcessClosedBarAsync(bar, cancellationToken); }
                catch (OperationCanceledException)
                {
                    // [OBS-001] Log cancellation so we can trace state at the time bars were dropped.
                    _logger.LogDebug(
                        "BAR CANCELLED | {Symbol} {End:u} — processing interrupted by cancellation token",
                        bar.Symbol, bar.EndUtc);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Unhandled exception processing bar {Symbol} {End:u}", bar.Symbol, bar.EndUtc);
                }
            }, cancellationToken);
        };

        _marketDataFeed.OnInfo        += onInfo;
        _marketDataFeed.OnError       += onError;
        _marketDataFeed.OnQuote       += onQuote;
        _barAggregationService.OnBarClosed += onBarClosed;

        await _marketDataFeed.ConnectAsync(subscriptions, cancellationToken);

        var staleThreshold = TimeSpan.FromSeconds(_options.Runtime.BarSeconds * 10);

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);

                if (LastBarAt != DateTime.MinValue && DateTime.UtcNow - LastBarAt > staleThreshold)
                    _logger.LogWarning(
                        "No bar received for {Seconds:F0}s — market data may be stale or feed disconnected.",
                        (DateTime.UtcNow - LastBarAt).TotalSeconds);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            // Unsubscribe handlers before disconnect so the singleton feeds don't
            // accumulate stale handlers across multiple Reset/restart cycles.
            _marketDataFeed.OnInfo        -= onInfo;
            _marketDataFeed.OnError       -= onError;
            _marketDataFeed.OnQuote       -= onQuote;
            _barAggregationService.OnBarClosed -= onBarClosed;

            if (_marketDataFeed is not null)
                await _marketDataFeed.DisconnectAsync(CancellationToken.None);

            // [ATOMICITY-05] EndSession must always run even if the summary export fails.
            try
            {
                await EmitLiveDemoSummaryAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SESSION SUMMARY | Failed to emit live demo summary — session will still be closed");
            }
            finally
            {
                if (!string.IsNullOrEmpty(_sessionId))
                    _db.EndSession(_sessionId, _portfolioStateStore.GetTotalEquity(_lastPriceBySymbol));
                _botState.NotifyStopped();
            }
        }
    }

    private void InitialiseState()
    {
        _lastPriceBySymbol.Clear();
        foreach (var symbol in _options.Symbols)
            _lastPriceBySymbol[symbol.Symbol] = 0m;
        // _positionSeeded is set explicitly in RunAsync based on resumeMode — not here.
    }

    private async Task ProcessClosedBarAsync(LiveBar bar, CancellationToken cancellationToken)
    {
        LastBarAt = DateTime.UtcNow;
        await _stateLock.WaitAsync(cancellationToken);

        try
        {
            var symbol = ResolveInternalSymbol(bar.Symbol);
            if (string.IsNullOrWhiteSpace(symbol))
            {
                _logger.LogWarning("Could not resolve internal symbol for provider symbol '{ProviderSymbol}'", bar.Symbol);
                return;
            }

            var candle = ToCandle(bar);
            _botState.NotifyBar(candle);

            // Seed starting ETH position at real market price on first bar so avgEntry is correct.
            // Use the session-specific starting ETH (from wallet) if set, otherwise fall back to config.
            var seedEth = _sessionStartingEth > 0m ? _sessionStartingEth : _options.StartingEth;
            if (!_positionSeeded && seedEth > 0m)
            {
                _portfolioStateStore.SeedPosition("ETHUSDT", seedEth, candle.Close);
                _positionSeeded = true;
                _logger.LogInformation(
                    "SEEDED | {Qty} ETH @ {Price:F4} (first bar close)",
                    seedEth, candle.Close);
            }
            _sessionResultRecorder.RecordCandle(symbol, candle);
            _lastPriceBySymbol[symbol] = candle.Close;

            var currentEquity = _portfolioStateStore.GetTotalEquity(_lastPriceBySymbol);
            var currentSymbolMarketValue = _portfolioStateStore.GetPositionMarketValue(symbol, candle.Close);

            var decision = await _barDecisionPipeline.ProcessClosedBarAsync(
                symbol,
                candle,
                _lastPriceBySymbol,
                currentEquity,
                currentSymbolMarketValue,
                cancellationToken);

            _logger.LogInformation(
                "BAR DECISION | Symbol={Symbol} Status={Status} HistoryCount={HistoryCount} Reason={Reason}",
                decision.Symbol,
                decision.Status,
                decision.HistoryCount,
                decision.Reason);

            if (!decision.IsReady || decision.Intent is null)
            {
                RecordEquityPoint(candle.Timestamp);
                return;
            }

            var symbolConfig = GetSymbolConfig(symbol);

            var quantity = decision.Intent.TargetQuantityOverride ?? (
                decision.Intent.IntentType == OrderIntentType.Buy
                    ? _positionSizer.GetBuyQuantity(
                        _portfolioStateStore.GetCash(),
                        candle.Close,
                        decision.CurrentEquity,
                        symbolConfig)
                    : _positionSizer.GetSellQuantity(
                        _portfolioStateStore.GetPositionQuantity(symbol),
                        symbolConfig));

            _logger.LogInformation(
                "SIZING | Symbol={Symbol} IntentType={IntentType} Qty={Qty:F4} Cash={Cash:F2} Equity={Equity:F2}",
                symbol,
                decision.Intent.IntentType,
                quantity,
                _portfolioStateStore.GetCash(),
                decision.CurrentEquity);

            var qtyValidation = _orderValidationService.ValidateExecutionQuantity(quantity, candle.Close, symbolConfig, symbol);
            if (!qtyValidation.IsValid)
            {
                _logger.LogWarning(
                    "QTY VALIDATION FAILED | Symbol={Symbol} Errors={Errors}",
                    symbol,
                    qtyValidation.ToSingleMessage());

                RecordEquityPoint(candle.Timestamp);
                return;
            }

            var request = new DemoOrderRequest
            {
                Symbol = symbol,
                Side = decision.Intent.IntentType == OrderIntentType.Buy ? "Buy" : "Sell",
                Quantity = quantity,
                ReferencePrice = candle.Close,
                LimitPrice = decision.Intent.LimitPrice,
                StopPrice = decision.Intent.StopPrice,
                Reason = $"{decision.Intent.OrderType} | {decision.Intent.TimeInForce} | {decision.Intent.Reason}"
            };

            _logger.LogInformation(
                "LOCAL PAPER REQUEST | Symbol={Symbol} Side={Side} Qty={Qty:F4} RefPrice={RefPrice:F4} Reason={Reason}",
                request.Symbol,
                request.Side,
                request.Quantity,
                request.ReferencePrice,
                request.Reason);

            var fill = await _executionGateway!.SubmitSimulatedOrderAsync(request, cancellationToken);

            if (!fill.Accepted)
            {
                _logger.LogWarning(
                    "LOCAL PAPER REJECTED | Symbol={Symbol} Side={Side} Qty={Qty:F4} Reason={Reason}",
                    request.Symbol,
                    request.Side,
                    request.Quantity,
                    fill.RejectionReason ?? "Unknown rejection");

                // If the intent pre-wrote a cycle row (estimated qty), delete it so the DB
                // doesn't contain a false completed cycle that never actually executed.
                if (decision.Intent.CycleId.HasValue && !string.IsNullOrEmpty(_sessionId))
                {
                    _db.DeleteCycleRow(decision.Intent.CycleId.Value);
                    _logger.LogWarning("CYCLE ROW DELETED | CycleId={Id} — fill rejected, pre-written estimate removed",
                        decision.Intent.CycleId.Value);
                }

                RecordEquityPoint(candle.Timestamp);
                return;
            }

            // [C-3] Write trade to DB FIRST, before applying to the in-memory portfolio.
            // If the process crashes after the DB write but before memory apply, the trade
            // is replayed correctly on resume. The old order (memory → DB) lost trades
            // permanently when a crash occurred in that window.
            if (!string.IsNullOrEmpty(_sessionId))
            {
                var tradeSide   = decision.Intent.IntentType == OrderIntentType.Buy ? TradeSide.Buy : TradeSide.Sell;
                // For sells, estimate PnL from current avgEntry (same formula as TryApplySellFill).
                // For buys, realizedPnL is always 0.
                var estimatedPnL = 0m;
                if (tradeSide == TradeSide.Sell)
                {
                    var avgEntry   = _portfolioStateStore.GetAverageEntryPrice(symbol);
                    var netProc    = fill.FillPrice * fill.FilledQuantity - fill.Commission;
                    estimatedPnL   = netProc - (avgEntry * fill.FilledQuantity);
                }

                var dbTrade = new Trade
                {
                    Timestamp   = fill.TimestampUtc,
                    Symbol      = symbol,
                    Side        = tradeSide,
                    Quantity    = fill.FilledQuantity,
                    Price       = fill.FillPrice,
                    Commission  = fill.Commission,
                    RealizedPnL = estimatedPnL,
                    Note        = fill.Reason
                };

                try
                {
                    _db.InsertTrade(_sessionId, dbTrade);
                }
                catch (Exception dbEx)
                {
                    // If the DB write fails, do NOT apply to memory — the portfolio and DB
                    // must stay in sync. The operator must investigate and recover manually.
                    _logger.LogCritical(dbEx,
                        "DB_TRADE_WRITE_FAILED | MANUAL RECOVERY REQUIRED | " +
                        "Symbol={Symbol} Side={Side} Qty={Qty:F5} Price={Price:F4} Ts={Ts:u}",
                        fill.Symbol, fill.Side, fill.FilledQuantity, fill.FillPrice, fill.TimestampUtc);
                    RecordEquityPoint(candle.Timestamp);
                    return;
                }
            }

            var applied = ApplyFillAndRecordTrade(symbol, decision.Intent, fill);
            if (!applied)
            {
                // DB record was written but portfolio rejected the apply — CRITICAL inconsistency.
                // The trade will be replayed on next resume so the portfolio will self-correct,
                // but alert immediately for manual review.
                _logger.LogCritical(
                    "PORTFOLIO_APPLY_FAILED | DB trade written but portfolio rejected — inconsistency! " +
                    "Symbol={Symbol} Side={Side} Qty={Qty:F4}. Will self-correct on next resume.",
                    fill.Symbol, fill.Side, fill.FilledQuantity);
                RecordEquityPoint(candle.Timestamp);
                return;
            }

            // Notify state with actual trade (which has the real RealizedPnL from TryApplySellFill).
            var lastTrade = _sessionResultRecorder.GetLastTrade();
            if (lastTrade is not null)
                _botState.NotifyTrade(lastTrade);

            _riskManager.RegisterExecutedTrade(
                DateOnly.FromDateTime(candle.Timestamp),
                symbol,
                decision.HistoryCount);

            _logger.LogInformation(
                "LOCAL PAPER EXECUTED | Symbol={Symbol} Side={Side} Qty={Qty:F4} Fill={Fill:F4} Comm={Comm:F2} Cash={Cash:F2} Equity={Equity:F2}",
                fill.Symbol,
                fill.Side,
                fill.FilledQuantity,
                fill.FillPrice,
                fill.Commission,
                _portfolioStateStore.GetCash(),
                _portfolioStateStore.GetTotalEquity(_lastPriceBySymbol));

            RecordEquityPoint(candle.Timestamp);
        }
        finally
        {
            _stateLock.Release();
        }
    }

    /// <summary>
    /// Converts all available cash back to ETH via a market buy, then closes any open
    /// cycling cycles in the DB. Called before Stop & Reset so no cash is stranded.
    /// </summary>
    public async Task ForceRebuyCyclesAsync()
    {
        if (_executionGateway == null) return;

        await _stateLock.WaitAsync(CancellationToken.None);
        try
        {
            var cash = _portfolioStateStore.GetCash();
            if (cash < 1m) return;

            var symbol    = _options.Symbols.FirstOrDefault()?.Symbol ?? "ETHUSDT";
            var price     = _lastPriceBySymbol.TryGetValue(symbol, out var p) && p > 0m ? p : 0m;
            if (price <= 0m) return;

            var symbolConfig  = GetSymbolConfig(symbol);
            var step          = symbolConfig?.QuantityStep ?? 0.001m;
            var feeMultiplier = 1m + _options.TakerFeePercent / 100m;
            var qty           = Math.Floor(cash / (price * feeMultiplier) / step) * step;
            if (qty <= 0m) return;

            _logger.LogInformation(
                "FORCE REBUY (Stop & Reset) | {Symbol} Cash={Cash:F2} Qty={Qty:F5} Price={Price:F2}",
                symbol, cash, qty, price);

            var request = new DemoOrderRequest
            {
                Symbol         = symbol,
                Side           = "Buy",
                Quantity       = qty,
                ReferencePrice = price,
                Reason         = "Stop & Reset — force rebuy"
            };

            var fill = await _executionGateway.SubmitSimulatedOrderAsync(request, CancellationToken.None);
            if (!fill.Accepted)
            {
                _logger.LogWarning("FORCE REBUY | Rejected: {Reason}", fill.RejectionReason ?? "unknown");
                return;
            }

            _portfolioStateStore.TryApplyBuyFill(symbol, fill.FilledQuantity, fill.FillPrice, fill.Commission, out _);

            var trade = new Trade
            {
                Timestamp   = fill.TimestampUtc,
                Symbol      = symbol,
                Side        = TradeSide.Buy,
                Quantity    = fill.FilledQuantity,
                Price       = fill.FillPrice,
                Commission  = fill.Commission,
                RealizedPnL = 0m,
                Note        = "Stop & Reset — force rebuy"
            };
            _sessionResultRecorder.RecordTrade(trade);

            if (!string.IsNullOrEmpty(_sessionId))
            {
                _db.InsertTrade(_sessionId, trade);
                _botState.NotifyTrade(trade);

                foreach (var c in _db.GetOpenCyclesForSession(_sessionId))
                    _db.CloseCycle(c.Id, fill.FilledQuantity, fill.FillPrice);
            }

            _logger.LogInformation(
                "FORCE REBUY (Stop & Reset) | Done — {Qty:F5} ETH @ {Price:F2}",
                fill.FilledQuantity, fill.FillPrice);
        }
        finally
        {
            _stateLock.Release();
        }
    }

    private async Task CloseAllOpenPositionsAsync()
    {
        // Acquire the state lock with no timeout so we wait for any in-flight bar
        // processing to finish before we flatten positions.
        await _stateLock.WaitAsync(CancellationToken.None);
        try
        {
            var openPositions = _portfolioStateStore
                .GetPortfolioSnapshot().Positions
                .Where(p => p.Value.Quantity > 0m)
                .ToList();

            if (openPositions.Count == 0)
                return;

            _logger.LogInformation(
                "SHUTDOWN | Closing {Count} open position(s) before exit",
                openPositions.Count);

            foreach (var (symbol, position) in openPositions)
            {
                var lastPrice = _lastPriceBySymbol.TryGetValue(symbol, out var px) && px > 0m
                    ? px
                    : position.AverageEntryPrice;

                if (lastPrice <= 0m)
                {
                    _logger.LogWarning(
                        "SHUTDOWN | Skipping {Symbol} close-out — no last price available",
                        symbol);
                    continue;
                }

                var symbolConfig = _options.Symbols.FirstOrDefault(s =>
                    string.Equals(s.Symbol, symbol, StringComparison.OrdinalIgnoreCase));

                if (symbolConfig is null)
                {
                    _logger.LogWarning(
                        "SHUTDOWN | Skipping {Symbol} close-out — no symbol config found",
                        symbol);
                    continue;
                }

                var quantity = _positionSizer.GetSellQuantity(position.Quantity, symbolConfig);
                if (quantity <= 0m)
                {
                    _logger.LogWarning(
                        "SHUTDOWN | Skipping {Symbol} close-out — sell quantity rounds to zero",
                        symbol);
                    continue;
                }

                var request = new DemoOrderRequest
                {
                    Symbol = symbol,
                    Side = "Sell",
                    Quantity = quantity,
                    ReferencePrice = lastPrice,
                    Reason = "Shutdown close-out"
                };

                _logger.LogInformation(
                    "SHUTDOWN CLOSE | Symbol={Symbol} Qty={Qty:F5} LastPrice={Price:F4}",
                    symbol, quantity, lastPrice);

                var fill = await _executionGateway!.SubmitSimulatedOrderAsync(
                    request, CancellationToken.None);

                if (!fill.Accepted)
                {
                    _logger.LogWarning(
                        "SHUTDOWN CLOSE REJECTED | Symbol={Symbol} Reason={Reason}",
                        symbol, fill.RejectionReason ?? "Unknown");
                    continue;
                }

                if (!_portfolioStateStore.TryApplySellFill(
                        symbol, fill.FilledQuantity, fill.FillPrice, fill.Commission,
                        out var realizedPnL, out var rejection))
                {
                    _logger.LogWarning(
                        "SHUTDOWN CLOSE APPLY FAILED | Symbol={Symbol} Reason={Reason}",
                        symbol, rejection ?? "Unknown");
                    continue;
                }

                var closeTrade = new Trade
                {
                    Timestamp   = fill.TimestampUtc,
                    Symbol      = symbol,
                    Side        = TradeSide.Sell,
                    Quantity    = fill.FilledQuantity,
                    Price       = fill.FillPrice,
                    Commission  = fill.Commission,
                    RealizedPnL = realizedPnL,
                    Note        = "Shutdown close-out"
                };

                _sessionResultRecorder.RecordTrade(closeTrade);

                if (!string.IsNullOrEmpty(_sessionId))
                {
                    _db.InsertTrade(_sessionId, closeTrade);
                    _botState.NotifyTrade(closeTrade);
                }

                _logger.LogInformation(
                    "SHUTDOWN CLOSE EXECUTED | Symbol={Symbol} Qty={Qty:F5} Price={Price:F4} RealizedPnL={PnL:F2}",
                    symbol, fill.FilledQuantity, fill.FillPrice, realizedPnL);
            }
        }
        finally
        {
            _stateLock.Release();
        }
    }

    private bool ApplyFillAndRecordTrade(string symbol, OrderIntent intent, LocalPaperFillResult fill)
    {
        if (intent.IntentType == OrderIntentType.Buy)
        {
            if (!_portfolioStateStore.TryApplyBuyFill(
                    symbol,
                    fill.FilledQuantity,
                    fill.FillPrice,
                    fill.Commission,
                    out var rejection))
            {
                _logger.LogWarning(
                    "BUY APPLY REJECTED | Symbol={Symbol} Reason={Reason}",
                    symbol,
                    rejection ?? "Unknown");

                return false;
            }

            _sessionResultRecorder.RecordTrade(new Trade
            {
                Timestamp = fill.TimestampUtc,
                Symbol = symbol,
                Side = TradeSide.Buy,
                Quantity = fill.FilledQuantity,
                Price = fill.FillPrice,
                Commission = fill.Commission,
                RealizedPnL = 0m,
                Note = fill.Reason
            });

            // Correct the pre-execution estimated qty in the cycle DB record with the actual fill
            if (intent.CycleId.HasValue && !string.IsNullOrEmpty(_sessionId))
                _db.CorrectCycleBuyQty(intent.CycleId.Value, fill.FilledQuantity, fill.FillPrice);

            return true;
        }

        if (intent.IntentType == OrderIntentType.Sell)
        {
            if (!_portfolioStateStore.TryApplySellFill(
                    symbol,
                    fill.FilledQuantity,
                    fill.FillPrice,
                    fill.Commission,
                    out var realizedPnL,
                    out var rejection))
            {
                _logger.LogWarning(
                    "SELL APPLY REJECTED | Symbol={Symbol} Reason={Reason}",
                    symbol,
                    rejection ?? "Unknown");

                return false;
            }

            _sessionResultRecorder.RecordTrade(new Trade
            {
                Timestamp = fill.TimestampUtc,
                Symbol = symbol,
                Side = TradeSide.Sell,
                Quantity = fill.FilledQuantity,
                Price = fill.FillPrice,
                Commission = fill.Commission,
                RealizedPnL = realizedPnL,
                Note = fill.Reason
            });

            return true;
        }

        return false;
    }

    private void RecordEquityPoint(DateTime timestampUtc)
    {
        var equity = _portfolioStateStore.GetTotalEquity(_lastPriceBySymbol);
        var point  = new EquityPoint { Timestamp = timestampUtc, Equity = equity };

        _sessionResultRecorder.RecordEquityPoint(
            point,
            exposed: _portfolioStateStore.GetTotalMarketValue(_lastPriceBySymbol) > 0m);

        if (!string.IsNullOrEmpty(_sessionId))
            _db.InsertEquityPoint(_sessionId, timestampUtc, equity);

        var ethQty = _portfolioStateStore.GetPositionQuantity("ETHUSDT");
        var positions = _portfolioStateStore
            .GetPortfolioSnapshot().Positions
            .ToDictionary(
                p => p.Key,
                p => (p.Value.Quantity, p.Value.AverageEntryPrice),
                StringComparer.OrdinalIgnoreCase);

        _botState.NotifyBarUpdate(point, ethQty, _portfolioStateStore.GetCash(), equity, positions);
    }

    private async Task EmitLiveDemoSummaryAsync(CancellationToken cancellationToken = default)
    {
        await _stateLock.WaitAsync(CancellationToken.None);
        try
        {

            var result = _sessionResultRecorder.BuildResult(
                _portfolioStateStore.GetPortfolioSnapshot(),
                _lastPriceBySymbol);

            var overallSummary = _analyticsService.BuildOverallSummary(result);
            var perSymbolSummaries = _analyticsService.BuildPerSymbolSummaries(result);

            Console.WriteLine();
            Console.WriteLine("===== LIVE DEMO SESSION SUMMARY =====");
            _analyticsService.PrintOverallSummary(overallSummary);
            _analyticsService.PrintPerSymbolSummaries(perSymbolSummaries);
            _analyticsService.PrintTrades(result.Trades);

            var outputFolder = Path.Combine(AppContext.BaseDirectory, _options.OutputFolder,
                _options.Runtime.Provider.ToString().ToLowerInvariant());
            Directory.CreateDirectory(outputFolder);

            CsvExporter.ExportTrades(Path.Combine(outputFolder, "live_demo_trades.csv"), result.Trades);
            CsvExporter.ExportEquityCurve(Path.Combine(outputFolder, "live_demo_equity_curve.csv"), result.EquityCurve);
            CsvExporter.ExportSummaries(Path.Combine(outputFolder, "live_demo_symbol_summaries.csv"), perSymbolSummaries);

            _logger.LogInformation("Live demo summary exported to {OutputFolder}", outputFolder);
        }
        finally
        {
            _stateLock.Release();
        }
    }

    private Candle ToCandle(LiveBar bar)
    {
        return new Candle
        {
            Timestamp = bar.EndUtc,
            Open = bar.Open,
            High = bar.High,
            Low = bar.Low,
            Close = bar.Close,
            Volume = bar.Volume
        };
    }

    private string ResolveInternalSymbol(string providerSymbol)
    {
        var match = _options.Symbols.FirstOrDefault(s =>
            string.Equals(s.Symbol, providerSymbol, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(s.ProviderSymbol, providerSymbol, StringComparison.OrdinalIgnoreCase));

        return match?.Symbol ?? providerSymbol;
    }

    private SymbolConfig GetSymbolConfig(string symbol)
    {
        var symbolConfig = _options.Symbols.FirstOrDefault(s =>
            string.Equals(s.Symbol, symbol, StringComparison.OrdinalIgnoreCase));

        if (symbolConfig is null)
            throw new InvalidOperationException($"No SymbolConfig found for symbol '{symbol}'.");

        return symbolConfig;
    }

    private static decimal? Mid(decimal? bid, decimal? ask)
    {
        if (bid.HasValue && ask.HasValue && bid.Value > 0m && ask.Value > 0m)
            return (bid.Value + ask.Value) / 2m;

        return bid ?? ask;
    }
}