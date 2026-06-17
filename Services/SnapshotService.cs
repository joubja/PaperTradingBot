using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using PaperTradingBot.Models;

namespace PaperTradingBot.Services;

/// <summary>
/// Loads the genuine FROZEN bot snapshots (wwwroot/snapshots/*.json) exported from each
/// live bot's SQLite DB, and exposes them to the read-only snapshot pages in the public
/// Reality Check app. These are static, pre-vetted, committed files — this app never
/// touches the live bots and offers no way to control them.
/// </summary>
public sealed class SnapshotService
{
    private readonly List<BotSnapshot> _snaps = new();

    public SnapshotService(IWebHostEnvironment env, ILogger<SnapshotService> logger)
    {
        var root = env.WebRootPath ?? Path.Combine(AppContext.BaseDirectory, "wwwroot");
        var dir  = Path.Combine(root, "snapshots");
        if (!Directory.Exists(dir)) { logger.LogWarning("Snapshots dir not found: {Dir}", dir); return; }

        var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
        {
            try
            {
                var s = JsonSerializer.Deserialize<BotSnapshot>(File.ReadAllText(file), opts);
                if (s is not null && !string.IsNullOrEmpty(s.Bot)) _snaps.Add(s);
            }
            catch (Exception ex) { logger.LogError(ex, "Failed to load snapshot {File}", file); }
        }
        // Stable order: Aether (ETH) first, then BitGain (BTC).
        _snaps.Sort((a, b) => string.Compare(Key(a), Key(b), StringComparison.OrdinalIgnoreCase));
        logger.LogInformation("Snapshots: loaded {Count} bot snapshots.", _snaps.Count);
    }

    public bool HasData => _snaps.Count > 0;

    /// <summary>All bot snapshots, Aether then BitGain.</summary>
    public IReadOnlyList<BotSnapshot> All => _snaps;

    /// <summary>URL-friendly bot key, e.g. "aether", "bitgain".</summary>
    public static string Key(BotSnapshot s) => s.Bot.ToLowerInvariant();

    /// <summary>Look up one bot by its url key; falls back to the first snapshot.</summary>
    public BotSnapshot? ByKey(string? key) =>
        _snaps.FirstOrDefault(s => Key(s).Equals(key, StringComparison.OrdinalIgnoreCase))
        ?? _snaps.FirstOrDefault();
}
