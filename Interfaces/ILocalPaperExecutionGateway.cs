using PaperTradingBot.Models;

namespace PaperTradingBot.Interfaces;

public interface ILocalPaperExecutionGateway
{
    Task<LocalPaperFillResult> SubmitSimulatedOrderAsync(
        DemoOrderRequest request,
        CancellationToken cancellationToken);
}