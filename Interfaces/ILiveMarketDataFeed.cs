using PaperTradingBot.Models;

namespace PaperTradingBot.Interfaces;

public interface ILiveMarketDataFeed
{
    string ProviderName { get; }

    ProviderConnectionState ConnectionState { get; }

    event Action<QuoteTick>? OnQuote;
    event Action<string>? OnInfo;
    event Action<Exception>? OnError;

    Task ConnectAsync(
        IReadOnlyList<MarketDataSubscription> subscriptions,
        CancellationToken cancellationToken);

    Task DisconnectAsync(CancellationToken cancellationToken);
}