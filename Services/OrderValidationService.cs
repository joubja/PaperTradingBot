using PaperTradingBot.Config;
using PaperTradingBot.Interfaces;
using PaperTradingBot.Models;

namespace PaperTradingBot.Services;

public class OrderValidationService : IOrderValidationService
{
    public OrderValidationResult ValidateIntent(OrderIntent intent, string symbol)
    {
        var errors = new List<string>();

        if (intent is null)
        {
            errors.Add($"Order intent for symbol '{symbol}' is null.");
            return OrderValidationResult.Invalid(errors);
        }

        if (string.IsNullOrWhiteSpace(symbol))
            errors.Add("Symbol is required.");

        switch (intent.IntentType)
        {
            case OrderIntentType.None:
                ValidateNoneIntent(intent, errors);
                break;

            case OrderIntentType.Buy:
            case OrderIntentType.Sell:
                ValidateActionableIntent(intent, errors);
                break;

            default:
                errors.Add($"Unsupported intent type: {intent.IntentType}.");
                break;
        }

        return errors.Count == 0
            ? OrderValidationResult.Valid()
            : OrderValidationResult.Invalid(errors);
    }

    public OrderValidationResult ValidatePendingOrder(PendingOrder order, string symbol)
    {
        var errors = new List<string>();

        if (order is null)
        {
            errors.Add($"Pending order for symbol '{symbol}' is null.");
            return OrderValidationResult.Invalid(errors);
        }

        if (string.IsNullOrWhiteSpace(order.Symbol))
            errors.Add("Pending order symbol is required.");

        if (!string.Equals(order.Symbol, symbol, StringComparison.OrdinalIgnoreCase))
        {
            errors.Add($"Pending order symbol '{order.Symbol}' does not match expected symbol '{symbol}'.");
        }

        if (order.IntentType == OrderIntentType.None)
            errors.Add("Pending orders cannot have IntentType.None.");

        // Reuse the same core validation by constructing a lightweight intent view.
        var syntheticIntent = new OrderIntent
        {
            IntentType = order.IntentType,
            OrderType = order.OrderType,
            TimeInForce = order.TimeInForce,
            ExpireAfterBars = order.ExpireAfterBars,
            LimitPrice = order.LimitPrice,
            StopPrice = order.StopPrice,
            Reason = order.Reason
        };

        var intentValidation = ValidateIntent(syntheticIntent, symbol);
        if (!intentValidation.IsValid)
            errors.AddRange(intentValidation.Errors);

        if (order.CreatedBarCount < 0)
            errors.Add("Pending order CreatedBarCount cannot be negative.");

        // StopTriggered only makes sense for StopLimit in this engine.
        if (order.StopTriggered && order.OrderType != OrderType.StopLimit)
        {
            errors.Add("StopTriggered is only valid for StopLimit pending orders.");
        }

        return errors.Count == 0
            ? OrderValidationResult.Valid()
            : OrderValidationResult.Invalid(errors);
    }

    public OrderValidationResult ValidateSymbolConfig(SymbolConfig symbolConfig)
    {
        var errors = new List<string>();

        if (symbolConfig is null)
        {
            errors.Add("Symbol configuration is null.");
            return OrderValidationResult.Invalid(errors);
        }

        if (string.IsNullOrWhiteSpace(symbolConfig.Symbol))
            errors.Add("SymbolConfig.Symbol is required.");

        if (string.IsNullOrWhiteSpace(symbolConfig.FilePath))
            errors.Add($"Symbol '{symbolConfig.Symbol}' is missing FilePath.");

        if (symbolConfig.QuantityStep <= 0m)
        {
            errors.Add(
                $"Symbol '{symbolConfig.Symbol}' has invalid QuantityStep '{symbolConfig.QuantityStep}'. QuantityStep must be greater than zero.");
        }

        return errors.Count == 0
            ? OrderValidationResult.Valid()
            : OrderValidationResult.Invalid(errors);
    }

    public OrderValidationResult ValidateExecutionQuantity(decimal quantity, SymbolConfig symbolConfig, string symbol)
        => ValidateExecutionQuantity(quantity, referencePrice: 0m, symbolConfig, symbol);

    public OrderValidationResult ValidateExecutionQuantity(
        decimal quantity,
        decimal referencePrice,
        SymbolConfig symbolConfig,
        string symbol)
    {
        var errors = new List<string>();

        if (quantity <= 0m)
            errors.Add($"Execution quantity for symbol '{symbol}' must be greater than zero.");

        if (symbolConfig is null)
        {
            errors.Add($"Symbol config for '{symbol}' is null.");
            return OrderValidationResult.Invalid(errors);
        }

        if (symbolConfig.QuantityStep <= 0m)
        {
            errors.Add(
                $"Symbol '{symbol}' has invalid QuantityStep '{symbolConfig.QuantityStep}'. QuantityStep must be greater than zero.");
            return OrderValidationResult.Invalid(errors);
        }

        if (!IsMultipleOfStep(quantity, symbolConfig.QuantityStep))
        {
            errors.Add(
                $"Execution quantity {quantity} for symbol '{symbol}' does not align to QuantityStep {symbolConfig.QuantityStep}.");
        }

        if (symbolConfig.MinNotional > 0m && referencePrice > 0m)
        {
            var notional = quantity * referencePrice;
            if (notional < symbolConfig.MinNotional)
            {
                errors.Add(
                    $"Order notional {notional:F4} for symbol '{symbol}' is below MinNotional {symbolConfig.MinNotional}.");
            }
        }

        return errors.Count == 0
            ? OrderValidationResult.Valid()
            : OrderValidationResult.Invalid(errors);
    }

    private static void ValidateNoneIntent(OrderIntent intent, List<string> errors)
    {
        if (intent.LimitPrice.HasValue)
            errors.Add("IntentType.None must not include LimitPrice.");

        if (intent.StopPrice.HasValue)
            errors.Add("IntentType.None must not include StopPrice.");
    }

    private static void ValidateActionableIntent(OrderIntent intent, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(intent.Reason))
            errors.Add("Actionable order intents must include a non-empty Reason.");

        if (intent.ExpireAfterBars.HasValue && intent.ExpireAfterBars.Value < 0)
            errors.Add("ExpireAfterBars cannot be negative.");

        ValidateTimeInForce(intent, errors);

        switch (intent.OrderType)
        {
            case OrderType.Market:
                ValidateMarketIntent(intent, errors);
                break;

            case OrderType.Limit:
                ValidateLimitIntent(intent, errors);
                break;

            case OrderType.Stop:
                ValidateStopIntent(intent, errors);
                break;

            case OrderType.StopLimit:
                ValidateStopLimitIntent(intent, errors);
                break;

            default:
                errors.Add($"Unsupported order type: {intent.OrderType}.");
                break;
        }
    }

    private static void ValidateTimeInForce(OrderIntent intent, List<string> errors)
    {
        // In this engine, IOC is only unambiguous for immediately-eligible order types.
        if (intent.TimeInForce == TimeInForce.Ioc &&
            (intent.OrderType == OrderType.Stop || intent.OrderType == OrderType.StopLimit))
        {
            errors.Add(
                $"TimeInForce.Ioc is not supported with {intent.OrderType} in the current engine because these orders may not be active on the first eligible bar.");
        }
    }

    private static void ValidateMarketIntent(OrderIntent intent, List<string> errors)
    {
        if (intent.LimitPrice.HasValue)
            errors.Add("Market orders must not include LimitPrice.");

        if (intent.StopPrice.HasValue)
            errors.Add("Market orders must not include StopPrice.");
    }

    private static void ValidateLimitIntent(OrderIntent intent, List<string> errors)
    {
        if (!intent.LimitPrice.HasValue || intent.LimitPrice.Value <= 0m)
            errors.Add("Limit orders must include LimitPrice > 0.");

        if (intent.StopPrice.HasValue)
            errors.Add("Limit orders must not include StopPrice.");
    }

    private static void ValidateStopIntent(OrderIntent intent, List<string> errors)
    {
        if (!intent.StopPrice.HasValue || intent.StopPrice.Value <= 0m)
            errors.Add("Stop orders must include StopPrice > 0.");

        if (intent.LimitPrice.HasValue)
            errors.Add("Stop orders must not include LimitPrice.");
    }

    private static void ValidateStopLimitIntent(OrderIntent intent, List<string> errors)
    {
        if (!intent.StopPrice.HasValue || intent.StopPrice.Value <= 0m)
            errors.Add("StopLimit orders must include StopPrice > 0.");

        if (!intent.LimitPrice.HasValue || intent.LimitPrice.Value <= 0m)
            errors.Add("StopLimit orders must include LimitPrice > 0.");

        if (!intent.StopPrice.HasValue || !intent.LimitPrice.HasValue)
            return;

        if (intent.IntentType == OrderIntentType.Buy && intent.LimitPrice.Value < intent.StopPrice.Value)
        {
            errors.Add(
                $"Buy StopLimit orders require LimitPrice >= StopPrice. Received LimitPrice={intent.LimitPrice.Value}, StopPrice={intent.StopPrice.Value}.");
        }

        if (intent.IntentType == OrderIntentType.Sell && intent.LimitPrice.Value > intent.StopPrice.Value)
        {
            errors.Add(
                $"Sell StopLimit orders require LimitPrice <= StopPrice. Received LimitPrice={intent.LimitPrice.Value}, StopPrice={intent.StopPrice.Value}.");
        }
    }

    private static bool IsMultipleOfStep(decimal value, decimal step)
    {
        if (step <= 0m)
            return false;

        // Uses decimal arithmetic to avoid binary floating point issues.
        var quotient = value / step;
        return quotient == decimal.Truncate(quotient);
    }
}
