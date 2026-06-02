namespace PaperTradingBot.Interfaces;

public interface ITradingProviderFactory
{
    ILiveMarketDataFeed CreateMarketDataFeed();
    ILocalPaperExecutionGateway CreateExecutionGateway();
}