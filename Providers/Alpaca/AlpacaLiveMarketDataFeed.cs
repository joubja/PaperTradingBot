using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PaperTradingBot.Config;
using PaperTradingBot.Interfaces;
using PaperTradingBot.Models;

namespace PaperTradingBot.Providers.Alpaca;

public class AlpacaLiveMarketDataFeed : ILiveMarketDataFeed
{
    private readonly BotOptions _options;
    private readonly ILogger<AlpacaLiveMarketDataFeed> _logger;
    private ClientWebSocket? _socket;
    private CancellationTokenSource? _receiveCts;

    private static readonly TimeSpan InitialBackoff = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(60);

    public string ProviderName => "Alpaca";
    public ProviderConnectionState ConnectionState { get; private set; } = ProviderConnectionState.Disconnected;

    public event Action<QuoteTick>? OnQuote;
    public event Action<string>? OnInfo;
    public event Action<Exception>? OnError;

    public AlpacaLiveMarketDataFeed(
        IOptions<BotOptions> options,
        ILogger<AlpacaLiveMarketDataFeed> logger)
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

                var uri = new Uri(_options.Providers.Alpaca.MarketDataUrl);

                ConnectionState = ProviderConnectionState.Connecting;
                await _socket.ConnectAsync(uri, cancellationToken);
                ConnectionState = ProviderConnectionState.Connected;

                OnInfo?.Invoke($"Connected to Alpaca market data: {uri}");

                await AuthenticateAsync(cancellationToken);
                await SubscribeAsync(subscriptions, cancellationToken);

                backoff = InitialBackoff;

                await ReceiveAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                ConnectionState = ProviderConnectionState.Faulted;
                _logger.LogError(ex, "Alpaca feed faulted. Reconnecting in {Backoff}s.", backoff.TotalSeconds);
                OnError?.Invoke(ex);
            }

            if (cancellationToken.IsCancellationRequested)
                break;

            await Task.Delay(backoff, cancellationToken).ConfigureAwait(false);
            backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, MaxBackoff.Ticks));
        }

        ConnectionState = ProviderConnectionState.Disconnected;
    }

    private async Task AuthenticateAsync(CancellationToken cancellationToken)
    {
        var auth = JsonSerializer.Serialize(new
        {
            action = "auth",
            key = _options.Providers.Alpaca.ApiKey,
            secret = _options.Providers.Alpaca.ApiSecret
        });

        await SendAsync(auth, cancellationToken);
        ConnectionState = ProviderConnectionState.Authenticated;
    }

    private async Task SubscribeAsync(
        IReadOnlyList<MarketDataSubscription> subscriptions,
        CancellationToken cancellationToken)
    {
        var providerSymbols = subscriptions.Select(s => s.ProviderSymbol).ToArray();
        var channel = _options.Providers.Alpaca.Channel;

        var subscribe = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["action"] = "subscribe",
            [channel] = providerSymbols
        });

        await SendAsync(subscribe, cancellationToken);
    }

    private async Task ReceiveAsync(CancellationToken cancellationToken)
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
                    OnInfo?.Invoke("Alpaca websocket closed by server.");
                    return;
                }

                ms.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);

            ParseMessages(Encoding.UTF8.GetString(ms.ToArray()));
        }
    }

    private async Task SendAsync(string payload, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(payload);
        await _socket!.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
    }

    private void ValidateConfig()
    {
        if (!_options.Runtime.AllowProductionMarketDataEndpoints &&
            !_options.Providers.Alpaca.UseSandbox &&
            !_options.Providers.Alpaca.MarketDataUrl.Contains("sandbox", StringComparison.OrdinalIgnoreCase) &&
            !_options.Providers.Alpaca.MarketDataUrl.Contains("/v2/test", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Production Alpaca market-data endpoints are blocked by configuration.");
        }

        if (string.IsNullOrWhiteSpace(_options.Providers.Alpaca.MarketDataUrl))
            throw new InvalidOperationException("Alpaca MarketDataUrl is required.");
    }

    private void ParseMessages(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            return;

        foreach (var item in doc.RootElement.EnumerateArray())
        {
            if (!item.TryGetProperty("T", out var tProp))
                continue;

            switch (tProp.GetString())
            {
                case "success":
                case "subscription":
                    OnInfo?.Invoke(item.ToString());
                    break;
                case "q":
                    EmitQuote(item);
                    break;
                case "b":
                    EmitBarAsQuote(item);
                    break;
                case "error":
                    OnInfo?.Invoke($"Alpaca error: {item}");
                    break;
            }
        }
    }

    private void EmitQuote(JsonElement item)
    {
        var symbol = item.TryGetProperty("S", out var s) ? s.GetString() ?? "" : "";
        var bid = item.TryGetProperty("bp", out var bp) ? bp.GetDecimal() : (decimal?)null;
        var ask = item.TryGetProperty("ap", out var ap) ? ap.GetDecimal() : (decimal?)null;
        var timestamp = item.TryGetProperty("t", out var t) && DateTime.TryParse(t.GetString(), out var ts)
            ? ts.ToUniversalTime()
            : DateTime.UtcNow;

        OnQuote?.Invoke(new QuoteTick
        {
            Symbol = symbol,
            Bid = bid,
            Ask = ask,
            TimestampUtc = timestamp,
            Provider = ProviderName,
            RawPayload = item.ToString()
        });
    }

    private void EmitBarAsQuote(JsonElement item)
    {
        var symbol = item.TryGetProperty("S", out var s) ? s.GetString() ?? "" : "";
        var close = item.TryGetProperty("c", out var c) ? c.GetDecimal() : (decimal?)null;
        var timestamp = item.TryGetProperty("t", out var t) && DateTime.TryParse(t.GetString(), out var ts)
            ? ts.ToUniversalTime()
            : DateTime.UtcNow;

        OnQuote?.Invoke(new QuoteTick
        {
            Symbol = symbol,
            Last = close,
            TimestampUtc = timestamp,
            Provider = ProviderName,
            RawPayload = item.ToString()
        });
    }
}
