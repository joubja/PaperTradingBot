using System.Text;
using System.Text.Json;

namespace PaperTradingBot.AI;

public readonly record struct ApiResult(string? Content, bool InsufficientCredits = false);

/// <summary>
/// Thin wrapper around the Anthropic messages API.
/// Stateless — create once as a singleton; HTTP clients are pooled via IHttpClientFactory.
/// </summary>
public sealed class ClaudeApiClient
{
    private readonly IHttpClientFactory             _factory;
    private readonly ILogger<ClaudeApiClient>       _logger;
    private const string Endpoint = "https://api.anthropic.com/v1/messages";

    public ClaudeApiClient(IHttpClientFactory factory, ILogger<ClaudeApiClient> logger)
    {
        _factory = factory;
        _logger  = logger;
    }

    /// <summary>
    /// Sends a single-turn message and returns the assistant's text content, or null on failure.
    /// InsufficientCredits is set when the API returns a 400 credit-balance error.
    /// </summary>
    public async Task<ApiResult> SendAsync(
        string apiKey, string model, string systemPrompt,
        string userMessage, int maxTokens, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            _logger.LogWarning("CLAUDE API | ApiKey not configured.");
            return default;
        }

        try
        {
            using var http = _factory.CreateClient();
            http.DefaultRequestHeaders.Add("x-api-key",        apiKey);
            http.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");

            // System prompt is static — mark for caching to avoid re-tokenising it on every call
            var payload = JsonSerializer.Serialize(new
            {
                model,
                max_tokens = maxTokens,
                system = new[]
                {
                    new { type = "text", text = systemPrompt, cache_control = new { type = "ephemeral" } }
                },
                messages = new[] { new { role = "user", content = userMessage } }
            });

            using var resp = await http.PostAsync(
                Endpoint,
                new StringContent(payload, Encoding.UTF8, "application/json"),
                ct);

            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                _logger.LogWarning("CLAUDE API | HTTP {Status}: {Body}",
                    (int)resp.StatusCode, body[..Math.Min(300, body.Length)]);
                var noCredits = body.Contains("credit balance is too low", StringComparison.OrdinalIgnoreCase);
                return new ApiResult(null, noCredits);
            }

            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var content = doc.RootElement
                .GetProperty("content")[0]
                .GetProperty("text")
                .GetString();
            return new ApiResult(content);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "CLAUDE API | Request failed.");
            return default;
        }
    }
}
