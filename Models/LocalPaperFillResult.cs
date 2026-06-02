namespace PaperTradingBot.Models;

public class LocalPaperFillResult
{
    public bool Accepted { get; set; }

    public string Symbol { get; set; } = string.Empty;

    /// <summary>
    /// "Buy" or "Sell"
    /// </summary>
    public string Side { get; set; } = string.Empty;

    public decimal RequestedQuantity { get; set; }
    public decimal FilledQuantity { get; set; }

    /// <summary>
    /// The price the caller asked the gateway to simulate against
    /// (typically the latest bar close in the current live-demo design).
    /// </summary>
    public decimal ReferencePrice { get; set; }

    /// <summary>
    /// The simulated fill price after slippage rules are applied.
    /// </summary>
    public decimal FillPrice { get; set; }

    public decimal Commission { get; set; }

    /// <summary>
    /// FillPrice * FilledQuantity
    /// </summary>
    public decimal GrossNotional { get; set; }

    /// <summary>
    /// Net cash impact on the paper account.
    /// Negative for buys, positive for sells.
    /// </summary>
    public decimal NetCashDelta { get; set; }

    public DateTime TimestampUtc { get; set; }

    public string Reason { get; set; } = string.Empty;

    public string? RejectionReason { get; set; }
}