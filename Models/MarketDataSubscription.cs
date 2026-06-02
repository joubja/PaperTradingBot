namespace PaperTradingBot.Models;

public class MarketDataSubscription
{
    public string Symbol { get; set; } = string.Empty;
    public string ProviderSymbol { get; set; } = string.Empty;
}