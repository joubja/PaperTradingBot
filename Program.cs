using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using MudBlazor.Services;
using PaperTradingBot.AI;
using PaperTradingBot.Config;
using PaperTradingBot.Interfaces;
using PaperTradingBot.Providers.Alpaca;
using PaperTradingBot.Providers.Binance;
using PaperTradingBot.Services;
using System.Globalization;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);

// ── Process resilience: runs as Windows Service or systemd unit transparently ─
builder.Host.UseWindowsService();
builder.Host.UseSystemd();

// Allow 30s for graceful shutdown so the bot can close positions and flush DB
builder.Host.ConfigureHostOptions(o => o.ShutdownTimeout = TimeSpan.FromSeconds(30));

builder.Configuration
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
    .AddEnvironmentVariables();

builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "yyyy-MM-dd HH:mm:ss "; });
builder.Logging.SetMinimumLevel(LogLevel.Information);

// ── Backtest replay mode (CLI) — isolated sandbox, no web host, no live bot ────
var backtestArgs = BacktestArgs.TryParse(args);
if (backtestArgs is not null)
{
    foreach (var suffix in new[] { "", "-wal", "-shm" })
        try { File.Delete(backtestArgs.SandboxDb + suffix); } catch { /* start from a fresh sandbox DB */ }
    builder.Configuration.AddInMemoryCollection(backtestArgs.ConfigOverrides());
    builder.Logging.SetMinimumLevel(LogLevel.Warning); // silence per-bar logs; the report uses Console
}

var services = builder.Services;

// ── Config ────────────────────────────────────────────────────────────────────
services.Configure<BotOptions>(builder.Configuration.GetSection("Bot"));
services.Configure<OptimizerOptions>(builder.Configuration.GetSection("Optimizer"));
services.Configure<ClaudeAdvisorOptions>(builder.Configuration.GetSection("ClaudeAdvisor"));
services.Configure<NotificationOptions>(builder.Configuration.GetSection("Notifications"));

// ── HTTP clients (Claude API, etc.) ──────────────────────────────────────────
services.AddHttpClient();

// ── Blazor / MudBlazor ────────────────────────────────────────────────────────
services.AddRazorPages();
services.AddServerSideBlazor();
services.AddMudServices();

// Public "Reality Check" page — loads precomputed backtest results (wwwroot/reality-check).
services.AddSingleton<RealityCheckService>();

// ── Infrastructure ────────────────────────────────────────────────────────────
services.AddSingleton<DatabaseService>();
services.AddSingleton<BinanceAccountService>();
services.AddSingleton<BotStateService>();
services.AddSingleton<LiveSettingsService>();
services.AddSingleton<ILoggerProvider, BotStateLoggerProvider>();
services.AddSingleton<PerformanceTracker>();
services.AddSingleton<OptimizerStateService>();
services.AddSingleton<AccumulationTracker>();
services.AddSingleton<StrategyBanditOptimizer>();
services.AddHostedService(sp => sp.GetRequiredService<StrategyBanditOptimizer>());
services.AddSingleton<FeatureDriftDetector>(_ => new FeatureDriftDetector(MarketFeatureExtractor.FeatureCount));
services.AddSingleton<ClaudeApiClient>();
services.AddSingleton<AdvisorContextBuilder>();
services.AddSingleton<ClaudeAdvisorService>();
services.AddHostedService(sp => sp.GetRequiredService<ClaudeAdvisorService>());

// ── Core trading services ─────────────────────────────────────────────────────
services.AddSingleton<AnalyticsService>();
services.AddSingleton<IOrderValidationService, OrderValidationService>();
services.AddSingleton<IRiskManager, SimpleRiskManager>();
services.AddSingleton<ISessionResultRecorder, SessionResultRecorder>();
services.AddSingleton<IPortfolioStateStore, InMemoryPortfolioStateStore>();

// ── Strategies ────────────────────────────────────────────────────────────────
services.AddKeyedSingleton<IOrderIntentProvider, DemoOrderIntentProvider>("Demo");
services.AddKeyedSingleton<IOrderIntentProvider, TechnicalOrderIntentProvider>("Technical");
services.AddKeyedSingleton<IOrderIntentProvider, BuildEthOrderIntentProvider>("BuildEth");
services.AddKeyedSingleton<IOrderIntentProvider, BuildEthCyclingOrderIntentProvider>("BuildEthCycling");
services.AddKeyedSingleton<IOrderIntentProvider, TrendFollowOrderIntentProvider>("TrendFollow");
services.AddKeyedSingleton<IOrderIntentProvider, ExposureControllerOrderIntentProvider>("ExposureController");

// Default strategy resolved from config (used by BarDecisionPipeline on first boot)
services.AddSingleton<IOrderIntentProvider>(sp =>
{
    var opts = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<BotOptions>>().Value;
    return sp.GetRequiredKeyedService<IOrderIntentProvider>(opts.Runtime.StrategyName);
});

// ── Position sizer ────────────────────────────────────────────────────────────
services.AddSingleton<IPositionSizer>(sp =>
{
    var opts = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<BotOptions>>().Value;
    return opts.PositionSizing.Mode switch
    {
        PositionSizerMode.Allocation    => new AllocationPositionSizer(sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<BotOptions>>(), sp.GetRequiredService<LiveSettingsService>()),
        PositionSizerMode.FixedQuantity => new FixedQuantityPositionSizer(opts.PositionSizing.FixedQuantity),
        _ => throw new InvalidOperationException($"Unsupported sizing mode: {opts.PositionSizing.Mode}")
    };
});

// ── Live-demo dependencies ────────────────────────────────────────────────────
services.AddSingleton<ITradingProviderFactory, TradingProviderFactory>();
services.AddSingleton<ILocalPaperExecutionGateway, LocalPaperExecutionGateway>();
services.AddSingleton<IBarAggregationService>(sp =>
{
    var opts = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<BotOptions>>().Value;
    return new LiveBarAggregationService(opts.Runtime.BarSeconds);
});
services.AddSingleton<IBarDecisionPipeline, BarDecisionPipeline>();
services.AddSingleton<BarProcessor>();
services.AddSingleton<AlpacaLiveMarketDataFeed>();
services.AddSingleton<BinanceLiveMarketDataFeed>();
services.AddSingleton<BinanceTestnetExecutionGateway>();

// ── Backtest dependencies ─────────────────────────────────────────────────────
services.AddSingleton<IMarketDataFeed, CsvMarketDataFeed>();
services.AddSingleton<PaperBroker>();
services.AddSingleton<BacktestEngine>();

// ── Runtimes ──────────────────────────────────────────────────────────────────
services.AddSingleton<BacktestRuntime>();
services.AddSingleton<ReplayRuntime>();
services.AddSingleton<LiveDemoRuntime>();
services.AddSingleton<ITradingRuntime>(sp => sp.GetRequiredService<LiveDemoRuntime>());

// ── Bot controller (start/stop from UI or auto-start) ─────────────────────────
services.AddSingleton<BotController>();

// ── Auto-start: starts the bot automatically when running as a service ────────
services.AddHostedService<BotAutoStartService>();

// ── Notifications ─────────────────────────────────────────────────────────────
services.AddSingleton<NotificationService>();
services.AddHostedService<BotMonitorService>();

var app = builder.Build();

// ── Backtest replay: run the sandbox replay and exit (no web host / live bot) ──
if (backtestArgs is not null)
{
    await app.Services.GetRequiredService<ReplayRuntime>().RunAsync(CancellationToken.None);
    return;
}

// Recover any sessions left in Running state by previous crash
app.Services.GetRequiredService<DatabaseService>();

// Wire PerformanceTracker to receive cycle events from the strategy
var perfTracker = app.Services.GetRequiredService<PerformanceTracker>();
var botState    = app.Services.GetRequiredService<BotStateService>();
botState.OnCycleCompleted += e =>
{
    perfTracker.Record(e);
    var logger = app.Services.GetRequiredService<ILogger<Program>>();
    logger.LogInformation(
        "OPTIMIZER EVENT | Cycle {Result} | Symbol={Symbol} NetEthGain={Gain:F5} Reward={Reward:F3} RollingReward={Rolling:F3} TotalRecorded={Total}",
        e.IsAbandoned ? "ABANDONED" : "COMPLETED",
        e.Symbol, e.NetEthGain, e.Reward,
        perfTracker.RollingReward(), perfTracker.TotalRecorded);
};

if (!app.Environment.IsDevelopment())
    app.UseExceptionHandler("/Error");

app.UseStaticFiles();
app.UseRouting();
app.MapBlazorHub();
app.MapFallbackToPage("/_Host");

// ── Health endpoint ───────────────────────────────────────────────────────────
app.MapGet("/health", (BotStateService state, LiveDemoRuntime runtime, BinanceLiveMarketDataFeed feed, DatabaseService db) =>
{
    var now = DateTime.UtcNow;

    var lastBar      = runtime.LastBarAt  == DateTime.MinValue ? (TimeSpan?)null : now - runtime.LastBarAt;
    var lastQuote    = feed.LastQuoteAt   == DateTime.MinValue ? (TimeSpan?)null : now - feed.LastQuoteAt;
    var lastTradeAge = state.LastTradeAt  == DateTime.MinValue ? (TimeSpan?)null : now - state.LastTradeAt;

    var barStale  = !lastBar.HasValue   || lastBar.Value   > TimeSpan.FromMinutes(5);
    var status    = state.IsRunning && !barStale ? "healthy" : "degraded";

    var ethQty  = state.Positions.TryGetValue(state.PrimarySymbol, out var pos) ? pos.Quantity : 0m;
    var ethGain = ethQty - state.StartingEth;

    var (totalCycles, wins) = state.ActiveSessionId is not null
        ? db.GetSessionCycleStats(state.ActiveSessionId)
        : (0, 0);

    return Results.Json(new
    {
        status,
        bot = new
        {
            isRunning    = state.IsRunning,
            strategy     = state.ActiveStrategy,
            sessionId    = state.ActiveSessionId,
            uptime       = state.SessionStartedAt != DateTime.MinValue
                               ? $"{(now - state.SessionStartedAt).TotalHours:F1}h" : "—",
            lastBarAge   = lastBar.HasValue   ? $"{lastBar.Value.TotalSeconds:F0}s"   : "never",
            lastTradeAge = lastTradeAge.HasValue ? $"{lastTradeAge.Value.TotalHours:F1}h" : "never"
        },
        portfolio = new
        {
            ethHeld      = ethQty,
            ethGain,
            cashUsdt     = state.Cash,
            startingEth  = state.StartingEth
        },
        cycling = new
        {
            enabled        = state.CyclingEnabled,
            phase          = state.StrategyStatus?.Phase,
            status         = state.StrategyStatus?.Summary,
            cyclesWon      = wins,
            cyclesTotal    = totalCycles
        },
        feed = new
        {
            connectionState = feed.ConnectionState.ToString(),
            lastQuoteAge    = lastQuote.HasValue ? $"{lastQuote.Value.TotalSeconds:F0}s" : "never"
        }
    });
});

app.Run();


// ── Backtest CLI args + sandbox config overrides ──────────────────────────────
// Usage: dotnet run -- --backtest --symbol ETHUSDT --data data/backtest/ETHUSDT-10s-uptrend.csv
//                     --starting-qty 1.630248 [--slippage 0.0005]
sealed record BacktestArgs(string Symbol, string DataPath, decimal StartingQty, decimal? Slippage, string SandboxDb, string Strategy)
{
    public static BacktestArgs? TryParse(string[] args)
    {
        if (!args.Contains("--backtest")) return null;

        string? Get(string name)
        {
            var i = Array.IndexOf(args, name);
            return i >= 0 && i < args.Length - 1 ? args[i + 1] : null;
        }

        var symbol = (Get("--symbol") ?? throw new ArgumentException("--backtest requires --symbol")).ToUpperInvariant();
        var data   = Get("--data") ?? throw new ArgumentException("--backtest requires --data");
        var qty    = decimal.Parse(Get("--starting-qty") ?? throw new ArgumentException("--backtest requires --starting-qty"),
                                   CultureInfo.InvariantCulture);
        decimal? slip = Get("--slippage") is { } s ? decimal.Parse(s, CultureInfo.InvariantCulture) : null;
        var strategy  = Get("--strategy") ?? "BuildEthCycling"; // default preserves prior behaviour
        // Sandbox DB name includes a run tag (strategy + slippage + data file stem) so parallel
        // runs don't collide on the same file.
        var stem    = Path.GetFileNameWithoutExtension(data);
        var slipTag = (Get("--slippage") ?? "def").Replace(".", "p");
        return new BacktestArgs(symbol, data, qty, slip, $"data/backtest/_sandbox_{strategy}_{stem}_{slipTag}.db", strategy);
    }

    public IEnumerable<KeyValuePair<string, string?>> ConfigOverrides()
    {
        var step = Symbol.StartsWith("BTC", StringComparison.OrdinalIgnoreCase) ? "0.00001" : "0.001";
        var o = new Dictionary<string, string?>
        {
            ["ConnectionStrings:Database"]          = $"Data Source={SandboxDb}",
            ["Bot:Symbols:0:Symbol"]                = Symbol,
            ["Bot:Symbols:0:ProviderSymbol"]        = Symbol,
            ["Bot:Symbols:0:FilePath"]              = DataPath,
            ["Bot:Symbols:0:QuantityStep"]          = step,
            ["Bot:Symbols:0:MinNotional"]           = "10.0",
            ["Bot:StartingQuantity"]                = StartingQty.ToString(CultureInfo.InvariantCulture),
            ["Bot:StartingCash"]                    = "0",
            ["Bot:Runtime:LocalPaperExecutionOnly"] = "true",
            ["Bot:Runtime:StrategyName"]            = Strategy,
            ["Bot:Runtime:AutoStart"]               = "false",
            // A long-hold trend-follower must not be throttled by the per-trade cooldown or the
            // daily trade cap (0 disables MaxTradesPerDay). The strategy has its own min-hold guard.
            ["Bot:Risk:CooldownBarsAfterTrade"]     = "0",
            ["Bot:Risk:MaxTradesPerDay"]            = "0",
            ["Optimizer:Enabled"]                   = "false",
            ["ClaudeAdvisor:Enabled"]               = "false",
            ["Notifications:Enabled"]               = "false",
        };
        if (Slippage is { } sl) o["Bot:SlippagePercent"] = sl.ToString(CultureInfo.InvariantCulture);
        return o;
    }
}
