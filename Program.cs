using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using MudBlazor.Services;
using PaperTradingBot.AI;
using PaperTradingBot.Config;
using PaperTradingBot.Interfaces;
using PaperTradingBot.Providers.Alpaca;
using PaperTradingBot.Providers.Binance;
using PaperTradingBot.Services;
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
builder.Logging.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; });
builder.Logging.SetMinimumLevel(LogLevel.Information);

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
services.AddSingleton<AlpacaLiveMarketDataFeed>();
services.AddSingleton<BinanceLiveMarketDataFeed>();
services.AddSingleton<BinanceTestnetExecutionGateway>();

// ── Backtest dependencies ─────────────────────────────────────────────────────
services.AddSingleton<IMarketDataFeed, CsvMarketDataFeed>();
services.AddSingleton<PaperBroker>();
services.AddSingleton<BacktestEngine>();

// ── Runtimes ──────────────────────────────────────────────────────────────────
services.AddSingleton<BacktestRuntime>();
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

    var ethQty  = state.Positions.TryGetValue("ETHUSDT", out var pos) ? pos.Quantity : 0m;
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
