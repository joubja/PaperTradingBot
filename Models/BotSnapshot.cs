namespace PaperTradingBot.Models;

/// <summary>
/// Genuine FROZEN snapshot of one live bot's last run, exported from its SQLite DB +
/// live_settings.json (see wwwroot/snapshots/{aether,bitgain}.json). Consumed by the
/// read-only snapshot pages in the public Reality Check app — NOT live data, and the
/// bots cannot be controlled from here. Numbers are exactly what the bot recorded.
/// </summary>
public sealed record BotSnapshot
{
    public string Bot    { get; init; } = "";   // "Aether" | "BitGain"
    public string Coin   { get; init; } = "";   // "ETH" | "BTC"
    public string Symbol { get; init; } = "";   // "ETHUSDT" | "BTCUSDT"

    public SnapshotSession Session   { get; init; } = new();
    public SnapshotSettings Settings { get; init; } = new();
    public IReadOnlyList<SnapshotTrade> Trades     { get; init; } = Array.Empty<SnapshotTrade>();
    public IReadOnlyList<SnapshotEquityPoint> EquityCurve { get; init; } = Array.Empty<SnapshotEquityPoint>();

    // ── Derived, reliable metrics (session.FinalEquity/FinalCoin are null mid-run;
    //    the last equity-curve point is the source of truth). ────────────────────
    public SnapshotEquityPoint? Last => EquityCurve.Count > 0 ? EquityCurve[^1] : null;
    public decimal FinalEquityUsd => Last?.Equity ?? Session.FinalEquityUsd ?? 0m;
    public decimal FinalCoin      => Last?.CoinQty ?? Session.FinalCoin ?? 0m;
    public decimal CoinGain       => FinalCoin - Session.StartingCoin;
    public decimal CoinGainPct    => Session.StartingCoin > 0 ? CoinGain / Session.StartingCoin : 0m;
}

public sealed record SnapshotSession
{
    public string   Strategy   { get; init; } = "";
    public DateTime StartedAt  { get; init; }
    public DateTime? StoppedAt { get; init; }
    public string   Status     { get; init; } = "";
    public decimal  StartingCoin   { get; init; }
    public decimal? FinalEquityUsd { get; init; }   // null mid-run; prefer BotSnapshot.FinalEquityUsd
    public decimal? FinalCoin      { get; init; }    // null mid-run; prefer BotSnapshot.FinalCoin
    public int      TradeCount  { get; init; }
    public decimal? WinRatePct  { get; init; }
}

/// <summary>The bot's tuned cycling parameters at the time of the snapshot.</summary>
public sealed record SnapshotSettings
{
    public decimal RsiDipBuy       { get; init; }
    public decimal RsiCrossoverMax { get; init; }
    public decimal RsiCycleSell    { get; init; }
    public decimal RsiCycleRebuy   { get; init; }
    public decimal DefaultSellPct  { get; init; }
    public decimal MinAbandonRise  { get; init; }
    public decimal MaxAbandonRise  { get; init; }
    public int     CycleCooldownBars { get; init; }
}

public sealed record SnapshotTrade(
    DateTime Timestamp, string Side, decimal Quantity, decimal Price,
    decimal Commission, decimal Notional, decimal RealizedPnl, string? Note);

public sealed record SnapshotEquityPoint(DateTime T, decimal Equity, decimal? CoinQty);
