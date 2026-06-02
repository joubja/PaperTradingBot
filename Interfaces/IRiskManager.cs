using PaperTradingBot.Models;

namespace PaperTradingBot.Interfaces;

public interface IRiskManager
{
    RiskCheckResult CanQueueOrder(RiskCheckContext context);
    void RegisterExecutedTrade(DateOnly tradeDate, string symbol, int symbolBarCount);
    void BeginDayIfNeeded(DateOnly date, decimal currentEquity);
}
