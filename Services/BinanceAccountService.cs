using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using PaperTradingBot.Config;

namespace PaperTradingBot.Services;

/// <summary>
/// Reads account balances from Binance REST API (testnet or live).
/// Used for pre-go-live wallet reconciliation — compare real wallet to virtual portfolio.
/// Results are cached for 30 seconds to avoid hammering the API.
/// </summary>
public sealed class BinanceAccountService
{
    public sealed record WalletSnapshot(
        decimal  BaseFree,
        decimal  UsdtFree,
        DateTime FetchedAt,
        bool     Success,
        string   BaseCurrency = "ETH",
        string?  Error = null);

    private readonly IHttpClientFactory              _factory;
    private readonly BinanceOptions                  _options;
    private readonly string                          _baseCurrency;
    private readonly ILogger<BinanceAccountService>  _logger;

    private WalletSnapshot? _cached;
    private DateTime        _lastFetch = DateTime.MinValue;
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);

    public BinanceAccountService(
        IHttpClientFactory factory,
        IOptions<BotOptions> options,
        ILogger<BinanceAccountService> logger)
    {
        _factory = factory;
        _options = options.Value.Providers.Binance;
        _logger  = logger;

        var primarySymbol = options.Value.Symbols.FirstOrDefault()?.Symbol ?? "ETHUSDT";
        _baseCurrency = primarySymbol.Replace("USDT", "").Replace("usdt", "").ToUpperInvariant();
    }

    public WalletSnapshot? Cached => _cached;

    public async Task<WalletSnapshot> GetBalancesAsync(CancellationToken ct = default)
    {
        if (_cached != null && DateTime.UtcNow - _lastFetch < CacheTtl)
            return _cached;

        if (string.IsNullOrWhiteSpace(_options.ApiKey) || string.IsNullOrWhiteSpace(_options.ApiSecret))
        {
            _cached = new WalletSnapshot(0, 0, DateTime.UtcNow, false, _baseCurrency, "API key/secret not configured");
            _lastFetch = DateTime.UtcNow;
            return _cached;
        }

        try
        {
            var timestamp   = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
            var queryString = $"timestamp={timestamp}";
            var signature   = Sign(queryString, _options.ApiSecret);
            var url         = $"{_options.RestApiUrl}/api/v3/account?{queryString}&signature={signature}";

            using var http = _factory.CreateClient();
            http.DefaultRequestHeaders.Add("X-MBX-APIKEY", _options.ApiKey);

            var resp = await http.GetAsync(url, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);

            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("WALLET CHECK | HTTP {Status}: {Body}",
                    (int)resp.StatusCode, body[..Math.Min(300, body.Length)]);
                _cached    = new WalletSnapshot(0, 0, DateTime.UtcNow, false, _baseCurrency, $"HTTP {(int)resp.StatusCode}");
                _lastFetch = DateTime.UtcNow;
                return _cached;
            }

            using var doc      = JsonDocument.Parse(body);
            var       balances = doc.RootElement.GetProperty("balances");

            decimal baseFree = 0m, usdtFree = 0m;
            foreach (var b in balances.EnumerateArray())
            {
                var asset = b.GetProperty("asset").GetString();
                if (asset == _baseCurrency && decimal.TryParse(b.GetProperty("free").GetString(), out var e)) baseFree = e;
                if (asset == "USDT"        && decimal.TryParse(b.GetProperty("free").GetString(), out var u)) usdtFree = u;
            }

            _logger.LogInformation("WALLET CHECK | {Currency}={Base:F5} USDT={Usdt:F2}", _baseCurrency, baseFree, usdtFree);
            _cached    = new WalletSnapshot(baseFree, usdtFree, DateTime.UtcNow, true, _baseCurrency);
            _lastFetch = DateTime.UtcNow;
            return _cached;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "WALLET CHECK | Request failed");
            _cached    = new WalletSnapshot(0, 0, DateTime.UtcNow, false, _baseCurrency, ex.Message[..Math.Min(100, ex.Message.Length)]);
            _lastFetch = DateTime.UtcNow;
            return _cached;
        }
    }

    private static string Sign(string data, string secret)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return BitConverter.ToString(hmac.ComputeHash(Encoding.UTF8.GetBytes(data)))
                           .Replace("-", "")
                           .ToLowerInvariant();
    }
}
