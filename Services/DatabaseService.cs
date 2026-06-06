using Dapper;
using Microsoft.Data.Sqlite;
using PaperTradingBot.Models;

namespace PaperTradingBot.Services;

public class DatabaseService : IDisposable
{
    private readonly SqliteConnection _conn;

    public DatabaseService(IConfiguration configuration)
    {
        var path = configuration.GetConnectionString("Database") ?? "Data Source=paperbotdata.db";
        _conn = new SqliteConnection(path);
        _conn.Open();

        // WAL mode: writes survive hard kills up to the last completed transaction
        _conn.Execute("PRAGMA journal_mode=WAL;");
        _conn.Execute("PRAGMA synchronous=NORMAL;");

        CreateSchema();
        MarkCrashedSessions();
    }

    // ── Schema ────────────────────────────────────────────────────────────────

    private void CreateSchema()
    {
        _conn.Execute("""
            CREATE TABLE IF NOT EXISTS Sessions (
                Id          TEXT    PRIMARY KEY,
                StartedAt   TEXT    NOT NULL,
                StoppedAt   TEXT,
                Strategy    TEXT    NOT NULL,
                StartingCash REAL   NOT NULL,
                FinalEquity REAL,
                Status      TEXT    NOT NULL DEFAULT 'Running'
            );

            CREATE TABLE IF NOT EXISTS Trades (
                Id          INTEGER PRIMARY KEY AUTOINCREMENT,
                SessionId   TEXT    NOT NULL,
                Timestamp   TEXT    NOT NULL,
                Symbol      TEXT    NOT NULL,
                Side        TEXT    NOT NULL,
                Quantity    REAL    NOT NULL,
                Price       REAL    NOT NULL,
                Commission  REAL    NOT NULL,
                Notional    REAL    NOT NULL,
                RealizedPnL REAL    NOT NULL,
                Note        TEXT,
                FOREIGN KEY (SessionId) REFERENCES Sessions(Id)
            );

            CREATE TABLE IF NOT EXISTS EquityPoints (
                Id          INTEGER PRIMARY KEY AUTOINCREMENT,
                SessionId   TEXT    NOT NULL,
                Timestamp   TEXT    NOT NULL,
                Equity      REAL    NOT NULL,
                FOREIGN KEY (SessionId) REFERENCES Sessions(Id)
            );

            CREATE TABLE IF NOT EXISTS CyclingCycles (
                Id              INTEGER PRIMARY KEY AUTOINCREMENT,
                SessionId       TEXT    NOT NULL,
                SellTimestamp   TEXT    NOT NULL,
                BuyTimestamp    TEXT,
                Symbol          TEXT    NOT NULL,
                SoldQuantity    REAL    NOT NULL,
                SellPrice       REAL    NOT NULL,
                BoughtQuantity  REAL,
                BuyPrice        REAL,
                NetEthGain      REAL,
                IsComplete      INTEGER NOT NULL DEFAULT 0,
                IsAbandoned     INTEGER NOT NULL DEFAULT 0,
                FOREIGN KEY (SessionId) REFERENCES Sessions(Id)
            );

            CREATE TABLE IF NOT EXISTS AdvisorRuns (
                Id          INTEGER PRIMARY KEY AUTOINCREMENT,
                SessionId   TEXT    NOT NULL,
                Timestamp   TEXT    NOT NULL,
                Trigger     TEXT    NOT NULL,
                Reasoning   TEXT    NOT NULL DEFAULT '',
                Changes     TEXT    NOT NULL DEFAULT '',
                FOREIGN KEY (SessionId) REFERENCES Sessions(Id)
            );

            CREATE TABLE IF NOT EXISTS BotEvents (
                Id          INTEGER PRIMARY KEY AUTOINCREMENT,
                SessionId   TEXT    NOT NULL,
                Timestamp   TEXT    NOT NULL,
                Phase       TEXT    NOT NULL,
                Description TEXT    NOT NULL DEFAULT '',
                DurationSec INTEGER,
                FOREIGN KEY (SessionId) REFERENCES Sessions(Id)
            );
            """);

        // Migration: add IsAbandoned to existing databases that predate this column
        var existingCols = _conn.Query("PRAGMA table_info(CyclingCycles)").Select(r => (string)r.name);
        if (!existingCols.Contains("IsAbandoned"))
            _conn.Execute("ALTER TABLE CyclingCycles ADD COLUMN IsAbandoned INTEGER NOT NULL DEFAULT 0");

        // Migration: add StartingEth to Sessions
        var sessionCols = _conn.Query("PRAGMA table_info(Sessions)").Select(r => (string)r.name);
        if (!sessionCols.Contains("StartingEth"))
            _conn.Execute("ALTER TABLE Sessions ADD COLUMN StartingEth REAL NOT NULL DEFAULT 0");
    }

    private void MarkCrashedSessions()
    {
        _conn.Execute(
            "UPDATE Sessions SET Status='Crashed', StoppedAt=@now WHERE Status='Running'",
            new { now = DateTime.UtcNow.ToString("o") });
    }

    // ── Sessions ──────────────────────────────────────────────────────────────

    public string StartSession(string strategy, decimal startingCash, decimal startingEth = 0m)
    {
        var id = Guid.NewGuid().ToString();
        _conn.Execute(
            "INSERT INTO Sessions (Id, StartedAt, Strategy, StartingCash, StartingEth, Status) VALUES (@id, @ts, @strat, @cash, @eth, 'Running')",
            new { id, ts = DateTime.UtcNow.ToString("o"), strat = strategy, cash = (double)startingCash, eth = (double)startingEth });
        return id;
    }

    public void EndSession(string sessionId, decimal finalEquity, string status = "Stopped")
    {
        _conn.Execute(
            "UPDATE Sessions SET StoppedAt=@ts, FinalEquity=@eq, Status=@status WHERE Id=@id",
            new { ts = DateTime.UtcNow.ToString("o"), eq = (double)finalEquity, status, id = sessionId });
    }

    public List<SessionSummary> GetRecentSessions(int limit = 20)
    {
        return _conn.Query<SessionSummary>(
            "SELECT Id, StartedAt, StoppedAt, Strategy, StartingCash, StartingEth, FinalEquity, Status FROM Sessions ORDER BY StartedAt DESC LIMIT @limit",
            new { limit }).ToList();
    }

    // ── Trades ────────────────────────────────────────────────────────────────

    public void InsertTrade(string sessionId, Trade trade)
    {
        _conn.Execute("""
            INSERT INTO Trades (SessionId,Timestamp,Symbol,Side,Quantity,Price,Commission,Notional,RealizedPnL,Note)
            VALUES (@sessionId,@ts,@symbol,@side,@qty,@price,@comm,@notional,@pnl,@note)
            """,
            new
            {
                sessionId,
                ts       = trade.Timestamp.ToString("o"),
                symbol   = trade.Symbol,
                side     = trade.Side.ToString(),
                qty      = (double)trade.Quantity,
                price    = (double)trade.Price,
                comm     = (double)trade.Commission,
                notional = (double)trade.Notional,
                pnl      = (double)trade.RealizedPnL,
                note     = trade.Note
            });
    }

    public decimal GetSessionPeakEth(string sessionId)
    {
        // Running cumulative sum via window function; take the maximum reached during the session.
        // Using the net (buys - sells) avoids the shutdown close-out sell zeroing out the result.
        var result = _conn.ExecuteScalar<double?>("""
            SELECT MAX(running) FROM (
                SELECT SUM(CASE WHEN Side='Buy' THEN Quantity ELSE -Quantity END)
                       OVER (ORDER BY Timestamp ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW) AS running
                FROM Trades
                WHERE SessionId=@id
            )
            """, new { id = sessionId });
        return result.HasValue ? (decimal)result.Value : 0m;
    }

    public decimal GetSessionFinalEth(string sessionId)
    {
        var result = _conn.ExecuteScalar<double?>("""
            SELECT SUM(CASE WHEN Side='Buy' THEN Quantity ELSE -Quantity END)
            FROM Trades
            WHERE SessionId=@id AND (Note IS NULL OR Note != 'Shutdown close-out')
            """, new { id = sessionId });
        return result.HasValue ? (decimal)result.Value : 0m;
    }

    public (int Total, int Wins) GetSessionCycleStats(string sessionId)
    {
        var total = _conn.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM CyclingCycles WHERE SessionId=@sid AND IsComplete=1 AND IsAbandoned=0",
            new { sid = sessionId });
        var wins = _conn.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM CyclingCycles WHERE SessionId=@sid AND IsComplete=1 AND IsAbandoned=0 AND NetEthGain>0",
            new { sid = sessionId });
        return (total, wins);
    }

    public List<Trade> GetSessionTrades(string sessionId)
    {
        return _conn.Query(
            "SELECT * FROM Trades WHERE SessionId=@id ORDER BY Timestamp",
            new { id = sessionId })
            .Select(r => new Trade
            {
                Timestamp   = DateTime.Parse((string)r.Timestamp),
                Symbol      = (string)r.Symbol,
                Side        = Enum.Parse<TradeSide>((string)r.Side),
                Quantity    = (decimal)(double)r.Quantity,
                Price       = (decimal)(double)r.Price,
                Commission  = (decimal)(double)r.Commission,
                RealizedPnL = (decimal)(double)r.RealizedPnL,
                Note        = (string?)r.Note ?? ""
            }).ToList();
    }

    // ── Equity points ─────────────────────────────────────────────────────────

    public void InsertEquityPoint(string sessionId, DateTime timestamp, decimal equity)
    {
        _conn.Execute(
            "INSERT INTO EquityPoints (SessionId,Timestamp,Equity) VALUES (@sid,@ts,@eq)",
            new { sid = sessionId, ts = timestamp.ToString("o"), eq = (double)equity });
    }

    public List<EquityPoint> GetSessionEquityCurve(string sessionId, int maxPoints = 500)
    {
        var all = _conn.Query<(string Ts, double Eq)>(
            "SELECT Timestamp, Equity FROM EquityPoints WHERE SessionId=@id ORDER BY Timestamp",
            new { id = sessionId }).ToList();

        // Downsample if too many points
        if (all.Count <= maxPoints)
            return all.Select(r => new EquityPoint { Timestamp = DateTime.Parse(r.Ts), Equity = (decimal)r.Eq }).ToList();

        var step = all.Count / maxPoints;
        return all.Where((_, i) => i % step == 0)
                  .Select(r => new EquityPoint { Timestamp = DateTime.Parse(r.Ts), Equity = (decimal)r.Eq })
                  .ToList();
    }

    // ── Cycling ───────────────────────────────────────────────────────────────

    public int OpenCycle(string sessionId, string symbol, decimal soldQty, decimal sellPrice)
    {
        _conn.Execute("""
            INSERT INTO CyclingCycles (SessionId,SellTimestamp,Symbol,SoldQuantity,SellPrice,IsComplete)
            VALUES (@sid,@ts,@sym,@qty,@price,0)
            """,
            new { sid = sessionId, ts = DateTime.UtcNow.ToString("o"), sym = symbol, qty = (double)soldQty, price = (double)sellPrice });
        return (int)_conn.ExecuteScalar<long>("SELECT last_insert_rowid()");
    }

    public void CloseCycle(int cycleId, decimal boughtQty, decimal buyPrice)
    {
        var netGain = boughtQty - (decimal)_conn.ExecuteScalar<double>(
            "SELECT SoldQuantity FROM CyclingCycles WHERE Id=@id", new { id = cycleId });

        _conn.Execute("""
            UPDATE CyclingCycles
            SET BuyTimestamp=@ts, BoughtQuantity=@qty, BuyPrice=@price, NetEthGain=@gain, IsComplete=1
            WHERE Id=@id
            """,
            new { ts = DateTime.UtcNow.ToString("o"), qty = (double)boughtQty, price = (double)buyPrice, gain = (double)netGain, id = cycleId });
    }

    /// <summary>
    /// Inserts a fully-completed cycle record directly — used by the post-abandon recovery rebuy
    /// which cannot update the original cycle row (already marked abandoned).
    /// Returns the new row's Id so the caller can pass it to CorrectCycleBuyQty after the fill.
    /// </summary>
    public int InsertCompletedCycle(string sessionId, string symbol,
        decimal soldQty, decimal sellPrice, DateTime sellTs,
        decimal boughtQty, decimal buyPrice)
    {
        var netGain = boughtQty - soldQty;
        _conn.Execute("""
            INSERT INTO CyclingCycles
                (SessionId, Symbol, SellTimestamp, SoldQuantity, SellPrice,
                 BuyTimestamp, BoughtQuantity, BuyPrice, NetEthGain, IsComplete, IsAbandoned)
            VALUES (@sid, @sym, @sellTs, @soldQty, @sellPrice,
                    @buyTs, @boughtQty, @buyPrice, @gain, 1, 0)
            """,
            new { sid = sessionId, sym = symbol, sellTs = sellTs.ToString("o"),
                  soldQty = (double)soldQty, sellPrice = (double)sellPrice,
                  buyTs = DateTime.UtcNow.ToString("o"),
                  boughtQty = (double)boughtQty, buyPrice = (double)buyPrice,
                  gain = (double)netGain });
        return (int)_conn.ExecuteScalar<long>("SELECT last_insert_rowid()");
    }

    /// <summary>
    /// Corrects the BoughtQuantity, BuyPrice, and NetEthGain of a completed cycle row
    /// using the actual fill from the execution gateway, replacing the pre-execution estimate.
    /// </summary>
    public void CorrectCycleBuyQty(int cycleId, decimal actualQty, decimal actualPrice)
    {
        var soldQty = (decimal)(double)_conn.ExecuteScalar<double>(
            "SELECT SoldQuantity FROM CyclingCycles WHERE Id=@id", new { id = cycleId });
        var netGain = actualQty - soldQty;
        _conn.Execute("""
            UPDATE CyclingCycles
            SET BoughtQuantity=@qty, BuyPrice=@price, NetEthGain=@gain
            WHERE Id=@id AND IsComplete=1 AND IsAbandoned=0
            """,
            new { qty = (double)actualQty, price = (double)actualPrice,
                  gain = (double)netGain, id = cycleId });
    }

    /// <summary>
    /// Removes a cycle row entirely — used when the order that was supposed to complete
    /// it was rejected by the execution gateway, leaving the pre-written estimate invalid.
    /// </summary>
    public void DeleteCycleRow(int cycleId) =>
        _conn.Execute("DELETE FROM CyclingCycles WHERE Id=@id", new { id = cycleId });

    public void MarkCycleAbandoned(int cycleId)
    {
        _conn.Execute("""
            UPDATE CyclingCycles
            SET BuyTimestamp=@ts, BoughtQuantity=0, BuyPrice=0, NetEthGain=0, IsAbandoned=1, IsComplete=1
            WHERE Id=@id
            """,
            new { ts = DateTime.UtcNow.ToString("o"), id = cycleId });
    }

    public List<CycleResult> GetRecentCompleteCycles(string sessionId, int limit = 10)
    {
        return _conn.Query("""
            SELECT SellTimestamp, BuyTimestamp, Symbol, SoldQuantity, SellPrice,
                   BoughtQuantity, BuyPrice, NetEthGain, IsAbandoned
            FROM CyclingCycles
            WHERE SessionId=@sid AND IsComplete=1
            ORDER BY BuyTimestamp DESC LIMIT @limit
            """,
            new { sid = sessionId, limit })
            .Select(MapCycleResult).ToList();
    }

    public List<CycleResult> GetRecentCompletedCyclesAllSessions(int limit = 30)
    {
        return _conn.Query("""
            SELECT SellTimestamp, BuyTimestamp, Symbol, SoldQuantity, SellPrice,
                   BoughtQuantity, BuyPrice, NetEthGain, IsAbandoned
            FROM CyclingCycles
            WHERE IsComplete=1
            ORDER BY BuyTimestamp DESC LIMIT @limit
            """,
            new { limit })
            .Select(MapCycleResult).ToList();
    }

    private static CycleResult MapCycleResult(dynamic r) => new()
    {
        SellTimestamp  = DateTime.Parse((string)r.SellTimestamp),
        // [SM-011] BuyTimestamp can be NULL for crash-interrupted cycles — guard against cast failure.
        BuyTimestamp   = r.BuyTimestamp is string bt ? DateTime.Parse(bt) : DateTime.UtcNow,
        Symbol         = (string)r.Symbol,
        SoldQuantity   = (decimal)(double)r.SoldQuantity,
        SellPrice      = (decimal)(double)r.SellPrice,
        BoughtQuantity = (decimal)(double)r.BoughtQuantity,
        BuyPrice       = (decimal)(double)r.BuyPrice,
        NetEthGain     = (decimal)(double)r.NetEthGain,
        IsAbandoned    = (long)r.IsAbandoned == 1
    };

    // ── Crash recovery ────────────────────────────────────────────────────────

    public SessionSummary? GetLastSessionWithOpenCycle(string strategy)
    {
        return _conn.QueryFirstOrDefault<SessionSummary>("""
            SELECT s.Id, s.StartedAt, s.StoppedAt, s.Strategy,
                   s.StartingCash, s.StartingEth, s.FinalEquity, s.Status
            FROM Sessions s
            WHERE s.Strategy = @strategy
            AND s.Status NOT IN ('Running', 'Archived')
            AND EXISTS (
                SELECT 1 FROM CyclingCycles c
                WHERE c.SessionId = s.Id AND c.IsComplete = 0
            )
            ORDER BY s.StartedAt DESC
            LIMIT 1
            """, new { strategy });
    }

    public void UpdateSessionStatus(string sessionId, string status)
    {
        _conn.Execute("UPDATE Sessions SET Status=@status WHERE Id=@id",
            new { status, id = sessionId });
    }

    public SessionSummary? GetLastStoppedSession(string strategy)
    {
        return _conn.QueryFirstOrDefault<SessionSummary>(
            "SELECT Id, StartedAt, StoppedAt, Strategy, StartingCash, StartingEth, FinalEquity, Status FROM Sessions WHERE Strategy=@strategy AND Status='Stopped' ORDER BY StartedAt DESC LIMIT 1",
            new { strategy });
    }

    public void ArchiveAllStoppedSessionsForStrategy(string strategy)
    {
        // Archive both Stopped AND Crashed sessions — ensures Reset/Resync produce a truly clean slate
        _conn.Execute(
            "UPDATE Sessions SET Status='Archived' WHERE Strategy=@strategy AND Status IN ('Stopped','Crashed')",
            new { strategy });
    }

    public SessionSummary? GetLastCrashedSession(string strategy)
    {
        return _conn.QueryFirstOrDefault<SessionSummary>(
            "SELECT Id, StartedAt, StoppedAt, Strategy, StartingCash, StartingEth, FinalEquity, Status FROM Sessions WHERE Status='Crashed' AND Strategy=@strategy ORDER BY StartedAt DESC LIMIT 1",
            new { strategy });
    }

    public List<OpenCycleInfo> GetOpenCyclesForSession(string sessionId)
    {
        return _conn.Query(
            "SELECT Id, Symbol, SoldQuantity, SellPrice FROM CyclingCycles WHERE SessionId=@sid AND IsComplete=0",
            new { sid = sessionId })
            .Select(r => new OpenCycleInfo(
                (int)(long)r.Id,
                (string)r.Symbol,
                (decimal)(double)r.SoldQuantity,
                (decimal)(double)r.SellPrice))
            .ToList();
    }

    public void ResumeSession(string sessionId)
    {
        _conn.Execute(
            "UPDATE Sessions SET Status='Running', StoppedAt=NULL WHERE Id=@id",
            new { id = sessionId });
    }

    // ── Advisor runs ──────────────────────────────────────────────────────────

    public decimal GetTotalCycleEthGainAllSessions()
    {
        var result = _conn.ExecuteScalar<double?>(
            "SELECT SUM(NetEthGain) FROM CyclingCycles WHERE IsComplete=1 AND IsAbandoned=0 AND NetEthGain IS NOT NULL");
        return result.HasValue ? (decimal)result.Value : 0m;
    }

    public void InsertAdvisorRun(string sessionId, string trigger, string reasoning, string changes)
    {
        _conn.Execute(
            "INSERT INTO AdvisorRuns (SessionId,Timestamp,Trigger,Reasoning,Changes) VALUES (@sid,@ts,@trig,@r,@c)",
            new { sid = sessionId, ts = DateTime.UtcNow.ToString("o"), trig = trigger, r = reasoning, c = changes });
    }

    public List<AdvisorRun> GetSessionAdvisorRuns(string sessionId)
    {
        return _conn.Query(
            "SELECT Timestamp,Trigger,Reasoning,Changes FROM AdvisorRuns WHERE SessionId=@id ORDER BY Timestamp DESC",
            new { id = sessionId })
            .Select(r => new AdvisorRun
            {
                Timestamp = DateTime.Parse((string)r.Timestamp),
                Trigger   = (string)r.Trigger,
                Reasoning = (string)r.Reasoning,
                Changes   = (string)r.Changes
            }).ToList();
    }

    // ── Bot events ────────────────────────────────────────────────────────────

    public void InsertBotEvent(string sessionId, string phase, string description, int? durationSec)
    {
        _conn.Execute(
            "INSERT INTO BotEvents (SessionId,Timestamp,Phase,Description,DurationSec) VALUES (@sid,@ts,@phase,@desc,@dur)",
            new { sid = sessionId, ts = DateTime.UtcNow.ToString("o"), phase, desc = description, dur = durationSec });
    }

    public List<BotEvent> GetSessionBotEvents(string sessionId)
    {
        return _conn.Query(
            "SELECT Timestamp,Phase,Description,DurationSec FROM BotEvents WHERE SessionId=@id ORDER BY Timestamp DESC LIMIT 500",
            new { id = sessionId })
            .Select(r => new BotEvent
            {
                Timestamp   = DateTime.Parse((string)r.Timestamp),
                Phase       = (string)r.Phase,
                Description = (string)r.Description,
                DurationSec = r.DurationSec is null ? (int?)null : (int)(long)r.DurationSec
            }).ToList();
    }

    public void Dispose() => _conn.Dispose();
}

// ── Records / view models ─────────────────────────────────────────────────────

public record OpenCycleInfo(int Id, string Symbol, decimal SoldQuantity, decimal SellPrice);

public class SessionSummary
{
    public string Id { get; set; } = "";
    public string StartedAt { get; set; } = "";
    public string? StoppedAt { get; set; }
    public string Strategy { get; set; } = "";
    public double StartingCash { get; set; }
    public double StartingEth { get; set; }
    public double? FinalEquity { get; set; }
    public string Status { get; set; } = "";

    public double ReturnPct => StartingCash > 0 && FinalEquity.HasValue
        ? (FinalEquity.Value - StartingCash) / StartingCash * 100
        : 0;
}

public class AdvisorRun
{
    public DateTime Timestamp { get; set; }
    public string Trigger { get; set; } = "";
    public string Reasoning { get; set; } = "";
    public string Changes { get; set; } = "";
}

public class BotEvent
{
    public DateTime Timestamp { get; set; }
    public string Phase { get; set; } = "";
    public string Description { get; set; } = "";
    public int? DurationSec { get; set; }

    public string FormatDuration() => DurationSec switch
    {
        null or 0        => "",
        var s when s >= 3600 => $"{s / 3600}h {(s % 3600) / 60}m",
        var s when s >= 60   => $"{s / 60}m {s % 60}s",
        var s                => $"{s}s"
    };
}

public class CycleResult
{
    public DateTime SellTimestamp { get; set; }
    public DateTime BuyTimestamp { get; set; }
    public string Symbol { get; set; } = "";
    public decimal SoldQuantity { get; set; }
    public decimal SellPrice { get; set; }
    public decimal BoughtQuantity { get; set; }
    public decimal BuyPrice { get; set; }
    public decimal NetEthGain { get; set; }
    public bool IsAbandoned { get; set; }
    public bool IsProfit => !IsAbandoned && NetEthGain > 0m;
}
