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

    private bool     _noTradeAlertSent        = false;
    private bool     _stuckStateAlertSent     = false;
    private bool     _cyclingSuspendedAlert   = false;
    private DateOnly _lastDailySummary        = DateOnly.MinValue;

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
                await CheckStuckStateAsync();
                await CheckCyclingSuspendedAsync();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "BotMonitorService check failed");
            }

            await Task.Delay(TimeSpan.FromMinutes(10), ct);
        }
    }

    // ── Stuck-state alert: ActiveSell with no cash ────────────────────────────

    // [ALERT-01] Detects the confirmed incident scenario: the strategy believes it sold ETH
    // (cs.ActiveSell=true) but the portfolio has no USDT cash — meaning the sell never applied.
    private async Task CheckStuckStateAsync()
    {
        if (!_opts.Enabled) return;
        if (!_state.IsRunning) { _stuckStateAlertSent = false; return; }

        var phase = _state.StrategyStatus?.Phase;
        var cash  = _state.Cash;

        if (phase == "ActiveSell" && cash < 1m && !_stuckStateAlertSent)
        {
            _stuckStateAlertSent = true;
            var ethQty = _state.Positions.TryGetValue("ETHUSDT", out var pos) ? pos.Quantity : 0m;
            _logger.LogCritical(
                "STUCK_STATE | Phase=ActiveSell but Cash={Cash:F2} USDT — sell may not have applied to portfolio. ETH held={Eth:F5}",
                cash, ethQty);
            await _notify.SendAsync(
                "CRITICAL: Bot stuck in ActiveSell with no cash",
                $"Phase=ActiveSell but Cash={cash:F2} USDT.\n" +
                $"The sell likely did not apply to the portfolio — manual intervention required.\n\n" +
                $"ETH held:  {ethQty:F5}\n" +
                $"Status:    {_state.StrategyStatus?.Summary ?? "unknown"}");
        }
        else if (phase != "ActiveSell")
        {
            _stuckStateAlertSent = false;
        }
    }

    // ── Cycling suspension alert ──────────────────────────────────────────────

    // [ALERT-03] Notifies when cycling suspends — the primary profit mechanism stopping
    // is a significant event that should not wait until the daily summary.
    private async Task CheckCyclingSuspendedAsync()
    {
        if (!_opts.Enabled) return;
        if (!_state.IsRunning) { _cyclingSuspendedAlert = false; return; }

        if (!_state.CyclingEnabled && !_cyclingSuspendedAlert)
        {
            _cyclingSuspendedAlert = true;
            var ethQty = _state.Positions.TryGetValue("ETHUSDT", out var pos) ? pos.Quantity : 0m;
            _logger.LogWarning("MONITOR | Cycling suspended — sending alert");
            await _notify.SendAsync(
                "Cycling suspended",
                $"The bot has suspended cycling after consecutive loss cycles.\n\n" +
                $"ETH held: {ethQty:F5}\n" +
                $"Phase:    {_state.StrategyStatus?.Phase}\n" +
                $"Status:   {_state.StrategyStatus?.Summary}\n\n" +
                $"Cycling will re-enable automatically based on RSI and elapsed time.");
        }
        else if (_state.CyclingEnabled)
        {
            _cyclingSuspendedAlert = false;
        }
    }

    // ── No-trade alert ────────────────────────────────────────────────────────

    private async Task CheckNoTradeAlert()
    {
        if (!_opts.Enabled) return;

        // Only alert when the bot is supposed to be running
        if (!_state.IsRunning) { _noTradeAlertSent = false; return; }

        var lastTrade = _state.LastTradeAt;

        // [OBS-004] If no trade has ever occurred, use session start time so the alert
        // fires after NoTradeAlertHours of running — not silently forever.
        if (lastTrade == DateTime.MinValue)
        {
            var sessionAge = _state.SessionStartedAt != DateTime.MinValue
                ? DateTime.UtcNow - _state.SessionStartedAt
                : TimeSpan.Zero;
            if (sessionAge.TotalHours < _opts.NoTradeAlertHours || _noTradeAlertSent)
                return;
            lastTrade = _state.SessionStartedAt; // use session start as reference for message
        }

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
