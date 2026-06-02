namespace PaperTradingBot.Models;

public class RiskCheckResult
{
    public bool Allowed { get; set; }
    public string Reason { get; set; } = string.Empty;

    public static RiskCheckResult Allow(string reason = "Allowed")
        => new() { Allowed = true, Reason = reason };

    public static RiskCheckResult Deny(string reason)
        => new() { Allowed = false, Reason = reason };
}