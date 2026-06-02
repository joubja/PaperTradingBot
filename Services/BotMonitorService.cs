using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PaperTradingBot.Config;

namespace PaperTradingBot.Services;

public class BotMonitorService : BackgroundService
{
    private readonly NotificationService _notify;
    private readonly BotStateService     _state;
    private readonly DatabaseService     _db;
    private readonly NotificationOptions _opts;
    private readonly ILogger<BotMonitorService> _logger;

    private bool     _noTradeAlertSent = false;
    private DateOnly _lastDailySummary = DateOnly.MinValue;

    public BotMonitorService(
        NotificationService notify,
        BotStateService state,
        DatabaseService db,
        IOptions<NotificationOptions> opts,
        ILogger<BotMonitorService> logger)
    {
        _notify = notify;
        _state  = state;
        _db     = db;
        _opts   = opts.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // Give the bot a couple of minutes to connect before first check
        await Task.Delay(TimeSpan.FromMinutes(2), ct);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await CheckNoTradeAlert();
                await CheckDailySummary();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "BotMonitorService check failed");
            }

            await Task.Delay(TimeSpan.FromMinutes(10), ct);
        }
    }

    // ── No-trade alert ────────────────────────────────────────────────────────

    private async Task CheckNoTradeAlert()
    {
        if (!_opts.Enabled) return;

        // Only alert when the bot is supposed to be running
        if (!_state.IsRunning) { _noTradeAlertSent = false; return; }

        var lastTrade = _state.LastTradeAt;
        if (lastTrade == DateTime.MinValue) return;   // no trade yet this session

        var silentFor = DateTime.UtcNow - lastTrade;

        if (silentFor.TotalHours >= _opts.NoTradeAlertHours && !_noTradeAlertSent)
        {
            _noTradeAlertSent = true;

            var phase   = _state.StrategyStatus?.Phase   ?? "Unknown";
            var summary = _state.StrategyStatus?.Summary ?? "";
            var ethQty  = _state.Positions.TryGetValue("ETHUSDT", out var p) ? p.Quantity : 0m;
            var ethGain = ethQty - _state.StartingEth;

            var body =
                $"No trade for {silentFor.TotalHours:F0}h — bot may be stuck.\n\n" +
                $"Phase:        {phase}\n" +
                $"Status:       {summary}\n" +
                $"ETH held:     {ethQty:F5}\n" +
                $"Session gain: {(ethGain >= 0 ? "+" : "")}{ethGain:F5} ETH\n" +
                $"Cycling:      {(_state.CyclingEnabled ? "ON" : "SUSPENDED")}\n" +
                $"Last trade:   {lastTrade:yyyy-MM-dd HH:mm} UTC";

            _logger.LogWarning("MONITOR | No trade for {Hours:F0}h — sending alert", silentFor.TotalHours);
            await _notify.SendAsync($"No trade for {silentFor.TotalHours:F0}h", body);
        }
        else if (silentFor.TotalHours < _opts.NoTradeAlertHours)
        {
            _noTradeAlertSent = false;  // reset so next drought triggers a fresh alert
        }
    }

    // ── Daily summary ─────────────────────────────────────────────────────────

    private async Task CheckDailySummary()
    {
        if (!_opts.Enabled) return;
        if (!TimeSpan.TryParse(_opts.DailySummaryTimeUtc, out var targetTime)) return;

        var now   = DateTime.UtcNow;
        var today = DateOnly.FromDateTime(now);

        if (now.TimeOfDay < targetTime) return;    // not yet time today
        if (_lastDailySummary >= today)  return;   // already sent today

        _lastDailySummary = today;

        var sessionId = _state.ActiveSessionId;
        var ethQty    = _state.Positions.TryGetValue("ETHUSDT", out var pos) ? pos.Quantity : 0m;
        var ethGain   = ethQty - _state.StartingEth;
        var gainPct   = _state.StartingEth > 0m ? ethGain / _state.StartingEth * 100m : 0m;
        var cash      = _state.Cash;
        var allTime   = _db.GetTotalCycleEthGainAllSessions();

        var (totalCycles, wins) = sessionId is not null
            ? _db.GetSessionCycleStats(sessionId)
            : (0, 0);

        var uptime = _state.SessionStartedAt != DateTime.MinValue
            ? now - _state.SessionStartedAt
            : TimeSpan.Zero;
        var uptimeTxt = uptime.TotalHours >= 1
            ? $"{(int)uptime.TotalHours}h {uptime.Minutes}m"
            : $"{uptime.Minutes}m";

        var body =
            $"Daily summary — {now:yyyy-MM-dd}\n\n" +
            $"Bot:          {(_state.IsRunning ? $"Running ({uptimeTxt})" : "Stopped")}\n" +
            $"Strategy:     {_state.ActiveStrategy}\n" +
            $"ETH held:     {ethQty:F5}\n" +
            $"Session gain: {(ethGain >= 0 ? "+" : "")}{ethGain:F5} ETH ({(gainPct >= 0 ? "+" : "")}{gainPct:F2}%)\n" +
            $"All-time:     {(allTime >= 0 ? "+" : "")}{allTime:F5} ETH\n" +
            $"Cycles:       {wins}/{totalCycles} won\n" +
            $"Cash (USDT):  ${cash:F2}\n" +
            $"Cycling:      {(_state.CyclingEnabled ? "ON" : "SUSPENDED")}";

        _logger.LogInformation("MONITOR | Sending daily summary");
        await _notify.SendAsync($"Daily summary {now:yyyy-MM-dd}", body);
    }
}
