using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PaperTradingBot.Config;
using PaperTradingBot.Interfaces;
using PaperTradingBot.Models;
using PaperTradingBot.Providers.Alpaca;
using PaperTradingBot.Providers.Binance;

namespace PaperTradingBot.Services;

public class TradingProviderFactory : ITradingProviderFactory
{
    private readonly IServiceProvider _serviceProvider;
    private readonly BotOptions _options;

    public TradingProviderFactory(IServiceProvider serviceProvider, IOptions<BotOptions> options)
    {
        _serviceProvider = serviceProvider;
        _options = options.Value;
    }

    public ILiveMarketDataFeed CreateMarketDataFeed()
    {
        return _options.Runtime.Provider switch
        {
            ProviderKind.Alpaca => _serviceProvider.GetRequiredService<AlpacaLiveMarketDataFeed>(),
            ProviderKind.Binance => _serviceProvider.GetRequiredService<BinanceLiveMarketDataFeed>(),
            _ => throw new InvalidOperationException($"Unsupported provider: {_options.Runtime.Provider}")
        };
    }

    public ILocalPaperExecutionGateway CreateExecutionGateway()
    {
        if (_options.Runtime.LocalPaperExecutionOnly)
            return _serviceProvider.GetRequiredService<LocalPaperExecutionGateway>();

        return _options.Runtime.Provider switch
        {
            ProviderKind.Binance => _serviceProvider.GetRequiredService<BinanceTestnetExecutionGateway>(),
            _ => throw new InvalidOperationException(
                $"No broker execution gateway for provider '{_options.Runtime.Provider}'. " +
                $"Set LocalPaperExecutionOnly=true to use local simulation.")
        };
    }
}
