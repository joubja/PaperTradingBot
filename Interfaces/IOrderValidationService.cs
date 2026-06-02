using PaperTradingBot.Config;
using PaperTradingBot.Models;

namespace PaperTradingBot.Interfaces;

public interface IOrderValidationService
{
    OrderValidationResult ValidateIntent(OrderIntent intent, string symbol);
    OrderValidationResult ValidatePendingOrder(PendingOrder order, string symbol);
    OrderValidationResult ValidateSymbolConfig(SymbolConfig symbolConfig);
    OrderValidationResult ValidateExecutionQuantity(decimal quantity, SymbolConfig symbolConfig, string symbol);
    OrderValidationResult ValidateExecutionQuantity(decimal quantity, decimal referencePrice, SymbolConfig symbolConfig, string symbol);
}