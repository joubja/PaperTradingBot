using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using PaperTradingBot.Models;

namespace PaperTradingBot.Services;

/// <summary>
/// Loads the PRE-COMPUTED Reality Check results (wwwroot/reality-check/*.json) that ship with
/// the app, and exposes them to the public /reality-check page. We deliberately do NOT run
/// backtests on the request path: results are generated offline (where the historical CSVs
/// live), pre-vetted, deterministic, and committed — so the public numbers are exactly what we
/// stand behind, not arbitrary user runs. Regeneration is a dev-time step (see docs).
/// </summary>
public sealed class RealityCheckService
{
    private readonly List<RealityCheckResult> _results = new();

    public RealityCheckService(IWebHostEnvironment env, ILogger<RealityCheckService> logger)
    {
        var root = env.WebRootPath ?? Path.Combine(AppContext.BaseDirectory, "wwwroot");
        var dir  = Path.Combine(root, "reality-check");
        if (!Directory.Exists(dir)) { logger.LogWarning("Reality Check data dir not found: {Dir}", dir); return; }

        var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
        {
            try
            {
                var r = JsonSerializer.Deserialize<RealityCheckResult>(File.ReadAllText(file), opts);
                if (r is not null && !string.IsNullOrEmpty(r.Strategy)) _results.Add(r);
            }
            catch (Exception ex) { logger.LogError(ex, "Failed to load Reality Check result {File}", file); }
        }
        logger.LogInformation("Reality Check: loaded {Count} precomputed results.", _results.Count);
    }

    public bool HasData => _results.Count > 0;

    /// <summary>Coin symbols present, e.g. BTCUSDT, ETHUSDT.</summary>
    public IReadOnlyList<string> Coins =>
        _results.Select(r => r.Symbol).Distinct().OrderBy(s => s).ToList();

    /// <summary>Strategy keys present, ordered by the catalog below.</summary>
    public IReadOnlyList<string> Strategies =>
        _results.Select(r => r.Strategy).Distinct()
                .OrderBy(s => Array.IndexOf(StrategyOrder, s) is var i && i >= 0 ? i : int.MaxValue)
                .ToList();

    /// <summary>All results for one coin + strategy, ordered bull → flat → crash → bear.</summary>
    public IReadOnlyList<RealityCheckResult> For(string symbol, string strategy) =>
        _results.Where(r => r.Symbol == symbol && r.Strategy == strategy)
                .OrderBy(r => RegimeOrder(r.Dataset))
                .ToList();

    // ── Friendly labels ──────────────────────────────────────────────────────
    private static readonly string[] StrategyOrder =
        { "Technical", "TrendFollow", "BuildEthCycling", "ExposureController" };

    public static string StrategyLabel(string key) => key switch
    {
        "Technical"          => "Classic TA (EMA / RSI / MACD)",
        "TrendFollow"        => "Trend-following (ride trends, dodge crashes)",
        "BuildEthCycling"    => "Buy-the-dip / sell-the-bounce cycling",
        "ExposureController" => "Crash circuit-breaker (de-risk on drops)",
        _                     => key,
    };

    public static string CoinLabel(string symbol) => symbol.Replace("USDT", "");

    public static string RegimeLabel(string dataset)
    {
        if (dataset.Contains("uptrend"))        return "Bull market";
        if (dataset.Contains("flat"))           return "Flat / ranging";
        if (dataset.Contains("downtrend"))      return "Downtrend";
        if (dataset.Contains("crash-covid"))    return "Crash — COVID 2020";
        if (dataset.Contains("crash-ftx"))      return "Crash — FTX 2022";
        if (dataset.Contains("crash-yencarry")) return "Crash — Aug 2024";
        if (dataset.Contains("bleed-2021"))     return "Slow bear — 2021";
        if (dataset.Contains("bleed-celsius"))  return "Slow bear — 2022";
        if (dataset.Contains("bleed-2025"))     return "Slow bear — 2025";
        return dataset;
    }

    private static int RegimeOrder(string dataset)
    {
        if (dataset.Contains("uptrend")) return 0;
        if (dataset.Contains("flat"))    return 1;
        if (dataset.Contains("crash"))   return 2;
        if (dataset.Contains("bleed") || dataset.Contains("downtrend")) return 3;
        return 9;
    }
}
