using System.Text.Json;

namespace PaperTradingBot.Utilities;

public static class JsonHelpers
{
    public static readonly JsonSerializerOptions Default = new()
    {
        PropertyNameCaseInsensitive = true
    };
}