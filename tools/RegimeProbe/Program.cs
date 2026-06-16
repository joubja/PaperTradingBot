using System.Globalization;

// ─────────────────────────────────────────────────────────────────────────────
// RegimeProbe — answers ONE question: at the instant the ExposureController's
// drawdown breaker must FIRST fire (1-min close ≥ ExitDD below the trailing
// HighWindow max), can causally-available features tell apart the windows where
// de-risking HELPED (violent crash) from where it HURT (slow bleed)?
//
// Replicates the chassis HTF logic: aggregate 10s→1-min closes, 720-bar (12h)
// rolling high, ExitDD = 10%. Everything reported uses only data up to the
// trigger bar — nothing forward-looking. Labels are the measured backtest
// outcome (return edge vs B&H), passed on the CLI as won/lost.
//
// Usage: regime-probe <label> <won|lost> <csv> [<label> <won|lost> <csv> ...]
// ─────────────────────────────────────────────────────────────────────────────

const int HighWindow = 720; // 12h of 1-min bars (matches EC_HIGH_WINDOW)
const decimal ExitDD = 0.10m; // EC_EXIT_DD
const int VolWin   = 60;    // trailing minutes for realized vol / momentum

if (args.Length < 3 || args.Length % 3 != 0)
{
    Console.Error.WriteLine("Usage: regime-probe <label> <won|lost> <csv> [...]");
    return 1;
}

Console.WriteLine($"{"window",-22} {"outcome",-7} {"toFall10%",10} {"rv60(%/min)",12} {"ret60(%)",9} {"vel(%/h)",9}");
Console.WriteLine(new string('─', 74));

for (var a = 0; a < args.Length; a += 3)
{
    var label = args[a];
    var outcome = args[a + 1];
    var path = args[a + 2];

    var closes = LoadMinuteCloses(path);
    if (closes.Count < HighWindow + VolWin) { Console.Error.WriteLine($"{label}: too few bars"); continue; }

    // Find FIRST breaker trigger: first bar i where close[i] <= (1-ExitDD)*max(closes[i-W+1..i]).
    var triggered = false;
    for (var i = 1; i < closes.Count; i++)
    {
        var lo = Math.Max(0, i - HighWindow + 1);
        decimal hi = 0m; var hiIdx = lo;
        for (var k = lo; k <= i; k++) if (closes[k] > hi) { hi = closes[k]; hiIdx = k; }

        if (hi <= 0m || closes[i] > hi * (1m - ExitDD)) continue;

        // ── Trigger at bar i. Causal features (only data ≤ i). ──
        var minutesToFall = i - hiIdx;                 // how long the 10% took
        var velPctPerHour = ExitDD * 100m / (minutesToFall / 60m); // drawdown velocity %/h

        // realized vol: stdev of last VolWin 1-min simple returns, in %/min
        var start = i - VolWin;
        decimal mean = 0m;
        for (var k = start + 1; k <= i; k++) mean += (closes[k] / closes[k - 1] - 1m);
        mean /= VolWin;
        decimal var2 = 0m;
        for (var k = start + 1; k <= i; k++) { var d = (closes[k] / closes[k - 1] - 1m) - mean; var2 += d * d; }
        var rv = (decimal)Math.Sqrt((double)(var2 / VolWin)) * 100m;

        var ret60 = (closes[i] / closes[i - VolWin] - 1m) * 100m;

        Console.WriteLine($"{label,-22} {outcome,-7} {minutesToFall + "m",10} {rv,12:F3} {ret60,9:F2} {velPctPerHour,9:F1}");
        triggered = true;
        break;
    }
    if (!triggered)
        Console.WriteLine($"{label,-22} {outcome,-7} {"never",10} {"-",12} {"-",9} {"-",9}");
}
return 0;

static List<decimal> LoadMinuteCloses(string path)
{
    // 10s OHLCV → last close of each wall-clock minute (matches HTF aggregation: close = last tick).
    var closes = new List<decimal>();
    long curMin = -1; decimal lastClose = 0m; var first = true;
    foreach (var line in File.ReadLines(path))
    {
        if (first) { first = false; continue; } // header
        if (line.Length == 0) continue;
        var f = line.Split(',');
        if (f.Length < 6) continue;
        var ts = DateTime.Parse(f[0], CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
        var min = ts.Ticks / TimeSpan.TicksPerMinute;
        var c = decimal.Parse(f[4], CultureInfo.InvariantCulture);
        if (min != curMin)
        {
            if (curMin >= 0) closes.Add(lastClose);
            curMin = min;
        }
        lastClose = c;
    }
    if (curMin >= 0) closes.Add(lastClose);
    return closes;
}
