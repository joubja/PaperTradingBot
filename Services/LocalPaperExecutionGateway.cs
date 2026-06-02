using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PaperTradingBot.Config;
using PaperTradingBot.Interfaces;
using PaperTradingBot.Models;

namespace PaperTradingBot.Services;

public class LocalPaperExecutionGateway : ILocalPaperExecutionGateway
{
    private readonly BotOptions _options;
    private readonly ILogger<LocalPaperExecutionGateway> _logger;

    public LocalPaperExecutionGateway(
        IOptions<BotOptions> options,
        ILogger<LocalPaperExecutionGateway> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public Task<LocalPaperFillResult> SubmitSimulatedOrderAsync(
        DemoOrderRequest request,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return Task.FromResult(new LocalPaperFillResult
            {
                Accepted = false,
                RejectionReason = "Request is null."
            });
        }

        var result = new LocalPaperFillResult
        {
            Accepted = false,
            Symbol = request.Symbol,
            Side = request.Side,
            RequestedQuantity = request.Quantity,
            FilledQuantity = 0m,
            ReferencePrice = request.ReferencePrice,
            FillPrice = 0m,
            Commission = 0m,
            GrossNotional = 0m,
            NetCashDelta = 0m,
            TimestampUtc = DateTime.UtcNow,
            Reason = request.Reason
        };

        if (string.IsNullOrWhiteSpace(request.Symbol))
        {
            result.RejectionReason = "Symbol is required.";
            return Task.FromResult(result);
        }

        if (string.IsNullOrWhiteSpace(request.Side))
        {
            result.RejectionReason = "Side is required.";
            return Task.FromResult(result);
        }

        if (!string.Equals(request.Side, "Buy", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(request.Side, "Sell", StringComparison.OrdinalIgnoreCase))
        {
            result.RejectionReason = $"Unsupported side '{request.Side}'.";
            return Task.FromResult(result);
        }

        if (request.Quantity <= 0m)
        {
            result.RejectionReason = "Quantity must be greater than zero.";
            return Task.FromResult(result);
        }

        if (request.ReferencePrice <= 0m)
        {
            result.RejectionReason = "ReferencePrice must be greater than zero.";
            return Task.FromResult(result);
        }

        var isBuy = string.Equals(request.Side, "Buy", StringComparison.OrdinalIgnoreCase);
        var slippage = _options.SlippagePercent;

        var fillPrice = isBuy
            ? request.ReferencePrice * (1m + slippage)
            : request.ReferencePrice * (1m - slippage);

        var grossNotional = fillPrice * request.Quantity;
        var commission    = grossNotional * _options.TakerFeePercent / 100m;

        // For buys: cash leaves account => negative delta
        // For sells: cash enters account => positive delta
        var netCashDelta = isBuy
            ? -(grossNotional + commission)
            : (grossNotional - commission);

        result.Accepted = true;
        result.FilledQuantity = request.Quantity;
        result.FillPrice = fillPrice;
        result.Commission = commission;
        result.GrossNotional = grossNotional;
        result.NetCashDelta = netCashDelta;

        _logger.LogInformation(
            "LOCAL PAPER GATEWAY FILL | Symbol={Symbol} Side={Side} Qty={Qty:F4} Ref={Ref:F4} Fill={Fill:F4} Comm={Comm:F2} CashDelta={CashDelta:F2} Reason={Reason}",
            result.Symbol,
            result.Side,
            result.FilledQuantity,
            result.ReferencePrice,
            result.FillPrice,
            result.Commission,
            result.NetCashDelta,
            result.Reason);

        return Task.FromResult(result);
    }
}