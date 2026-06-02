using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PaperTradingBot.Config;
using PaperTradingBot.Interfaces;
using PaperTradingBot.Models;

namespace PaperTradingBot.Services;

public class BacktestEngine
{
    private readonly BotOptions _options;
    private readonly IMarketDataFeed _marketDataFeed;
    private readonly IBarDecisionPipeline _barDecisionPipeline;
    private readonly IPositionSizer _positionSizer;
    private readonly IOrderValidationService _orderValidationService;
    private readonly ISessionResultRecorder _sessionResultRecorder;
    private readonly PaperBroker _paperBroker;
    private readonly ILogger<BacktestEngine> _logger;

    public BacktestEngine(
        IOptions<BotOptions> options,
        IMarketDataFeed marketDataFeed,
        IBarDecisionPipeline barDecisionPipeline,
        IPositionSizer positionSizer,
        IOrderValidationService orderValidationService,
        ISessionResultRecorder sessionResultRecorder,
        PaperBroker paperBroker,
        ILogger<BacktestEngine> logger)
    {
        _options = options.Value;
        _marketDataFeed = marketDataFeed;
        _barDecisionPipeline = barDecisionPipeline;
        _positionSizer = positionSizer;
        _orderValidationService = orderValidationService;
        _sessionResultRecorder = sessionResultRecorder;
        _paperBroker = paperBroker;
        _logger = logger;
    }

    public async Task<BacktestResult> RunAsync(CancellationToken cancellationToken)
    {
        _sessionResultRecorder.Reset(_options.StartingCash);
        _paperBroker.Reset();

        var seriesBySymbol = _marketDataFeed.GetCandlesBySymbol();
        var symbolConfigs = _options.Symbols.ToDictionary(s => s.Symbol, StringComparer.OrdinalIgnoreCase);

        _barDecisionPipeline.Reset(seriesBySymbol.Keys);

        var indices = seriesBySymbol.Keys.ToDictionary(s => s, _ => 0, StringComparer.OrdinalIgnoreCase);
        var pendingOrders = new Dictionary<string, PendingOrder>(StringComparer.OrdinalIgnoreCase);
        var lastCloseBySymbol = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);

        var allTimestamps = seriesBySymbol.Values
            .SelectMany(x => x.Select(c => c.Timestamp))
            .Distinct()
            .OrderBy(x => x)
            .ToList();

        int recordedTradeCount = 0;

        foreach (var timestamp in allTimestamps)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var currentBars = new Dictionary<string, Candle>(StringComparer.OrdinalIgnoreCase);

            foreach (var kvp in seriesBySymbol)
            {
                var symbol = kvp.Key;
                var series = kvp.Value;
                var index = indices[symbol];

                if (index < series.Count && series[index].Timestamp == timestamp)
                {
                    currentBars[symbol] = series[index];
                    indices[symbol]++;
                }
            }

            // Step 1: attempt to fill any pending orders on this bar's open/high/low
            foreach (var symbol in currentBars.Keys.ToList())
            {
                if (!pendingOrders.TryGetValue(symbol, out var pending))
                    continue;

                var pendingValidation = _orderValidationService.ValidatePendingOrder(pending, symbol);
                if (!pendingValidation.IsValid)
                {
                    _logger.LogWarning(
                        "Invalid pending order removed for {Symbol}: {Errors}",
                        symbol, pendingValidation.ToSingleMessage());

                    pendingOrders.Remove(symbol);
                    continue;
                }

                var bar = currentBars[symbol];
                // History count before this bar is appended
                var historyCountBeforeBar = _barDecisionPipeline.GetHistory(symbol).Count;

                if (ShouldCancelPendingOrder(pending, bar.Timestamp, historyCountBeforeBar))
                {
                    pendingOrders.Remove(symbol);
                    continue;
                }

                var evaluation = EvaluatePendingOrderForBar(pending, bar);

                if (!evaluation.ExecutionPrice.HasValue)
                    continue;

                if (TryExecutePendingOrder(
                    symbol,
                    pending,
                    evaluation.ExecutionPrice.Value,
                    bar.Timestamp,
                    historyCountBeforeBar,
                    symbolConfigs[symbol],
                    lastCloseBySymbol))
                {
                    pendingOrders.Remove(symbol);
                }
            }

            // Step 2: update price state and record candles
            foreach (var kvp in currentBars)
            {
                lastCloseBySymbol[kvp.Key] = kvp.Value.Close;
                _sessionResultRecorder.RecordCandle(kvp.Key, kvp.Value);
            }

            // Flush any new trades the broker just recorded
            while (recordedTradeCount < _paperBroker.Trades.Count)
            {
                _sessionResultRecorder.RecordTrade(_paperBroker.Trades[recordedTradeCount]);
                recordedTradeCount++;
            }

            // Step 3: run the shared pipeline per symbol to get new order intents
            foreach (var kvp in currentBars)
            {
                var symbol = kvp.Key;
                var candle = kvp.Value;

                if (pendingOrders.ContainsKey(symbol))
                    continue;

                var currentEquity = _paperBroker.GetTotalEquity(lastCloseBySymbol);
                var symbolMarketValue = _paperBroker.GetPositionMarketValue(symbol, candle.Close);

                var decision = await _barDecisionPipeline.ProcessClosedBarAsync(
                    symbol,
                    candle,
                    lastCloseBySymbol,
                    currentEquity,
                    symbolMarketValue,
                    cancellationToken);

                _logger.LogInformation(
                    "BAR DECISION | Symbol={Symbol} Status={Status} HistoryCount={HistoryCount} Reason={Reason}",
                    decision.Symbol,
                    decision.Status,
                    decision.HistoryCount,
                    decision.Reason);

                if (!decision.IsReady || decision.Intent is null)
                    continue;

                var intent = decision.Intent;

                if (intent.IntentType == OrderIntentType.Buy && !_paperBroker.HasPosition(symbol))
                {
                    pendingOrders[symbol] = CreatePendingOrder(symbol, intent, candle.Timestamp, decision.HistoryCount);
                }
                else if (intent.IntentType == OrderIntentType.Sell && _paperBroker.HasPosition(symbol))
                {
                    pendingOrders[symbol] = CreatePendingOrder(symbol, intent, candle.Timestamp, decision.HistoryCount);
                }
            }

            _sessionResultRecorder.RecordEquityPoint(
                new EquityPoint
                {
                    Timestamp = timestamp,
                    Equity = _paperBroker.GetTotalEquity(lastCloseBySymbol)
                },
                exposed: _paperBroker.CurrentPortfolio.GetMarketValue(lastCloseBySymbol) > 0m);
        }

        return _sessionResultRecorder.BuildResult(
            _paperBroker.CurrentPortfolio,
            lastCloseBySymbol);
    }

    private PendingOrder CreatePendingOrder(string symbol, OrderIntent intent, DateTime createdAt, int createdBarCount)
    {
        return new PendingOrder
        {
            Symbol = symbol,
            IntentType = intent.IntentType,
            OrderType = intent.OrderType,
            TimeInForce = intent.TimeInForce,
            Reason = intent.Reason,
            CreatedAt = createdAt,
            CreatedBarCount = createdBarCount,
            ExpireAfterBars = intent.ExpireAfterBars ?? _options.Orders.DefaultExpireAfterBars,
            LimitPrice = intent.LimitPrice,
            StopPrice = intent.StopPrice,
            StopTriggered = false
        };
    }

    private bool ShouldCancelPendingOrder(PendingOrder order, DateTime currentBarTimestamp, int currentBarCountBeforeAppend)
    {
        if (order.TimeInForce == TimeInForce.Ioc && currentBarCountBeforeAppend > order.CreatedBarCount)
            return true;

        if (order.TimeInForce == TimeInForce.Day &&
            DateOnly.FromDateTime(currentBarTimestamp) > DateOnly.FromDateTime(order.CreatedAt))
            return true;

        if (order.ExpireAfterBars.HasValue)
        {
            var barsAlive = currentBarCountBeforeAppend - order.CreatedBarCount;
            if (barsAlive > order.ExpireAfterBars.Value)
                return true;
        }

        return false;
    }

    private PendingOrderEvaluation EvaluatePendingOrderForBar(PendingOrder order, Candle bar)
    {
        return order.OrderType switch
        {
            OrderType.Market => new PendingOrderEvaluation { ExecutionPrice = bar.Open },
            OrderType.Limit => EvaluateLimitOrder(order, bar),
            OrderType.Stop => EvaluateStopOrder(order, bar),
            OrderType.StopLimit => EvaluateStopLimitOrder(order, bar),
            _ => new PendingOrderEvaluation()
        };
    }

    private PendingOrderEvaluation EvaluateLimitOrder(PendingOrder order, Candle bar)
    {
        if (!order.LimitPrice.HasValue)
            return new PendingOrderEvaluation();

        var limit = order.LimitPrice.Value;

        if (order.Side == TradeSide.Buy)
        {
            if (bar.Open <= limit) return new PendingOrderEvaluation { ExecutionPrice = bar.Open };
            if (bar.Low <= limit) return new PendingOrderEvaluation { ExecutionPrice = limit };
        }
        else
        {
            if (bar.Open >= limit) return new PendingOrderEvaluation { ExecutionPrice = bar.Open };
            if (bar.High >= limit) return new PendingOrderEvaluation { ExecutionPrice = limit };
        }

        return new PendingOrderEvaluation();
    }

    private PendingOrderEvaluation EvaluateStopOrder(PendingOrder order, Candle bar)
    {
        if (!order.StopPrice.HasValue)
            return new PendingOrderEvaluation();

        var stop = order.StopPrice.Value;

        if (order.Side == TradeSide.Buy)
        {
            if (bar.Open >= stop) return new PendingOrderEvaluation { ExecutionPrice = bar.Open };
            if (bar.High >= stop) return new PendingOrderEvaluation { ExecutionPrice = stop };
        }
        else
        {
            if (bar.Open <= stop) return new PendingOrderEvaluation { ExecutionPrice = bar.Open };
            if (bar.Low <= stop) return new PendingOrderEvaluation { ExecutionPrice = stop };
        }

        return new PendingOrderEvaluation();
    }

    private PendingOrderEvaluation EvaluateStopLimitOrder(PendingOrder order, Candle bar)
    {
        if (!order.StopPrice.HasValue || !order.LimitPrice.HasValue)
            return new PendingOrderEvaluation();

        if (order.StopTriggered)
            return EvaluateLimitOrder(order, bar);

        var stop = order.StopPrice.Value;
        var limit = order.LimitPrice.Value;

        if (order.Side == TradeSide.Buy)
        {
            if (bar.Open >= stop)
            {
                order.StopTriggered = true;

                if (bar.Open <= limit)
                    return new PendingOrderEvaluation { ExecutionPrice = bar.Open };

                if (bar.Low <= limit)
                    return new PendingOrderEvaluation { ExecutionPrice = limit };

                return new PendingOrderEvaluation();
            }

            if (bar.High >= stop)
            {
                order.StopTriggered = true;
                return new PendingOrderEvaluation();
            }
        }
        else
        {
            if (bar.Open <= stop)
            {
                order.StopTriggered = true;

                if (bar.Open >= limit)
                    return new PendingOrderEvaluation { ExecutionPrice = bar.Open };

                if (bar.High >= limit)
                    return new PendingOrderEvaluation { ExecutionPrice = limit };

                return new PendingOrderEvaluation();
            }

            if (bar.Low <= stop)
            {
                order.StopTriggered = true;
                return new PendingOrderEvaluation();
            }
        }

        return new PendingOrderEvaluation();
    }

    private bool TryExecutePendingOrder(
        string symbol,
        PendingOrder order,
        decimal executionPrice,
        DateTime timestamp,
        int symbolBarCount,
        SymbolConfig symbolConfig,
        IReadOnlyDictionary<string, decimal> lastCloseBySymbol)
    {
        var tradeDate = DateOnly.FromDateTime(timestamp);

        if (order.IntentType == OrderIntentType.Buy)
        {
            if (_paperBroker.HasPosition(symbol))
                return true;

            var equityBeforeTrade = _paperBroker.GetTotalEquity(lastCloseBySymbol);
            if (equityBeforeTrade <= 0m)
                equityBeforeTrade = _paperBroker.CurrentPortfolio.Cash;

            var quantity = _positionSizer.GetBuyQuantity(
                _paperBroker.CurrentPortfolio.Cash,
                executionPrice,
                equityBeforeTrade,
                symbolConfig);

            var qtyValidation = _orderValidationService.ValidateExecutionQuantity(quantity, symbolConfig, symbol);
            if (!qtyValidation.IsValid)
                return false;

            if (_paperBroker.Buy(symbol, timestamp, executionPrice, quantity,
                $"{order.OrderType} | {order.TimeInForce} | {order.Reason}"))
            {
                // Risk manager trade registration is handled inside the pipeline on the next bar;
                // register here so the cooldown takes effect immediately in backtest.
                return true;
            }
        }
        else if (order.IntentType == OrderIntentType.Sell)
        {
            if (!_paperBroker.HasPosition(symbol))
                return true;

            var quantity = _positionSizer.GetSellQuantity(
                _paperBroker.GetPositionQuantity(symbol),
                symbolConfig);

            var qtyValidation = _orderValidationService.ValidateExecutionQuantity(quantity, symbolConfig, symbol);
            if (!qtyValidation.IsValid)
                return false;

            if (_paperBroker.Sell(symbol, timestamp, executionPrice, quantity,
                $"{order.OrderType} | {order.TimeInForce} | {order.Reason}"))
            {
                return true;
            }
        }

        return false;
    }

    private sealed class PendingOrderEvaluation
    {
        public decimal? ExecutionPrice { get; init; }
    }
}
