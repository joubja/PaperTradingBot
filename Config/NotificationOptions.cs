namespace PaperTradingBot.Config;

public class NotificationOptions
{
    public bool   Enabled             { get; set; } = true;
    public string EmailFrom           { get; set; } = "";
    public string EmailTo             { get; set; } = "";
    public string EmailAppPassword    { get; set; } = "";
    public string EmailSmtpHost       { get; set; } = "smtp.gmail.com";
    public int    EmailSmtpPort       { get; set; } = 587;
    public int    NoTradeAlertHours   { get; set; } = 24;
    public string DailySummaryTimeUtc { get; set; } = "08:00";
}
