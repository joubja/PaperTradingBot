namespace PaperTradingBot.Interfaces;

public interface ITradingRuntime
{
    string Name { get; }

    Task RunAsync(CancellationToken cancellationToken);
}