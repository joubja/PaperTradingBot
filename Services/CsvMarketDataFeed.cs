using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PaperTradingBot.Config;
using PaperTradingBot.Interfaces;
using PaperTradingBot.Models;
using PaperTradingBot.Utilities;

namespace PaperTradingBot.Services;

public class CsvMarketDataFeed : IMarketDataFeed
{
    private readonly BotOptions _options;
    private readonly ILogger<CsvMarketDataFeed> _logger;

    public CsvMarketDataFeed(IOptions<BotOptions> options, ILogger<CsvMarketDataFeed> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public IReadOnlyDictionary<string, IReadOnlyList<Candle>> GetCandlesBySymbol()
    {
        var result = new Dictionary<string, IReadOnlyList<Candle>>(StringComparer.OrdinalIgnoreCase);

        foreach (var symbolConfig in _options.Symbols)
        {
            if (string.IsNullOrWhiteSpace(symbolConfig.Symbol))
                throw new InvalidOperationException("A symbol entry is missing the Symbol value.");

            if (string.IsNullOrWhiteSpace(symbolConfig.FilePath))
                throw new InvalidOperationException($"Symbol '{symbolConfig.Symbol}' is missing FilePath.");

            var resolvedPath = PathHelper.ResolveFile(symbolConfig.FilePath);
            _logger.LogInformation("Loading candles for {Symbol} from {Path}", symbolConfig.Symbol, resolvedPath);

            var lines = File.ReadAllLines(resolvedPath);
            if (lines.Length <= 1)
            {
                result[symbolConfig.Symbol] = Array.Empty<Candle>();
                continue;
            }

            var candles = new List<Candle>();

            foreach (var rawLine in lines.Skip(1))
            {
                if (string.IsNullOrWhiteSpace(rawLine))
                    continue;

                var parts = rawLine.Split(',');
                if (parts.Length < 6)
                    continue;

                candles.Add(new Candle
                {
                    Timestamp = DateTime.Parse(
                        parts[0],
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal),
                    Open = decimal.Parse(parts[1], CultureInfo.InvariantCulture),
                    High = decimal.Parse(parts[2], CultureInfo.InvariantCulture),
                    Low = decimal.Parse(parts[3], CultureInfo.InvariantCulture),
                    Close = decimal.Parse(parts[4], CultureInfo.InvariantCulture),
                    Volume = decimal.Parse(parts[5], CultureInfo.InvariantCulture)
                });
            }

            result[symbolConfig.Symbol] = candles.OrderBy(c => c.Timestamp).ToList();
        }

        return result;
    }
}
