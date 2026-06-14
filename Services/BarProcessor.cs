using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PaperTradingBot.Config;
using PaperTradingBot.Interfaces;
using PaperTradingBot.Models;

namespace PaperTradingBot.Services;

/// <summary>
/// Per-session mutable context for <see cref="BarProcessor"/>. Holds the bits that vary
/// per run (execution gateway, session id, shared last-price map, seeding state) so the
/// processor itself stays stateless and can be shared between the live runtime and the
/// backtest replay runtime — guaranteeing they execute identical logic.
/// </summary>
public sealed class BarSession
{
    public required ILocalPaperExecutionGateway Gateway { get; init; }
    public required string SessionId { get; set; }
    /// <summary>Shared with the owning runtime (same reference) so both see price updates.</summary>
    public required Dictionary<string, decimal> LastPriceBySymbol { get; init; }
    public bool    PositionSeeded { get; set; }
    public decimal SessionStartingEth { get; set; }
    /// <summary>Live persists per-bar equity to the DB (dashboard/history). Backtest skips it
    /// (190k inserts/run, unused for the edge report) — strategy behaviour is unaffected.</summary>
    public bool    PersistEquityPoints { get; init; } = true;
}

/// <summary>
/// The single source of truth for processing one closed bar: run the decision pipeline,
/// size the order, validate it, submit to the (simulated) execution gateway, persist the
/// trade, apply the fill, and record equity. Extracted verbatim from LiveDemoRuntime so
/// that backtests run the exact same path as the live paper bot (no drift = honest backtest).
///
/// Caller owns concurrency: the live runtime wraps this in its state lock; the replay
/// runtime drives bars strictly sequentially. This class does not lock.
/// </summary>
public sealed class BarProcessor
{
    private readonly BotOptions               _options;
    private readonly ILogger<BarProcessor>    _logger;
    private readonly IBarDecisionPipeline      _barDecisionPipeline;
    private readonly IPositionSizer            _positionSizer;
    private readonly IOrderValidationService   _orderValidationService;
    private readonly IRiskManager              _riskManager;
    private readonly ISessionResultRecorder    _sessionResultRecorder;
    private readonly IPortfolioStateStore      _portfolioStateStore;
    private readonly BotStateService           _botState;
    private readonly DatabaseService           _db;

    public BarProcessor(
        IOptions<BotOptions> options,
        ILogger<BarProcessor> logger,
        IBarDecisionPipeline barDecisionPipeline,
        IPositionSizer positionSizer,
        IOrderValidationService orderValidationService,
        IRiskManager riskManager,
        ISessionResultRecorder sessionResultRecorder,
        IPortfolioStateStore portfolioStateStore,
        BotStateService botState,
        DatabaseService db)
    {
        _options                = options.Value;
        _logger                 = logger;
        _barDecisionPipeline    = barDecisionPipeline;
        _positionSizer          = positionSizer;
        _orderValidationService = orderValidationService;
        _riskManager            = riskManager;
        _sessionResultRecorder  = sessionResultRecorder;
        _portfolioStateStore    = portfolioStateStore;
        _botState               = botState;
        _db                     = db;
    }

    public async Task ProcessBarAsync(Candle candle, string symbol, BarSession session, CancellationToken cancellationToken)
    {
        _botState.NotifyBar(candle);

        // Seed starting position at real market price on first bar so avgEntry is correct.
        var seedEth = session.SessionStartingEth > 0m ? session.SessionStartingEth : _options.StartingQuantity;
        if (!session.PositionSeeded && seedEth > 0m)
        {
            var primarySym = _options.Symbols.FirstOrDefault()?.Symbol ?? "ETHUSDT";
            _portfolioStateStore.SeedPosition(primarySym, seedEth, candle.Close);
            session.PositionSeeded = true;
            _logger.LogInformation(
                "SEEDED | {Qty} {Symbol} @ {Price:F4} (first bar close)",
                seedEth, primarySym, candle.Close);
        }
        _sessionResultRecorder.RecordCandle(symbol, candle);
        session.LastPriceBySymbol[symbol] = candle.Close;

        var currentEquity = _portfolioStateStore.GetTotalEquity(session.LastPriceBySymbol);
        var currentSymbolMarketValue = _portfolioStateStore.GetPositionMarketValue(symbol, candle.Close);

        var decision = await _barDecisionPipeline.ProcessClosedBarAsync(
            symbol,
            candle,
            session.LastPriceBySymbol,
            currentEquity,
            currentSymbolMarketValue,
            cancellationToken);

        _logger.LogInformation(
            "BAR DECISION | Symbol={Symbol} Price={Price:F2} Status={Status} HistoryCount={HistoryCount} Reason={Reason}",
            decision.Symbol, candle.Close, decision.Status, decision.HistoryCount, decision.Reason);

        if (!decision.IsReady || decision.Intent is null)
        {
            if (decision.Status == BarDecisionStatus.Rejected &&
                decision.Intent?.IntentType == OrderIntentType.Sell)
            {
                _barDecisionPipeline.Strategy.RollbackSell(symbol, decision.Reason);
                _logger.LogWarning("SELL REJECTED BY PIPELINE | Symbol={Symbol} reason={Reason}", symbol, decision.Reason);
            }
            RecordEquityPoint(candle.Timestamp, session);
            return;
        }

        var symbolConfig = GetSymbolConfig(symbol);

        var quantity = decision.Intent.TargetQuantityOverride ?? (
            decision.Intent.IntentType == OrderIntentType.Buy
                ? _positionSizer.GetBuyQuantity(
                    _portfolioStateStore.GetCash(), candle.Close, decision.CurrentEquity, symbolConfig)
                : _positionSizer.GetSellQuantity(
                    _portfolioStateStore.GetPositionQuantity(symbol), symbolConfig));

        _logger.LogInformation(
            "SIZING | Symbol={Symbol} IntentType={IntentType} Qty={Qty:F4} Cash={Cash:F2} Equity={Equity:F2}",
            symbol, decision.Intent.IntentType, quantity, _portfolioStateStore.GetCash(), decision.CurrentEquity);

        var qtyValidation = _orderValidationService.ValidateExecutionQuantity(quantity, candle.Close, symbolConfig, symbol);
        if (!qtyValidation.IsValid)
        {
            _logger.LogWarning("QTY VALIDATION FAILED | Symbol={Symbol} Errors={Errors}", symbol, qtyValidation.ToSingleMessage());
            if (decision.Intent.IntentType == OrderIntentType.Sell)
                _barDecisionPipeline.Strategy.RollbackSell(symbol, $"qty validation: {qtyValidation.ToSingleMessage()}");
            RecordEquityPoint(candle.Timestamp, session);
            return;
        }

        // Commit cycle sell to DB only after both pipeline and qty validation approve.
        if (decision.Intent.IntentType == OrderIntentType.Sell && !string.IsNullOrEmpty(session.SessionId))
        {
            var cycleId = _barDecisionPipeline.Strategy.CommitSellToDB(symbol, session.SessionId);
            if (cycleId.HasValue)
                decision.Intent.CycleId = cycleId;
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
            request.Symbol, request.Side, request.Quantity, request.ReferencePrice, request.Reason);

        var fill = await session.Gateway.SubmitSimulatedOrderAsync(request, cancellationToken);

        if (!fill.Accepted)
        {
            _logger.LogWarning(
                "LOCAL PAPER REJECTED | Symbol={Symbol} Side={Side} Qty={Qty:F4} Reason={Reason}",
                request.Symbol, request.Side, request.Quantity, fill.RejectionReason ?? "Unknown rejection");

            if (decision.Intent.CycleId.HasValue && !string.IsNullOrEmpty(session.SessionId))
            {
                _db.DeleteCycleRow(decision.Intent.CycleId.Value);
                _logger.LogWarning("CYCLE ROW DELETED | CycleId={Id} — fill rejected, cycle row removed", decision.Intent.CycleId.Value);
            }

            if (decision.Intent.IntentType == OrderIntentType.Sell)
                _barDecisionPipeline.Strategy.RollbackSell(symbol, $"gateway rejected: {fill.RejectionReason ?? "unknown"}");

            RecordEquityPoint(candle.Timestamp, session);
            return;
        }

        // [C-3] Write trade to DB FIRST, before applying to the in-memory portfolio.
        if (!string.IsNullOrEmpty(session.SessionId))
        {
            var tradeSide = decision.Intent.IntentType == OrderIntentType.Buy ? TradeSide.Buy : TradeSide.Sell;
            var estimatedPnL = 0m;
            if (tradeSide == TradeSide.Sell)
            {
                var avgEntry = _portfolioStateStore.GetAverageEntryPrice(symbol);
                var netProc  = fill.FillPrice * fill.FilledQuantity - fill.Commission;
                estimatedPnL = netProc - (avgEntry * fill.FilledQuantity);
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
                _db.InsertTrade(session.SessionId, dbTrade);
            }
            catch (Exception dbEx)
            {
                _logger.LogCritical(dbEx,
                    "DB_TRADE_WRITE_FAILED | MANUAL RECOVERY REQUIRED | Symbol={Symbol} Side={Side} Qty={Qty:F5} Price={Price:F4} Ts={Ts:u}",
                    fill.Symbol, fill.Side, fill.FilledQuantity, fill.FillPrice, fill.TimestampUtc);
                RecordEquityPoint(candle.Timestamp, session);
                return;
            }
        }

        var applied = ApplyFillAndRecordTrade(symbol, decision.Intent, fill, session);
        if (!applied)
        {
            _logger.LogCritical(
                "PORTFOLIO_APPLY_FAILED | DB trade written but portfolio rejected — inconsistency! " +
                "Symbol={Symbol} Side={Side} Qty={Qty:F4}. Will self-correct on next resume.",
                fill.Symbol, fill.Side, fill.FilledQuantity);
            RecordEquityPoint(candle.Timestamp, session);
            return;
        }

        var lastTrade = _sessionResultRecorder.GetLastTrade();
        if (lastTrade is not null)
            _botState.NotifyTrade(lastTrade);

        _riskManager.RegisterExecutedTrade(
            DateOnly.FromDateTime(candle.Timestamp), symbol, decision.HistoryCount);

        _logger.LogInformation(
            "LOCAL PAPER EXECUTED | Symbol={Symbol} Side={Side} Qty={Qty:F4} Fill={Fill:F4} Comm={Comm:F2} Cash={Cash:F2} Equity={Equity:F2}",
            fill.Symbol, fill.Side, fill.FilledQuantity, fill.FillPrice, fill.Commission,
            _portfolioStateStore.GetCash(), _portfolioStateStore.GetTotalEquity(session.LastPriceBySymbol));

        RecordEquityPoint(candle.Timestamp, session);
    }

    public void RecordEquityPoint(DateTime timestampUtc, BarSession session)
    {
        var equity = _portfolioStateStore.GetTotalEquity(session.LastPriceBySymbol);
        var point  = new EquityPoint { Timestamp = timestampUtc, Equity = equity };

        _sessionResultRecorder.RecordEquityPoint(
            point,
            exposed: _portfolioStateStore.GetTotalMarketValue(session.LastPriceBySymbol) > 0m);

        var ethQty = _portfolioStateStore.GetPositionQuantity(_options.Symbols.FirstOrDefault()?.Symbol ?? "ETHUSDT");
        var positions = _portfolioStateStore
            .GetPortfolioSnapshot().Positions
            .ToDictionary(
                p => p.Key,
                p => (p.Value.Quantity, p.Value.AverageEntryPrice),
                StringComparer.OrdinalIgnoreCase);

        if (session.PersistEquityPoints && !string.IsNullOrEmpty(session.SessionId))
            _db.InsertEquityPoint(session.SessionId, timestampUtc, equity, ethQty);

        _botState.NotifyBarUpdate(point, ethQty, _portfolioStateStore.GetCash(), equity, positions);
    }

    private bool ApplyFillAndRecordTrade(string symbol, OrderIntent intent, LocalPaperFillResult fill, BarSession session)
    {
        if (intent.IntentType == OrderIntentType.Buy)
        {
            if (!_portfolioStateStore.TryApplyBuyFill(symbol, fill.FilledQuantity, fill.FillPrice, fill.Commission, out var rejection))
            {
                _logger.LogWarning("BUY APPLY REJECTED | Symbol={Symbol} Reason={Reason}", symbol, rejection ?? "Unknown");
                return false;
            }

            _sessionResultRecorder.RecordTrade(new Trade
            {
                Timestamp = fill.TimestampUtc, Symbol = symbol, Side = TradeSide.Buy,
                Quantity = fill.FilledQuantity, Price = fill.FillPrice, Commission = fill.Commission,
                RealizedPnL = 0m, Note = fill.Reason
            });

            // Correct the pre-execution estimated qty in the cycle DB record with the actual
            // fill, capped at what the cycle's own sell proceeds could repurchase.
            if (intent.CycleId.HasValue && !string.IsNullOrEmpty(session.SessionId))
            {
                var attributedQty = intent.CycleBuyQtyCap.HasValue
                    ? Math.Min(fill.FilledQuantity, intent.CycleBuyQtyCap.Value)
                    : fill.FilledQuantity;
                _db.CorrectCycleBuyQty(intent.CycleId.Value, attributedQty, fill.FillPrice);
            }

            return true;
        }

        if (intent.IntentType == OrderIntentType.Sell)
        {
            if (!_portfolioStateStore.TryApplySellFill(symbol, fill.FilledQuantity, fill.FillPrice, fill.Commission, out var realizedPnL, out var rejection))
            {
                _logger.LogWarning("SELL APPLY REJECTED | Symbol={Symbol} Reason={Reason}", symbol, rejection ?? "Unknown");
                return false;
            }

            _sessionResultRecorder.RecordTrade(new Trade
            {
                Timestamp = fill.TimestampUtc, Symbol = symbol, Side = TradeSide.Sell,
                Quantity = fill.FilledQuantity, Price = fill.FillPrice, Commission = fill.Commission,
                RealizedPnL = realizedPnL, Note = fill.Reason
            });

            return true;
        }

        return false;
    }

    private SymbolConfig GetSymbolConfig(string symbol)
    {
        var symbolConfig = _options.Symbols.FirstOrDefault(s =>
            string.Equals(s.Symbol, symbol, StringComparison.OrdinalIgnoreCase));
        if (symbolConfig is null)
            throw new InvalidOperationException($"No SymbolConfig found for symbol '{symbol}'.");
        return symbolConfig;
    }
}
