using Microsoft.Extensions.Logging;
using PaperTradingBot.Interfaces;
using PaperTradingBot.Models;

namespace PaperTradingBot.Services;

public class BarDecisionPipeline : IBarDecisionPipeline
{
    private IOrderIntentProvider _orderIntentProvider;
    private readonly IRiskManager _riskManager;
    private readonly IOrderValidationService _orderValidationService;
    private readonly ILogger<BarDecisionPipeline> _logger;

    private readonly Dictionary<string, List<Candle>> _historyBySymbol =
        new(StringComparer.OrdinalIgnoreCase);

    public BarDecisionPipeline(
        IOrderIntentProvider orderIntentProvider,
        IRiskManager riskManager,
        IOrderValidationService orderValidationService,
        ILogger<BarDecisionPipeline> logger)
    {
        _orderIntentProvider = orderIntentProvider;
        _riskManager = riskManager;
        _orderValidationService = orderValidationService;
        _logger = logger;
    }

    public void SetStrategy(IOrderIntentProvider provider) => _orderIntentProvider = provider;

    public void Reset(IEnumerable<string> symbols)
    {
        _historyBySymbol.Clear();

        foreach (var symbol in symbols.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            _historyBySymbol[symbol] = new List<Candle>();
        }
    }

    public IReadOnlyList<Candle> GetHistory(string symbol)
    {
        if (!_historyBySymbol.TryGetValue(symbol, out var history))
        {
            history = new List<Candle>();
            _historyBySymbol[symbol] = history;
        }

        return history;
    }

    public Task<BarDecisionResult> ProcessClosedBarAsync(
        string symbol,
        Candle candle,
        IReadOnlyDictionary<string, decimal> lastPrices,
        decimal currentEquity,
        decimal currentSymbolMarketValue,
        CancellationToken cancellationToken)
    {
        if (!_historyBySymbol.TryGetValue(symbol, out var history))
        {
            history = new List<Candle>();
            _historyBySymbol[symbol] = history;
        }

        history.Add(candle);

        // [H-3] Cap history to prevent unbounded memory growth — 500 bars ≈ 83 minutes at 10s bars,
        // well above the maximum indicator lookback used by any strategy.
        const int MaxHistoryBars = 500;
        if (history.Count > MaxHistoryBars)
            history.RemoveRange(0, history.Count - MaxHistoryBars);

        var currentDate = DateOnly.FromDateTime(candle.Timestamp);

        _logger.LogDebug(
            "PIPELINE INPUT | Symbol={Symbol} Close={Close:F4} HistoryCount={HistoryCount} Equity={Equity:F2} PositionValue={PositionValue:F2}",
            symbol,
            candle.Close,
            history.Count,
            currentEquity,
            currentSymbolMarketValue);

        _riskManager.BeginDayIfNeeded(currentDate, currentEquity);

        var intent = _orderIntentProvider.GetIntent(symbol, history);

        if (intent is null || !intent.IsActionable)
        {
            _logger.LogDebug(
                "PIPELINE RESULT | Symbol={Symbol} Status={Status} Reason={Reason}",
                symbol,
                BarDecisionStatus.NoAction,
                "No actionable intent");

            return Task.FromResult(new BarDecisionResult
            {
                Symbol = symbol,
                Candle = candle,
                HistoryCount = history.Count,
                Status = BarDecisionStatus.NoAction,
                Intent = intent,
                CurrentEquity = currentEquity,
                CurrentSymbolMarketValue = currentSymbolMarketValue,
                Reason = "No actionable intent"
            });
        }

        _logger.LogDebug(
            "PIPELINE INTENT | Symbol={Symbol} IntentType={IntentType} OrderType={OrderType} TIF={TimeInForce} Strength={Strength:F4}",
            symbol,
            intent.IntentType,
            intent.OrderType,
            intent.TimeInForce,
            intent.SignalStrength);

        var intentValidation = _orderValidationService.ValidateIntent(intent, symbol);
        if (!intentValidation.IsValid)
        {
            var reason = intentValidation.ToSingleMessage();

            _logger.LogWarning(
                "PIPELINE RESULT | Symbol={Symbol} Status={Status} Reason={Reason}",
                symbol,
                BarDecisionStatus.Rejected,
                reason);

            return Task.FromResult(new BarDecisionResult
            {
                Symbol = symbol,
                Candle = candle,
                HistoryCount = history.Count,
                Status = BarDecisionStatus.Rejected,
                Intent = intent,
                CurrentEquity = currentEquity,
                CurrentSymbolMarketValue = currentSymbolMarketValue,
                Reason = reason
            });
        }

        var riskCheck = _riskManager.CanQueueOrder(new RiskCheckContext
        {
            Date = currentDate,
            Symbol = symbol,
            IntentType = intent.IntentType,
            SymbolBarCount = history.Count,
            CurrentPrice = candle.Close,
            TotalEquity = currentEquity,
            CurrentSymbolMarketValue = currentSymbolMarketValue
        });

        if (!riskCheck.Allowed)
        {
            _logger.LogWarning(
                "PIPELINE RESULT | Symbol={Symbol} Status={Status} Reason={Reason}",
                symbol,
                BarDecisionStatus.Rejected,
                riskCheck.Reason);

            return Task.FromResult(new BarDecisionResult
            {
                Symbol = symbol,
                Candle = candle,
                HistoryCount = history.Count,
                Status = BarDecisionStatus.Rejected,
                Intent = intent,
                CurrentEquity = currentEquity,
                CurrentSymbolMarketValue = currentSymbolMarketValue,
                Reason = riskCheck.Reason
            });
        }

        _logger.LogDebug(
            "PIPELINE RESULT | Symbol={Symbol} Status={Status} Reason={Reason}",
            symbol,
            BarDecisionStatus.Ready,
            "Ready");

        return Task.FromResult(new BarDecisionResult
        {
            Symbol = symbol,
            Candle = candle,
            HistoryCount = history.Count,
            Status = BarDecisionStatus.Ready,
            Intent = intent,
            CurrentEquity = currentEquity,
            CurrentSymbolMarketValue = currentSymbolMarketValue,
            Reason = "Ready"
        });
    }
}
