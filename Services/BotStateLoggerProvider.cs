using PaperTradingBot.Services;

namespace Microsoft.Extensions.Logging;

/// <summary>
/// Forwards log entries from PaperTradingBot namespaces to BotStateService so they
/// appear in the dashboard Live Log panel.
/// </summary>
public sealed class BotStateLoggerProvider : ILoggerProvider
{
    private readonly BotStateService _state;

    public BotStateLoggerProvider(BotStateService state) => _state = state;

    public ILogger CreateLogger(string categoryName) =>
        categoryName.StartsWith("PaperTradingBot", StringComparison.Ordinal)
            ? (ILogger)new BotStateLogger(_state)
            : new NoopLogger();

    public void Dispose() { }
}

file sealed class NoopLogger : ILogger
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => false;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) { }
}

file sealed class BotStateLogger : ILogger
{
    private readonly BotStateService _state;

    public BotStateLogger(BotStateService state) => _state = state;

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;
        var prefix = logLevel switch
        {
            LogLevel.Warning  => "⚠ ",
            LogLevel.Error    => "✖ ",
            LogLevel.Critical => "✖ ",
            _                 => ""
        };
        _state.NotifyLog(prefix + formatter(state, exception));
    }
}
