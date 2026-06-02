namespace PaperTradingBot.Models;

public class QuoteTick
{
    public string Symbol { get; set; } = string.Empty;
    public decimal? Bid { get; set; }
    public decimal? Ask { get; set; }
    public decimal? Last { get; set; }
    public DateTime TimestampUtc { get; set; }
    public string Provider { get; set; } = string.Empty;
    public string RawPayload { get; set; } = string.Empty;
}