using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PaperTradingBot.Config;
using PaperTradingBot.Interfaces;

namespace PaperTradingBot.Services;

public class BacktestRuntime : ITradingRuntime
{
    private readonly BotOptions _options;
    private readonly ILogger<BacktestRuntime> _logger;
    private readonly BacktestEngine _backtestEngine;
    private readonly AnalyticsService _analyticsService;

    public string Name => "Backtest";

    public BacktestRuntime(
        IOptions<BotOptions> options,
        ILogger<BacktestRuntime> logger,
        BacktestEngine backtestEngine,
        AnalyticsService analyticsService)
    {
        _options = options.Value;
        _logger = logger;
        _backtestEngine = backtestEngine;
        _analyticsService = analyticsService;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Running BACKTEST mode...");

        var result = await _backtestEngine.RunAsync(cancellationToken);

        var overallSummary = _analyticsService.BuildOverallSummary(result);
        var perSymbolSummaries = _analyticsService.BuildPerSymbolSummaries(result);

        _analyticsService.PrintOverallSummary(overallSummary);
        _analyticsService.PrintPerSymbolSummaries(perSymbolSummaries);
        _analyticsService.PrintTrades(result.Trades);

        var outputFolder = Path.Combine(AppContext.BaseDirectory, _options.OutputFolder, "backtest");
        Directory.CreateDirectory(outputFolder);

        CsvExporter.ExportTrades(Path.Combine(outputFolder, "backtest_trades.csv"), result.Trades);
        CsvExporter.ExportEquityCurve(Path.Combine(outputFolder, "backtest_equity_curve.csv"), result.EquityCurve);
        CsvExporter.ExportSummaries(Path.Combine(outputFolder, "backtest_symbol_summaries.csv"), perSymbolSummaries);

        _logger.LogInformation("Backtest results exported to {OutputFolder}", outputFolder);
    }
}