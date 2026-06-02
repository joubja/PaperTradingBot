using System.Text.Json;
using Microsoft.Extensions.Options;
using PaperTradingBot.Config;

namespace PaperTradingBot.Services;

public class LiveSettingsService
{
    private readonly BotOptions _configDefaults;

    // ── BuildEth + BuildEthCycling: RSI accumulation signals ─────────────────
    public decimal RsiDipBuy       { get; set; }
    public decimal RsiCrossoverMax { get; set; }

    // ── BuildEthCycling: cycle triggers ───────────────────────────────────────
    public decimal RsiCycleSell       { get; set; }
    public decimal RsiCycleRebuy      { get; set; }
    public decimal CyclingReenableRsi { get; set; }

    // ── BuildEthCycling: adaptive sell fraction (0–1 fractions, e.g. 0.30 = 30%) ─
    public decimal MinSellPct     { get; set; }
    public decimal MaxSellPct     { get; set; }
    public decimal DefaultSellPct { get; set; }

    // ── BuildEthCycling: abandon & bounce (0–1 fractions, e.g. 0.015 = 1.5%) ─
    public decimal MinAbandonRise   { get; set; }
    public decimal MaxAbandonRise   { get; set; }
    public decimal MinBounceFromLow { get; set; }

    // ── BuildEthCycling: trend filter & post-cycle cooldown ───────────────────
    public decimal TrendSpreadBlock  { get; set; }
    public int     CycleCooldownBars { get; set; }

    // ── Technical strategy ────────────────────────────────────────────────────
    public decimal RsiBuyMax  { get; set; }
    public decimal RsiSellMin { get; set; }
    public int     RsiPeriod  { get; set; }

    // ── Risk ──────────────────────────────────────────────────────────────────
    public decimal MaxDailyLossPercent    { get; set; }
    public int     MaxTradesPerDay        { get; set; }
    public int     CooldownBarsAfterTrade { get; set; }
    public decimal MaxPositionValuePct    { get; set; }

    // ── Position sizing ───────────────────────────────────────────────────────
    public decimal TargetPositionValuePercent { get; set; }

    public LiveSettingsService(IOptions<BotOptions> options)
    {
        _configDefaults = options.Value;
        ResetToDefaults();
    }

    // ── Persistence ───────────────────────────────────────────────────────────

    private string? _persistPath;

    /// <summary>
    /// Saves the eight optimizer-tunable settings to disk.
    /// Remembers the path so the no-arg overload works for subsequent saves.
    /// </summary>
    public void Save(string path)
    {
        _persistPath = path;
        var dto = new LiveSettingsDto(
            RsiDipBuy, RsiCrossoverMax, RsiCycleSell, RsiCycleRebuy,
            DefaultSellPct, MinAbandonRise, MaxAbandonRise, CycleCooldownBars);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(dto));
    }

    /// <summary>Saves to the last path used by <see cref="Save(string)"/> or <see cref="TryLoad"/>.</summary>
    public void Save()
    {
        if (_persistPath is not null) Save(_persistPath);
    }

    /// <summary>
    /// Loads persisted settings. Returns true if the file existed and was applied.
    /// Remembers the path for subsequent no-arg <see cref="Save()"/> calls.
    /// </summary>
    public bool TryLoad(string path)
    {
        _persistPath = path;
        if (!File.Exists(path)) return false;
        try
        {
            var dto = JsonSerializer.Deserialize<LiveSettingsDto>(File.ReadAllText(path));
            if (dto is null) return false;
            RsiDipBuy         = dto.RsiDipBuy;
            RsiCrossoverMax   = dto.RsiCrossoverMax;
            RsiCycleSell      = dto.RsiCycleSell;
            RsiCycleRebuy     = dto.RsiCycleRebuy;
            DefaultSellPct    = dto.DefaultSellPct;
            MinAbandonRise    = dto.MinAbandonRise;
            MaxAbandonRise    = dto.MaxAbandonRise;
            CycleCooldownBars = dto.CycleCooldownBars;
            return true;
        }
        catch { return false; }
    }

    public void ResetToDefaults()
    {
        RsiDipBuy          = 40m;
        RsiCrossoverMax    = 60m;
        RsiCycleSell       = 72m;
        RsiCycleRebuy      = 45m;
        CyclingReenableRsi = 38m;
        MinSellPct         = 0.30m;
        MaxSellPct         = 0.55m;
        DefaultSellPct     = 0.40m;
        MinAbandonRise     = 0.015m;
        MaxAbandonRise     = 0.045m;
        MinBounceFromLow   = 0.001m;
        TrendSpreadBlock   = 0.12m;
        CycleCooldownBars  = 10;
        RsiBuyMax          = 65m;
        RsiSellMin         = 35m;
        RsiPeriod          = 14;

        var r = _configDefaults.Risk;
        MaxDailyLossPercent        = r.MaxDailyLossPercent;
        MaxTradesPerDay            = r.MaxTradesPerDay;
        CooldownBarsAfterTrade     = r.CooldownBarsAfterTrade;
        MaxPositionValuePct        = r.MaxPositionValuePercentPerSymbol;
        TargetPositionValuePercent = _configDefaults.PositionSizing.TargetPositionValuePercent;
    }
}

/// <summary>Persisted subset — only the eight optimizer-tunable settings.</summary>
internal sealed record LiveSettingsDto(
    decimal RsiDipBuy,
    decimal RsiCrossoverMax,
    decimal RsiCycleSell,
    decimal RsiCycleRebuy,
    decimal DefaultSellPct,
    decimal MinAbandonRise,
    decimal MaxAbandonRise,
    int     CycleCooldownBars);
