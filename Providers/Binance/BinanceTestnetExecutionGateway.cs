using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PaperTradingBot.Config;
using PaperTradingBot.Interfaces;
using PaperTradingBot.Models;

namespace PaperTradingBot.Providers.Binance;

public class BinanceTestnetExecutionGateway : ILocalPaperExecutionGateway
{
    private const string BaseUrl = "https://testnet.binance.vision";

    private readonly BotOptions _options;
    private readonly ILogger<BinanceTestnetExecutionGateway> _logger;
    private readonly HttpClient _httpClient;

    public BinanceTestnetExecutionGateway(
        IOptions<BotOptions> options,
        ILogger<BinanceTestnetExecutionGateway> logger)
    {
        _options = options.Value;
        _logger = logger;
        _httpClient = new HttpClient { BaseAddress = new Uri(BaseUrl) };
        _httpClient.DefaultRequestHeaders.Add("X-MBX-APIKEY", _options.Providers.Binance.ApiKey);
    }

    public async Task<LocalPaperFillResult> SubmitSimulatedOrderAsync(
        DemoOrderRequest request,
        CancellationToken cancellationToken)
    {
        var result = new LocalPaperFillResult
        {
            Symbol = request.Symbol,
            Side = request.Side,
            RequestedQuantity = request.Quantity,
            ReferencePrice = request.ReferencePrice,
            Reason = request.Reason,
            TimestampUtc = DateTime.UtcNow
        };

        if (string.IsNullOrWhiteSpace(_options.Providers.Binance.ApiKey) ||
            string.IsNullOrWhiteSpace(_options.Providers.Binance.ApiSecret))
        {
            result.RejectionReason = "Binance ApiKey and ApiSecret are required for testnet execution.";
            return result;
        }

        try
        {
            var side = request.Side.ToUpperInvariant(); // BUY or SELL
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var qty = request.Quantity.ToString("F5").TrimEnd('0').TrimEnd('.');

            var queryString = $"symbol={request.Symbol}" +
                              $"&side={side}" +
                              $"&type=MARKET" +
                              $"&quantity={qty}" +
                              $"&timestamp={timestamp}";

            var signature = Sign(queryString, _options.Providers.Binance.ApiSecret);
            var url = $"/api/v3/order?{queryString}&signature={signature}";

            var response = await _httpClient.PostAsync(url, null, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Binance testnet order rejected | Symbol={Symbol} Side={Side} Status={Status} Body={Body}",
                    request.Symbol, request.Side, (int)response.StatusCode, body);

                result.RejectionReason = $"Binance rejected order: {body}";
                return result;
            }

            return ParseFillResult(result, body, request);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Binance testnet execution failed for {Symbol} {Side}", request.Symbol, request.Side);
            result.RejectionReason = ex.Message;
            return result;
        }
    }

    private LocalPaperFillResult ParseFillResult(
        LocalPaperFillResult result,
        string responseBody,
        DemoOrderRequest request)
    {
        using var doc = JsonDocument.Parse(responseBody);
        var root = doc.RootElement;

        var status = root.TryGetProperty("status", out var s) ? s.GetString() : null;
        if (!string.Equals(status, "FILLED", StringComparison.OrdinalIgnoreCase))
        {
            result.RejectionReason = $"Order status was '{status}', expected FILLED.";
            return result;
        }

        var executedQty = root.TryGetProperty("executedQty", out var eq) &&
                          decimal.TryParse(eq.GetString(), out var eqv) ? eqv : 0m;

        // Compute weighted average fill price and total commission from fills array
        decimal totalNotional = 0m;
        decimal totalCommissionUsdt = 0m;

        if (root.TryGetProperty("fills", out var fills) && fills.ValueKind == JsonValueKind.Array)
        {
            foreach (var fill in fills.EnumerateArray())
            {
                var fillPrice = fill.TryGetProperty("price", out var fp) &&
                                decimal.TryParse(fp.GetString(), out var fpv) ? fpv : 0m;
                var fillQty = fill.TryGetProperty("qty", out var fq) &&
                              decimal.TryParse(fq.GetString(), out var fqv) ? fqv : 0m;
                var commission = fill.TryGetProperty("commission", out var fc) &&
                                 decimal.TryParse(fc.GetString(), out var fcv) ? fcv : 0m;
                var commissionAsset = fill.TryGetProperty("commissionAsset", out var ca)
                    ? ca.GetString() ?? ""
                    : "";

                totalNotional += fillPrice * fillQty;

                // Convert commission to quote currency (USDT) if charged in base asset
                if (commissionAsset.EndsWith("USDT", StringComparison.OrdinalIgnoreCase) ||
                    commissionAsset.Equals("USDT", StringComparison.OrdinalIgnoreCase))
                {
                    totalCommissionUsdt += commission;
                }
                else if (fillPrice > 0m)
                {
                    totalCommissionUsdt += commission * fillPrice;
                }
            }
        }

        var avgFillPrice = executedQty > 0m ? totalNotional / executedQty : request.ReferencePrice;
        var isBuy = string.Equals(request.Side, "Buy", StringComparison.OrdinalIgnoreCase);
        var netCashDelta = isBuy
            ? -(totalNotional + totalCommissionUsdt)
            : totalNotional - totalCommissionUsdt;

        result.Accepted = true;
        result.FilledQuantity = executedQty;
        result.FillPrice = avgFillPrice;
        result.Commission = totalCommissionUsdt;
        result.GrossNotional = totalNotional;
        result.NetCashDelta = netCashDelta;
        result.TimestampUtc = DateTime.UtcNow;

        _logger.LogInformation(
            "BINANCE TESTNET FILL | Symbol={Symbol} Side={Side} Qty={Qty:F5} AvgPrice={Price:F4} Comm={Comm:F4} CashDelta={Delta:F2}",
            result.Symbol, result.Side, result.FilledQuantity, result.FillPrice,
            result.Commission, result.NetCashDelta);

        return result;
    }

    private static string Sign(string queryString, string secret)
    {
        var keyBytes = Encoding.UTF8.GetBytes(secret);
        var msgBytes = Encoding.UTF8.GetBytes(queryString);
        using var hmac = new HMACSHA256(keyBytes);
        return Convert.ToHexString(hmac.ComputeHash(msgBytes)).ToLowerInvariant();
    }
}
