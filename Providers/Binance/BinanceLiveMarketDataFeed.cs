using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PaperTradingBot.Config;
using PaperTradingBot.Interfaces;
using PaperTradingBot.Models;

namespace PaperTradingBot.Providers.Binance;

public class BinanceLiveMarketDataFeed : ILiveMarketDataFeed
{
    private readonly BotOptions _options;
    private readonly ILogger<BinanceLiveMarketDataFeed> _logger;
    private ClientWebSocket? _socket;
    private CancellationTokenSource? _receiveCts;

    private static readonly TimeSpan InitialBackoff = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxBackoff     = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan IdleTimeout    = TimeSpan.FromSeconds(75);

    public string ProviderName => "Binance";
    public ProviderConnectionState ConnectionState { get; private set; } = ProviderConnectionState.Disconnected;

    /// <summary>Timestamp of the last received message — used externally for health checks.</summary>
    public DateTime LastQuoteAt { get; private set; } = DateTime.MinValue;

    public event Action<QuoteTick>? OnQuote;
    public event Action<string>? OnInfo;
    public event Action<Exception>? OnError;

    public BinanceLiveMarketDataFeed(
        IOptions<BotOptions> options,
        ILogger<BinanceLiveMarketDataFeed> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public Task ConnectAsync(
        IReadOnlyList<MarketDataSubscription> subscriptions,
        CancellationToken cancellationToken)
    {
        ValidateConfig();

        _receiveCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _ = Task.Run(() => ReconnectLoopAsync(subscriptions, _receiveCts.Token), _receiveCts.Token);

        return Task.CompletedTask;
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken)
    {
        try
        {
            _receiveCts?.Cancel();

            if (_socket is { State: WebSocketState.Open })
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "shutdown", cancellationToken);
        }
        finally
        {
            _socket?.Dispose();
            _socket = null;
            ConnectionState = ProviderConnectionState.Disconnected;
        }
    }

    private async Task ReconnectLoopAsync(
        IReadOnlyList<MarketDataSubscription> subscriptions,
        CancellationToken cancellationToken)
    {
        var backoff = InitialBackoff;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                _socket?.Dispose();
                _socket = new ClientWebSocket();

                var uri = BuildUri(subscriptions);

                ConnectionState = ProviderConnectionState.Connecting;
                await _socket.ConnectAsync(uri, cancellationToken);
                ConnectionState = ProviderConnectionState.Authenticated;

                OnInfo?.Invoke($"Connected to Binance stream: {uri}");
                backoff = InitialBackoff;

                // Idle watchdog: if no data arrives for IdleTimeout, cancel receive and reconnect
                using var idleCts = new CancellationTokenSource(IdleTimeout);
                using var linked  = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, idleCts.Token);

                var idleTimedOut = false;
                try
                {
                    await ReceiveAsync(linked.Token, () => idleCts.CancelAfter(IdleTimeout));
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    idleTimedOut = true;
                }

                if (idleTimedOut)
                {
                    ConnectionState = ProviderConnectionState.Faulted;
                    _logger.LogWarning("Binance feed idle for {Timeout}s — forcing reconnect in {Backoff}s.",
                        IdleTimeout.TotalSeconds, backoff.TotalSeconds);
                    OnError?.Invoke(new TimeoutException($"Binance feed idle for {IdleTimeout.TotalSeconds}s"));
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                ConnectionState = ProviderConnectionState.Faulted;
                _logger.LogError(ex, "Binance feed faulted. Reconnecting in {Backoff}s.", backoff.TotalSeconds);
                OnError?.Invoke(ex);
            }

            if (cancellationToken.IsCancellationRequested)
                break;

            await Task.Delay(backoff, cancellationToken).ConfigureAwait(false);
            backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, MaxBackoff.Ticks));
        }

        ConnectionState = ProviderConnectionState.Disconnected;
    }

    private async Task ReceiveAsync(CancellationToken cancellationToken, Action resetIdleTimer)
    {
        var buffer = new byte[64 * 1024];

        while (!cancellationToken.IsCancellationRequested && _socket is { State: WebSocketState.Open })
        {
            using var ms = new MemoryStream();
            WebSocketReceiveResult result;

            do
            {
                result = await _socket.ReceiveAsync(buffer, cancellationToken);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    OnInfo?.Invoke("Binance websocket closed by server.");
                    return;
                }

                ms.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);

            LastQuoteAt = DateTime.UtcNow;
            resetIdleTimer();
            ParseMessage(Encoding.UTF8.GetString(ms.ToArray()));
        }
    }

    private Uri BuildUri(IReadOnlyList<MarketDataSubscription> subscriptions)
    {
        var channel = _options.Providers.Binance.Channel;
        var base_ = _options.Providers.Binance.WebSocketUrl.TrimEnd('/');
        var streamNames = subscriptions
            .Select(s => $"{s.ProviderSymbol.ToLowerInvariant()}@{channel}")
            .ToList();

        return streamNames.Count == 1
            ? new Uri($"{base_}/ws/{streamNames[0]}")
            : new Uri($"{base_}/stream?streams={string.Join("/", streamNames)}");
    }

    private void ValidateConfig()
    {
        if (string.IsNullOrWhiteSpace(_options.Providers.Binance.WebSocketUrl))
            throw new InvalidOperationException("Binance WebSocketUrl is required.");

        // Binance has no testnet market-data stream — production WebSocket is always used.
        // Execution stays local via LocalPaperExecutionOnly.
    }

    private void ParseMessage(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // Combined stream wraps payload: { "stream": "ethusdt@bookTicker", "data": { ... } }
        var data = root.TryGetProperty("data", out var d) ? d : root;

        var channel = _options.Providers.Binance.Channel;

        if (string.Equals(channel, "bookTicker", StringComparison.OrdinalIgnoreCase))
            EmitBookTicker(data);
        else if (string.Equals(channel, "ticker", StringComparison.OrdinalIgnoreCase))
            EmitTicker(data);
        else if (string.Equals(channel, "trade", StringComparison.OrdinalIgnoreCase))
            EmitTrade(data);
    }

    // { "s": "ETHUSDT", "b": "bid", "B": "bidQty", "a": "ask", "A": "askQty" }
    private void EmitBookTicker(JsonElement data)
    {
        var symbol = data.TryGetProperty("s", out var s) ? s.GetString() ?? "" : "";
        var bid = data.TryGetProperty("b", out var b) && decimal.TryParse(b.GetString(), out var bv) ? bv : (decimal?)null;
        var ask = data.TryGetProperty("a", out var a) && decimal.TryParse(a.GetString(), out var av) ? av : (decimal?)null;

        OnQuote?.Invoke(new QuoteTick
        {
            Symbol = symbol,
            Bid = bid,
            Ask = ask,
            TimestampUtc = DateTime.UtcNow,
            Provider = ProviderName,
            RawPayload = data.ToString()
        });
    }

    // 24hr rolling window — "c" is last price, "b"/"a" are best bid/ask
    private void EmitTicker(JsonElement data)
    {
        var symbol = data.TryGetProperty("s", out var s) ? s.GetString() ?? "" : "";
        var last = data.TryGetProperty("c", out var c) && decimal.TryParse(c.GetString(), out var lv) ? lv : (decimal?)null;
        var bid = data.TryGetProperty("b", out var b) && decimal.TryParse(b.GetString(), out var bv) ? bv : (decimal?)null;
        var ask = data.TryGetProperty("a", out var a) && decimal.TryParse(a.GetString(), out var av) ? av : (decimal?)null;

        OnQuote?.Invoke(new QuoteTick
        {
            Symbol = symbol,
            Last = last,
            Bid = bid,
            Ask = ask,
            TimestampUtc = DateTime.UtcNow,
            Provider = ProviderName,
            RawPayload = data.ToString()
        });
    }

    // { "s": "ETHUSDT", "p": "price", "T": timestamp_ms }
    private void EmitTrade(JsonElement data)
    {
        var symbol = data.TryGetProperty("s", out var s) ? s.GetString() ?? "" : "";
        var price = data.TryGetProperty("p", out var p) && decimal.TryParse(p.GetString(), out var pv) ? pv : (decimal?)null;
        var timestamp = data.TryGetProperty("T", out var t)
            ? DateTimeOffset.FromUnixTimeMilliseconds(t.GetInt64()).UtcDateTime
            : DateTime.UtcNow;

        OnQuote?.Invoke(new QuoteTick
        {
            Symbol = symbol,
            Last = price,
            TimestampUtc = timestamp,
            Provider = ProviderName,
            RawPayload = data.ToString()
        });
    }
}
