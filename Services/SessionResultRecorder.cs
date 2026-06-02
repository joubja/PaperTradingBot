using PaperTradingBot.Interfaces;
using PaperTradingBot.Models;

namespace PaperTradingBot.Services;

public class SessionResultRecorder : ISessionResultRecorder
{
    private decimal _startingCash;

    private readonly List<Trade> _trades = new();
    private readonly List<EquityPoint> _equityCurve = new();
    private readonly Dictionary<string, List<Candle>> _candlesBySymbol =
        new(StringComparer.OrdinalIgnoreCase);

    private int _exposurePoints;

    public void Reset(decimal startingCash)
    {
        _startingCash = startingCash;
        _trades.Clear();
        _equityCurve.Clear();
        _candlesBySymbol.Clear();
        _exposurePoints = 0;
    }

    public void RecordCandle(string symbol, Candle candle)
    {
        if (!_candlesBySymbol.TryGetValue(symbol, out var list))
        {
            list = new List<Candle>();
            _candlesBySymbol[symbol] = list;
        }

        list.Add(candle);
    }

    public void RecordTrade(Trade trade)
    {
        _trades.Add(new Trade
        {
            Timestamp = trade.Timestamp,
            Symbol = trade.Symbol,
            Side = trade.Side,
            Quantity = trade.Quantity,
            Price = trade.Price,
            Commission = trade.Commission,
            RealizedPnL = trade.RealizedPnL,
            Note = trade.Note
        });
    }

    public Trade? GetLastTrade() => _trades.Count > 0 ? _trades[^1] : null;

    public void RecordEquityPoint(EquityPoint point, bool exposed)
    {
        _equityCurve.Add(new EquityPoint
        {
            Timestamp = point.Timestamp,
            Equity = point.Equity
        });

        if (exposed)
        {
            _exposurePoints++;
        }
    }

    public BacktestResult BuildResult(
        Portfolio portfolio,
        IReadOnlyDictionary<string, decimal> lastPrices)
    {
        var clonedPortfolio = ClonePortfolio(portfolio);

        var candles = _candlesBySymbol.ToDictionary(
            kvp => kvp.Key,
            kvp => (IReadOnlyList<Candle>)kvp.Value.ToList(),
            StringComparer.OrdinalIgnoreCase);

        var exposurePercent = _equityCurve.Count == 0
            ? 0m
            : (decimal)_exposurePoints / _equityCurve.Count * 100m;

        return new BacktestResult
        {
            StartingCash = _startingCash,
            Portfolio = clonedPortfolio,
            Trades = _trades.ToList(),
            EquityCurve = _equityCurve.ToList(),
            LastPrices = new Dictionary<string, decimal>(lastPrices, StringComparer.OrdinalIgnoreCase),
            CandlesBySymbol = candles,
            ExposurePercent = exposurePercent
        };
    }

    private static Portfolio ClonePortfolio(Portfolio source)
    {
        var clone = new Portfolio
        {
            Cash = source.Cash
        };

        foreach (var kvp in source.Positions)
        {
            var pos = clone.GetOrCreatePosition(kvp.Key);
            pos.Quantity = kvp.Value.Quantity;
            pos.AverageEntryPrice = kvp.Value.AverageEntryPrice;
        }

        return clone;
    }
}