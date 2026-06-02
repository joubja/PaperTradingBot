using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PaperTradingBot.Interfaces;

namespace PaperTradingBot.Services;

public class TradingHostedService : BackgroundService
{
    private readonly ILogger<TradingHostedService> _logger;
    private readonly ITradingRuntime _runtime;
    private readonly IHostApplicationLifetime _lifetime;

    public TradingHostedService(
        ILogger<TradingHostedService> logger,
        ITradingRuntime runtime,
        IHostApplicationLifetime lifetime)
    {
        _logger = logger;
        _runtime = runtime;
        _lifetime = lifetime;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            _logger.LogInformation("Starting runtime: {RuntimeName}", _runtime.Name);
            await _runtime.RunAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Runtime cancelled: {RuntimeName}", _runtime.Name);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Runtime failed: {RuntimeName}", _runtime.Name);
        }
        finally
        {
            _logger.LogInformation("Stopping application from runtime: {RuntimeName}", _runtime.Name);
            _lifetime.StopApplication();
        }
    }
}
