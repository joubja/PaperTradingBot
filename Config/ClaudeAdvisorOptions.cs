namespace PaperTradingBot.Config;

public sealed class ClaudeAdvisorOptions
{
    public bool   Enabled         { get; set; } = false;
    public string ApiKey          { get; set; } = "";
    public string Model           { get; set; } = "claude-sonnet-4-6";
    public int    IntervalMinutes { get; set; } = 30;
    public int    MaxTokens       { get; set; } = 512;
}
