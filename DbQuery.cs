// Temporary query script — run with: dotnet script DbQuery.cs (or dotnet run)
using Microsoft.Data.Sqlite;

var db = "paperbotdata.db";
using var conn = new SqliteConnection($"Data Source={db};Mode=ReadOnly");
conn.Open();

static void Print(SqliteConnection c, string title, string sql)
{
    Console.WriteLine($"\n{'=',1} {title} {'=',1}");
    using var cmd = c.CreateCommand();
    cmd.CommandText = sql;
    using var r = cmd.ExecuteReader();
    var cols = Enumerable.Range(0, r.FieldCount).Select(i => r.GetName(i)).ToList();
    Console.WriteLine(string.Join(" | ", cols.Select(c => c.PadRight(18))));
    Console.WriteLine(new string('-', cols.Count * 20));
    while (r.Read())
        Console.WriteLine(string.Join(" | ", cols.Select((c, i) => $"{r.GetValue(i)}".PadRight(18))));
}

Print(conn, "SESSIONS", """
    SELECT substr(Id,1,8) Id, Strategy, substr(StartedAt,1,19) Started, substr(StoppedAt,1,19) Stopped,
           StartingCash, round(FinalEquity,2) FinalEquity, Status
    FROM Sessions ORDER BY StartedAt DESC LIMIT 8
""");

Print(conn, "ALL TRADES", """
    SELECT substr(Timestamp,12,8) Time, Symbol, Side,
           round(Quantity,4) Qty, round(Price,2) Price,
           round(Commission,4) Comm, round(RealizedPnL,4) PnL,
           substr(Note,1,50) Note
    FROM Trades ORDER BY Timestamp DESC LIMIT 40
""");

Print(conn, "CYCLES", """
    SELECT Symbol, round(SoldQuantity,4) Sold, round(SellPrice,2) SellPx,
           round(BoughtQuantity,4) Bought, round(BuyPrice,2) BuyPx,
           round(NetEthGain,5) NetEthGain, IsComplete, IsAbandoned,
           substr(SellTimestamp,12,8) SellTime
    FROM CyclingCycles ORDER BY SellTimestamp DESC LIMIT 15
""");

// Summary stats per session
Print(conn, "SESSION TRADE SUMMARY", """
    SELECT substr(s.Id,1,8) SessId, s.Strategy,
           count(t.Id) Trades,
           round(sum(CASE WHEN t.Side='Buy'  THEN t.Quantity ELSE 0 END),4) EthBought,
           round(sum(CASE WHEN t.Side='Sell' THEN t.Quantity ELSE 0 END),4) EthSold,
           round(sum(t.Commission),4) TotalComm,
           round(sum(t.RealizedPnL),2) TotalPnL
    FROM Sessions s LEFT JOIN Trades t ON t.SessionId=s.Id
    GROUP BY s.Id ORDER BY s.StartedAt DESC LIMIT 8
""");
