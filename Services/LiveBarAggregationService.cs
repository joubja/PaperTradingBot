using PaperTradingBot.Interfaces;
using PaperTradingBot.Models;

namespace PaperTradingBot.Services;

public class LiveBarAggregationService : IBarAggregationService, IDisposable
{
    private readonly int _barSeconds;
    private readonly object _sync = new();
    private readonly Timer _timer;

    private readonly Dictionary<string, LiveBarBuilder> _builders =
        new(StringComparer.OrdinalIgnoreCase);

    public event Action<LiveBar>? OnBarClosed;

    public LiveBarAggregationService(int barSeconds)
    {
        if (barSeconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(barSeconds));

        _barSeconds = barSeconds;

        // Check every second for bar windows that have elapsed with no incoming quotes.
        _timer = new Timer(_ => FlushElapsedBars(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    public void Dispose() => _timer.Dispose();

    public void OnQuote(QuoteTick quote)
    {
        var px = quote.Last ?? Mid(quote.Bid, quote.Ask);
        if (!px.HasValue || px.Value <= 0m)
            return;

        lock (_sync)
        {
            if (!_builders.TryGetValue(quote.Symbol, out var builder))
            {
                builder = new LiveBarBuilder(
                    quote.Symbol,
                    quote.Provider,
                    Align(quote.TimestampUtc),
                    _barSeconds);

                _builders[quote.Symbol] = builder;
            }

            var alignedStart = Align(quote.TimestampUtc);

            while (builder.StartUtc < alignedStart)
            {
                if (builder.HasTicks)
                    OnBarClosed?.Invoke(builder.CloseCurrentBar());

                builder = new LiveBarBuilder(
                    builder.Symbol,
                    builder.Provider,
                    builder.StartUtc.AddSeconds(_barSeconds),
                    _barSeconds);

                _builders[quote.Symbol] = builder;
            }

            builder.Apply(px.Value, quote.TimestampUtc);
        }
    }

    private void FlushElapsedBars()
    {
        var alignedNow = Align(DateTime.UtcNow);

        lock (_sync)
        {
            foreach (var symbol in _builders.Keys.ToList())
            {
                var builder = _builders[symbol];

                while (builder.StartUtc < alignedNow && builder.HasTicks)
                {
                    OnBarClosed?.Invoke(builder.CloseCurrentBar());

                    builder = new LiveBarBuilder(
                        builder.Symbol,
                        builder.Provider,
                        builder.StartUtc.AddSeconds(_barSeconds),
                        _barSeconds);

                    _builders[symbol] = builder;
                }
            }
        }
    }

    private DateTime Align(DateTime utc)
    {
        var ticksPerBar = TimeSpan.FromSeconds(_barSeconds).Ticks;
        var alignedTicks = utc.Ticks - (utc.Ticks % ticksPerBar);
        return new DateTime(alignedTicks, DateTimeKind.Utc);
    }

    private static decimal? Mid(decimal? bid, decimal? ask)
    {
        if (bid.HasValue && ask.HasValue && bid.Value > 0m && ask.Value > 0m)
            return (bid.Value + ask.Value) / 2m;

        return bid ?? ask;
    }

    private sealed class LiveBarBuilder
    {
        public string Symbol { get; }
        public string Provider { get; }
        public DateTime StartUtc { get; }
        public bool HasTicks => _open.HasValue;

        private readonly int _barSeconds;

        private decimal? _open;
        private decimal? _high;
        private decimal? _low;
        private decimal? _close;
        private decimal _volume;

        public LiveBarBuilder(string symbol, string provider, DateTime startUtc, int barSeconds)
        {
            Symbol = symbol;
            Provider = provider;
            StartUtc = startUtc;
            _barSeconds = barSeconds;
        }

        public void Apply(decimal price, DateTime timestampUtc)
        {
            _open ??= price;
            _high = !_high.HasValue ? price : Math.Max(_high.Value, price);
            _low = !_low.HasValue ? price : Math.Min(_low.Value, price);
            _close = price;
            _volume += 0m;
        }

        public LiveBar CloseCurrentBar()
        {
            var open = _open ?? 0m;

            return new LiveBar
            {
                Symbol = Symbol,
                Provider = Provider,
                StartUtc = StartUtc,
                EndUtc = StartUtc.AddSeconds(_barSeconds),
                Open = open,
                High = _high ?? open,
                Low = _low ?? open,
                Close = _close ?? open,
                Volume = _volume
            };
        }
    }
}
