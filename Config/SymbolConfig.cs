namespace PaperTradingBot.Config;

public class SymbolConfig
{
    /// <summary>
    /// Internal symbol used by the bot.
    /// Example: TEST, EURUSD
    /// </summary>
    public string Symbol { get; set; } = string.Empty;

    /// <summary>
    /// Used in Backtest mode.
    /// Example: data/candles.csv
    /// </summary>
    public string FilePath { get; set; } = string.Empty;

    /// <summary>
    /// Used in LiveDemo mode if the provider symbol differs from Symbol.
    /// Example:
    ///   Symbol = EURUSD
    ///   ProviderSymbol = EUR_USD
    /// </summary>
    public string? ProviderSymbol { get; set; }

    public decimal QuantityStep { get; set; } = 0.0001m;

    /// <summary>
    /// Minimum order notional (quantity × price) required by the exchange.
    /// 0 means no minimum is enforced.
    /// Binance USDT pairs typically require 10 USDT minimum.
    /// </summary>
    public decimal MinNotional { get; set; } = 0m;
}