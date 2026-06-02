using PaperTradingBot.Models;
using PaperTradingBot.Services;

namespace PaperTradingBot.Interfaces;

public interface ISessionResultRecorder
{
    void Reset(decimal startingCash);

    void RecordCandle(string symbol, Candle candle);

    void RecordTrade(Trade trade);
    Trade? GetLastTrade();

    void RecordEquityPoint(EquityPoint point, bool exposed);

    BacktestResult BuildResult(
        Portfolio portfolio,
        IReadOnlyDictionary<string, decimal> lastPrices);
}