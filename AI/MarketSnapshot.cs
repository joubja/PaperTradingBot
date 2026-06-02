namespace PaperTradingBot.AI;

/// <summary>
/// Point-in-time capture of indicator values and bot state, used as NN input features.
/// Captured at the moment of a cycle sell signal so the outcome can be attributed to
/// the market conditions that triggered it.
/// </summary>
public sealed record MarketSnapshot
{
    public DateTime Timestamp          { get; init; }
    public string   Symbol             { get; init; } = "";
    public decimal  Close              { get; init; }

    // Decision-layer signals (1-min aggregated)
    public decimal  Rsi1m              { get; init; }

    // Execution-layer signals (10-second bars)
    public decimal  AtrPct             { get; init; }   // ATR / Close
    public decimal  MacdHistogram      { get; init; }
    public decimal  EmaSpreadPct       { get; init; }   // |EMA9-EMA21| / EMA21 × 100
    public decimal  PriceMomentum5     { get; init; }   // (close - close[5]) / close[5]

    // Portfolio state at signal time
    public decimal  CashPct            { get; init; }   // cash / totalEquity
    public decimal  EthPositionPct     { get; init; }   // ethValue / totalEquity

    // Context
    public decimal  CycleSuccessRate   { get; init; }   // 0–1, last 5 completed cycles
    public bool     AboveTrendEma      { get; init; }   // close > EMA50
    public bool     CyclingEnabled     { get; init; }
    public int      BarsSinceLastTrade { get; init; }
}
