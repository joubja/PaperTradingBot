using System.Globalization;
using System.IO.Compression;

// ─────────────────────────────────────────────────────────────────────────────
// FetchKlines — Phase 0 backtest data pipeline.
//
// Downloads Binance 1-second spot klines (daily dumps from data.binance.vision),
// aggregates them to N-second OHLCV bars, and writes a CSV in the exact format
// CsvMarketDataFeed consumes:  Timestamp,Open,High,Low,Close,Volume  (ISO-8601 UTC).
//
// The bot decides on 10s bars (aggregating to 1-min internally), so the default
// bucket is 10s — backtesting on coarser bars would test a different strategy.
//
// Usage:
//   dotnet run --project tools/FetchKlines -- \
//       --symbol ETHUSDT --from 2026-05-23 --to 2026-06-12 \
//       --out data/backtest/ETHUSDT-10s.csv [--bucket 10]
// ─────────────────────────────────────────────────────────────────────────────

var opts = ParseArgs(args);
if (opts is null) return 1;
var (symbol, from, to, outPath, bucketSeconds) = opts.Value;

Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);

using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
http.DefaultRequestHeaders.UserAgent.ParseAdd("PaperTradingBot-FetchKlines/1.0");

long totalSrcRows = 0, totalOutBars = 0, missingDays = 0;
DateTime? firstBar = null, lastBar = null;

await using (var writer = new StreamWriter(outPath, append: false))
{
    await writer.WriteLineAsync("Timestamp,Open,High,Low,Close,Volume");

    for (var day = from; day <= to; day = day.AddDays(1))
    {
        var dateStr = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var url = $"https://data.binance.vision/data/spot/daily/klines/{symbol}/1s/{symbol}-1s-{dateStr}.zip";

        byte[] zipBytes;
        try
        {
            using var resp = await http.GetAsync(url);
            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                Console.Error.WriteLine($"  [skip] {dateStr} — daily file not published (404)");
                missingDays++;
                continue;
            }
            resp.EnsureSuccessStatusCode();
            zipBytes = await resp.Content.ReadAsByteArrayAsync();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"  [warn] {dateStr} — download failed: {ex.Message}");
            missingDays++;
            continue;
        }

        var (srcRows, outBars, dayFirst, dayLast) =
            AggregateDay(zipBytes, bucketSeconds, writer);

        totalSrcRows += srcRows;
        totalOutBars += outBars;
        firstBar ??= dayFirst;
        if (dayLast.HasValue) lastBar = dayLast;

        Console.WriteLine($"  {dateStr}: {srcRows,6} 1s rows → {outBars,5} {bucketSeconds}s bars");
    }
}

Console.WriteLine();
Console.WriteLine($"DONE  {symbol}  {from:yyyy-MM-dd}…{to:yyyy-MM-dd}");
Console.WriteLine($"  source 1s rows : {totalSrcRows:N0}");
Console.WriteLine($"  output {bucketSeconds}s bars: {totalOutBars:N0}");
Console.WriteLine($"  missing days   : {missingDays}");
Console.WriteLine($"  coverage       : {firstBar:u} … {lastBar:u}");
Console.WriteLine($"  written to     : {Path.GetFullPath(outPath)}");

// ── Self-validation against the CsvMarketDataFeed parse contract ──────────────
Console.WriteLine();
Console.WriteLine("VALIDATING output against CsvMarketDataFeed parse rules…");
var problems = ValidateOutput(outPath, bucketSeconds);
if (problems.Count == 0)
{
    Console.WriteLine("  ✓ all rows parse; timestamps strictly increasing & evenly spaced; OHLC invariants hold.");
    return 0;
}
foreach (var p in problems.Take(20)) Console.Error.WriteLine($"  ✗ {p}");
Console.Error.WriteLine($"  {problems.Count} problem(s) found.");
return 2;

// ─────────────────────────────────────────────────────────────────────────────

static (long srcRows, long outBars, DateTime? first, DateTime? last) AggregateDay(
    byte[] zipBytes, int bucketSeconds, StreamWriter writer)
{
    using var zip = new ZipArchive(new MemoryStream(zipBytes), ZipArchiveMode.Read);
    var entry = zip.Entries.FirstOrDefault(e => e.Name.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException("zip has no CSV entry");

    using var reader = new StreamReader(entry.Open());

    long srcRows = 0, outBars = 0;
    DateTime? first = null, last = null;

    long curBucket = -1;
    decimal o = 0, h = 0, l = 0, c = 0, v = 0;

    string? line;
    while ((line = reader.ReadLine()) is not null)
    {
        if (line.Length == 0) continue;
        var f = line.Split(',');
        if (f.Length < 6) continue;

        srcRows++;
        var sec    = ToUnixSeconds(long.Parse(f[0], CultureInfo.InvariantCulture));
        var open   = decimal.Parse(f[1], CultureInfo.InvariantCulture);
        var high   = decimal.Parse(f[2], CultureInfo.InvariantCulture);
        var low    = decimal.Parse(f[3], CultureInfo.InvariantCulture);
        var close  = decimal.Parse(f[4], CultureInfo.InvariantCulture);
        var vol    = decimal.Parse(f[5], CultureInfo.InvariantCulture);
        var bucket = sec - (sec % bucketSeconds);

        if (bucket != curBucket)
        {
            if (curBucket >= 0)
            {
                Flush(writer, curBucket, o, h, l, c, v);
                outBars++;
                last = DateTimeOffset.FromUnixTimeSeconds(curBucket).UtcDateTime;
            }
            curBucket = bucket;
            o = open; h = high; l = low; c = close; v = vol;
            first ??= DateTimeOffset.FromUnixTimeSeconds(bucket).UtcDateTime;
        }
        else
        {
            if (high > h) h = high;
            if (low  < l) l = low;
            c  = close;
            v += vol;
        }
    }

    if (curBucket >= 0)
    {
        Flush(writer, curBucket, o, h, l, c, v);
        outBars++;
        last = DateTimeOffset.FromUnixTimeSeconds(curBucket).UtcDateTime;
    }

    return (srcRows, outBars, first, last);
}

static void Flush(StreamWriter w, long bucketSec, decimal o, decimal h, decimal l, decimal c, decimal v)
{
    var ts = DateTimeOffset.FromUnixTimeSeconds(bucketSec).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ");
    w.WriteLine($"{ts},{o},{h},{l},{c},{v}");
}

// Binance kline open_time is epoch ms on older data, microseconds on newer (2025+) data.
// Normalise to whole seconds regardless.
static long ToUnixSeconds(long raw) => raw switch
{
    >= 1_000_000_000_000_000 => raw / 1_000_000, // microseconds (16 digits)
    >= 1_000_000_000_000     => raw / 1_000,     // milliseconds (13 digits)
    _                        => raw              // already seconds
};

static List<string> ValidateOutput(string path, int bucketSeconds)
{
    var problems = new List<string>();
    DateTime? prev = null;
    var lineNo = 0;
    foreach (var raw in File.ReadLines(path))
    {
        lineNo++;
        if (lineNo == 1) continue; // header
        if (raw.Length == 0) continue;
        var f = raw.Split(',');
        if (f.Length < 6) { problems.Add($"line {lineNo}: <6 fields"); continue; }

        // Same parse rules CsvMarketDataFeed uses.
        DateTime ts;
        try
        {
            ts = DateTime.Parse(f[0], CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
        }
        catch { problems.Add($"line {lineNo}: bad timestamp '{f[0]}'"); continue; }

        if (!decimal.TryParse(f[1], NumberStyles.Any, CultureInfo.InvariantCulture, out var o) ||
            !decimal.TryParse(f[2], NumberStyles.Any, CultureInfo.InvariantCulture, out var h) ||
            !decimal.TryParse(f[3], NumberStyles.Any, CultureInfo.InvariantCulture, out var l) ||
            !decimal.TryParse(f[4], NumberStyles.Any, CultureInfo.InvariantCulture, out var c) ||
            !decimal.TryParse(f[5], NumberStyles.Any, CultureInfo.InvariantCulture, out _))
        { problems.Add($"line {lineNo}: non-numeric OHLCV"); continue; }

        if (h < o || h < c || h < l) problems.Add($"line {lineNo}: high {h} below open/close/low");
        if (l > o || l > c)          problems.Add($"line {lineNo}: low {l} above open/close");

        if (prev is { } p)
        {
            if (ts <= p) problems.Add($"line {lineNo}: timestamp {ts:u} not after previous {p:u}");
            else if ((ts - p) != TimeSpan.FromSeconds(bucketSeconds) && ts.Date == p.Date)
                problems.Add($"line {lineNo}: gap {(ts - p).TotalSeconds:F0}s (expected {bucketSeconds}s) at {ts:u}");
        }
        prev = ts;
    }
    return problems;
}

static (string symbol, DateTime from, DateTime to, string outPath, int bucket)? ParseArgs(string[] args)
{
    string? symbol = null, fromS = null, toS = null, outPath = null;
    var bucket = 10;
    for (var i = 0; i < args.Length - 1; i++)
    {
        switch (args[i])
        {
            case "--symbol": symbol = args[++i]; break;
            case "--from":   fromS  = args[++i]; break;
            case "--to":     toS    = args[++i]; break;
            case "--out":    outPath = args[++i]; break;
            case "--bucket": bucket = int.Parse(args[++i]); break;
        }
    }
    if (symbol is null || fromS is null || toS is null || outPath is null)
    {
        Console.Error.WriteLine("Usage: --symbol ETHUSDT --from yyyy-MM-dd --to yyyy-MM-dd --out path.csv [--bucket 10]");
        return null;
    }
    var from = DateTime.ParseExact(fromS, "yyyy-MM-dd", CultureInfo.InvariantCulture);
    var to   = DateTime.ParseExact(toS,   "yyyy-MM-dd", CultureInfo.InvariantCulture);
    if (to < from) { Console.Error.WriteLine("--to is before --from"); return null; }
    if (86400 % bucket != 0) { Console.Error.WriteLine("--bucket must divide 86400 evenly"); return null; }
    return (symbol.ToUpperInvariant(), from, to, outPath, bucket);
}
