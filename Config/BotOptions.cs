using PaperTradingBot.Models;

namespace PaperTradingBot.Config;

public class BotOptions
{
    public decimal StartingCash { get; set; } = 10_000m;
    public decimal StartingEth  { get; set; } = 0m;
    /// <summary>Taker fee as a percentage. 0.1 = 0.1% (Binance default).</summary>
    public decimal TakerFeePercent { get; set; } = 0.1m;
    public decimal SlippagePercent { get; set; } = 0.001m;
    public string OutputFolder { get; set; } = "output";

    public RiskOptions Risk { get; set; } = new();
    public OrderOptions Orders { get; set; } = new();
    public PositionSizingOptions PositionSizing { get; set; } = new();

    /// <summary>
    /// Existing symbols list still used for both backtest and live-demo modes.
    /// In Backtest mode, FilePath is used.
    /// In LiveDemo mode, ProviderSymbol is used.
    /// </summary>
    public List<SymbolConfig> Symbols { get; set; } = new();

    public RuntimeOptions Runtime { get; set; } = new();

    /// <summary>
    /// Added for Alpaca/OANDA provider settings.
    /// </summary>
    public ProviderOptions Providers { get; set; } = new();
}

public class RiskOptions
{
    /// <summary>0 = disabled. Stops trading if daily USD equity loss exceeds this %.</summary>
    public decimal MaxDailyLossPercent { get; set; } = 2.0m;
    public int MaxTradesPerDay { get; set; } = 6;
    public int CooldownBarsAfterTrade { get; set; } = 2;
    public decimal MaxPositionValuePercentPerSymbol { get; set; } = 0.20m;
    /// <summary>Always keep this % of StartingCash as undeployed cash. 0 = disabled.</summary>
    public decimal MinCashReservePercent { get; set; } = 0m;
}

public class OrderOptions
{
    public OrderType DefaultOrderType { get; set; } = OrderType.Market;
    public TimeInForce DefaultTimeInForce { get; set; } = TimeInForce.Gtc;

    /// <summary>
    /// Used by the demo order-intent provider when creating limit prices.
    /// Example: 0.50 = 0.50%
    /// </summary>
    public decimal LimitOffsetPercent { get; set; } = 0.50m;

    /// <summary>
    /// Used by the demo order-intent provider when creating stop prices.
    /// Example: 0.50 = 0.50%
    /// </summary>
    public decimal StopOffsetPercent { get; set; } = 0.50m;

    /// <summary>
    /// Default fallback if an intent does not explicitly set ExpireAfterBars.
    /// Null means "no bar-based expiry unless TimeInForce causes expiry".
    /// </summary>
    public int? DefaultExpireAfterBars { get; set; } = 3;
}

public class PositionSizingOptions
{
    public PositionSizerMode Mode { get; set; } = PositionSizerMode.Allocation;

    /// <summary>
    /// Used when Mode = Allocation.
    /// Example: 0.10 = 10% of total equity.
    /// </summary>
    public decimal TargetPositionValuePercent { get; set; } = 0.10m;

    /// <summary>
    /// Used when Mode = FixedQuantity.
    /// Example: 1.0 = buy/sell 1 unit.
    /// </summary>
    public decimal FixedQuantity { get; set; } = 1.0m;
}

public enum PositionSizerMode
{
    Allocation,
    FixedQuantity
}

public class RuntimeOptions
{
    /// <summary>
    /// Backtest = CSV/historical replay
    /// LiveDemo = live market data + local paper execution only
    /// </summary>
    public EngineMode Mode { get; set; } = EngineMode.Backtest;

    /// <summary>
    /// Which live-data provider to use when Mode = LiveDemo.
    /// </summary>
    public ProviderKind Provider { get; set; } = ProviderKind.Alpaca;

    /// <summary>
    /// Hard safety switch: external execution remains disabled.
    /// </summary>
    public bool LocalPaperExecutionOnly { get; set; } = true;

    /// <summary>
    /// Refuse startup if config points to production market-data endpoints while false.
    /// </summary>
    public bool AllowProductionMarketDataEndpoints { get; set; } = false;

    public int BarSeconds { get; set; } = 60;

    /// <summary>
    /// Matches a registered keyed IOrderIntentProvider. Add new strategies to Program.cs
    /// with services.AddKeyedSingleton&lt;IOrderIntentProvider, MyStrategy&gt;("MyStrategy")
    /// then set this to "MyStrategy".
    /// </summary>
    public string StrategyName { get; set; } = "Demo";

    /// <summary>If true, the bot starts automatically when the process starts (service/systemd mode).</summary>
    public bool AutoStart { get; set; } = false;

    /// <summary>Strategy to auto-start. Falls back to StrategyName if not set.</summary>
    public string? AutoStartStrategy { get; set; }

    /// <summary>If true and a crashed session exists, resumes it instead of starting fresh.</summary>
    public bool AutoResumeOnCrash { get; set; } = true;
}

public enum EngineMode
{
    Backtest,
    LiveDemo
}

public class ProviderOptions
{
    public AlpacaOptions Alpaca { get; set; } = new();
    public BinanceOptions Binance { get; set; } = new();
}

public class AlpacaOptions
{
    public bool Enabled { get; set; } = true;
    public bool UseSandbox { get; set; } = true;

    public string ApiKey { get; set; } = string.Empty;
    public string ApiSecret { get; set; } = string.Empty;

    /// <summary>
    /// Example:
    /// wss://stream.data.alpaca.markets/v2/test
    /// </summary>
    public string MarketDataUrl { get; set; } = "wss://stream.data.alpaca.markets/v2/test";

    /// <summary>
    /// Example values:
    /// quotes, bars, trades
    /// </summary>
    public string Channel { get; set; } = "quotes";
}

public class BinanceOptions
{
    public bool Enabled { get; set; } = true;

    public string ApiKey    { get; set; } = string.Empty;
    public string ApiSecret { get; set; } = string.Empty;

    /// <summary>Spot testnet: wss://testnet.binance.vision/stream</summary>
    public string WebSocketUrl { get; set; } = "wss://stream.binance.com:9443";

    /// <summary>
    /// REST base URL for account/order endpoints.
    /// Testnet: https://testnet.binance.vision  |  Live: https://api.binance.com
    /// </summary>
    public string RestApiUrl { get; set; } = "https://api.binance.com";

    public string Channel { get; set; } = "bookTicker";
}