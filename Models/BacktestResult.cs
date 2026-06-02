namespace PaperTradingBot.Models;

public class BacktestResult
{
    public decimal StartingCash { get; set; }

    public Portfolio Portfolio { get; set; } = new();

    public List<Trade> Trades { get; set; } = new();

    public List<EquityPoint> EquityCurve { get; set; } = new();

    public Dictionary<string, decimal> LastPrices { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, IReadOnlyList<Candle>> CandlesBySymbol { get; set; } =
        new Dictionary<string, IReadOnlyList<Candle>>(StringComparer.OrdinalIgnoreCase);

    public decimal ExposurePercent { get; set; }
}