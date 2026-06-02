using Microsoft.Extensions.Options;
using PaperTradingBot.Config;

namespace PaperTradingBot.Services;

/// <summary>
/// IHostedService that auto-starts the bot when the process starts (service/systemd mode).
/// Enabled via Runtime.AutoStart = true in appsettings.json.
/// Falls back to a fresh session if no crashed session is found.
/// </summary>
public class BotAutoStartService : IHostedService
{
    private readonly BotController _controller;
    private readonly RuntimeOptions _runtime;
    private readonly ILogger<BotAutoStartService> _logger;

    public BotAutoStartService(
        BotController controller,
        IOptions<BotOptions> options,
        ILogger<BotAutoStartService> logger)
    {
        _controller = controller;
        _runtime    = options.Value.Runtime;
        _logger     = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_runtime.AutoStart) return;

        var strategy = _runtime.AutoStartStrategy ?? _runtime.StrategyName;
        _logger.LogInformation(
            "Auto-start: strategy='{Strategy}' AutoResumeOnCrash={Resume}",
            strategy, _runtime.AutoResumeOnCrash);

        // Brief delay so Blazor and all singletons finish initialising before the bot races ahead
        await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
        if (cancellationToken.IsCancellationRequested) return;

        if (_runtime.AutoResumeOnCrash)
            await _controller.ResumeOrStartAsync(strategy);
        else
            await _controller.StartAsync(strategy);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
