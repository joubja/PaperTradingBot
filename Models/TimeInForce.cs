namespace PaperTradingBot.Models;

public enum TimeInForce
{
    Gtc, //Good-Til-Cancelled (or until ExpireAfterBars)
    Day, // expires when the trading date changes
    Ioc  // Immediate-Or-Cancel (first eligible bar only): Allowed for Market & Limit. Not allowed for Stop & StopLimit.
}
