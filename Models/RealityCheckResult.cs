namespace PaperTradingBot.Models;

/// <summary>
/// Machine-readable result of one backtest run, emitted by ReplayRuntime when the
/// BACKTEST_OUT_JSON env var points at a file. This is the contract the public
/// "Reality Check" tool consumes: each interactive run shells out to an isolated
/// --backtest child process (so the live paper bot's in-process state is never
/// touched) and deserializes this. Returns are fractions (0.12 = +12%).
/// </summary>
public sealed record RealityCheckResult
{
    public string Strategy { get; init; } = "";
    public string Symbol   { get; init; } = "";
    public string Dataset  { get; init; } = "";   // data file stem, e.g. "ETHUSDT-10s-crash-ftx"

    public DateTime StartUtc { get; init; }
    public DateTime EndUtc   { get; init; }
    public int      Bars     { get; init; }

    public decimal StartEquityUsd { get; init; }
    public decimal FinalEquityUsd { get; init; }

    public decimal StrategyReturn { get; init; }   // (final-start)/start
    public decimal BuyHoldReturn  { get; init; }
    public decimal EdgeReturn     { get; init; }    // strategy - buy&hold (the honest number)

    public decimal StrategyMaxDrawdown { get; init; }
    public decimal BuyHoldMaxDrawdown  { get; init; }

    public int TradeCount { get; init; }

    public decimal SlippagePercent  { get; init; } // fraction applied per fill
    public decimal TakerFeePercent  { get; init; } // percent, 0.1 = 0.1%

    /// <summary>Downsampled equity curves (≈200 pts), both rebased to 100 at the start, for charting.</summary>
    public IReadOnlyList<RcCurvePoint> Curve { get; init; } = Array.Empty<RcCurvePoint>();
}

/// <summary>One point on the Reality Check chart: strategy vs buy-and-hold, both rebased to 100.</summary>
public sealed record RcCurvePoint(DateTime T, decimal Strategy, decimal BuyHold);
