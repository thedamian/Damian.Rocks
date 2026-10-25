using System;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DamianRocks;

public enum ConnectionState { Connecting, Connected, Disconnected }

public sealed record ReactionMessage(string Id, string? Emoji, string? Image);

/// <summary>A raised hand as the server shares it with displays.</summary>
public sealed record HandInfo(string Id, string Name, bool Active, bool Talking, bool Online);

/// <summary>
/// Keeps a WebSocket connection to the server's /display endpoint alive, reconnecting with
/// exponential backoff. Events are raised on a background thread.
/// </summary>
public sealed class DisplayConnection : IDisposable
{
    private readonly Uri _uri;
    private readonly Func<string?> _presenterKey;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private CancellationTokenSource? _connectionCts;
    private ClientWebSocket? _ws;
    private Task? _loop;

    public event Action<ConnectionState, string?>? StateChanged;
    public event Action<ReactionMessage>? ReactionReceived;
    public event Action<int>? AudienceChanged;
    /// <summary>Whether this app may control questions, and the ICE servers (raw JSON array) for audio.</summary>
    public event Action<bool, string>? HelloReceived;
    public event Action<IReadOnlyList<HandInfo>>? HandsChanged;
    /// <summary>A called-on audience member's WebRTC offer: hand id, name, SDP.</summary>
    public event Action<string, string, string>? RtcOffer;
    public event Action<string>? RtcClose;

    public DisplayConnection(Uri uri, Func<string?> presenterKey)
    {
        _uri = uri;
        _presenterKey = presenterKey;
    }

    public void Start() => _loop ??= Task.Run(() => RunAsync(_cts.Token));

    /// <summary>Drops the current connection and reconnects right away (e.g. after the key changed).</summary>
    public void Reconnect()
    {
        try { _connectionCts?.Cancel(); } catch (ObjectDisposedException) { }
    }

    /// <summary>Sends a JSON message. Returns false if not connected.</summary>
    public async Task<bool> SendAsync(object message)
    {
        var ws = _ws;
        if (ws is null || ws.State != WebSocketState.Open) return false;
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(message);
        await _sendLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await ws.SendAsync(bytes, WebSocketMessageType.Text, true, _cts.Token).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        int attempt = 0;
        while (!ct.IsCancellationRequested)
        {
            string? error = null;
            bool reconnectNow = false;
            using var connectionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _connectionCts = connectionCts;
            using (var ws = new ClientWebSocket())
            {
                ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
                ws.Options.SetRequestHeader("User-Agent", "DamianRocks-Overlay/1.1");
                string? key = _presenterKey();
                if (!string.IsNullOrWhiteSpace(key)) ws.Options.SetRequestHeader("Authorization", $"Bearer {key.Trim()}");
                try
                {
                    StateChanged?.Invoke(ConnectionState.Connecting, null);
                    using (var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(connectionCts.Token))
                    {
                        connectTimeout.CancelAfter(TimeSpan.FromSeconds(15));
                        await ws.ConnectAsync(_uri, connectTimeout.Token).ConfigureAwait(false);
                    }
                    attempt = 0;
                    _ws = ws;
                    StateChanged?.Invoke(ConnectionState.Connected, null);
                    await ReceiveLoopAsync(ws, connectionCts.Token).ConfigureAwait(false);
                    error = ws.CloseStatusDescription is { Length: > 0 } d ? d : "Server closed the connection";
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception) when (connectionCts.IsCancellationRequested && !ct.IsCancellationRequested)
                {
                    reconnectNow = true;
                }
                catch (Exception ex)
                {
                    error = ex.InnerException?.Message ?? ex.Message;
                }
                finally
                {
                    _ws = null;
                    _connectionCts = null;
                }
            }

            if (reconnectNow) continue;
            var delay = TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, attempt)) + Random.Shared.NextDouble());
            attempt = Math.Min(attempt + 1, 6);
            StateChanged?.Invoke(ConnectionState.Disconnected, $"{error} — retrying in {delay.TotalSeconds:0}s");
            try { await Task.Delay(delay, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task ReceiveLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[8 * 1024];
        using var message = new MemoryStream();
        while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            var result = await ws.ReceiveAsync(buffer, ct).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                try { await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, ct).ConfigureAwait(false); }
                catch { /* already gone */ }
                return;
            }
            message.Write(buffer, 0, result.Count);
            if (message.Length > 64 * 1024) throw new InvalidDataException("Message too large");
            if (!result.EndOfMessage) continue;

            if (result.MessageType == WebSocketMessageType.Text)
                Handle(Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length));
            message.SetLength(0);
        }
    }

    private void Handle(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            switch (GetString(root, "type"))
            {
                case "reaction":
                    var id = GetString(root, "id");
                    if (id is not null)
                        ReactionReceived?.Invoke(new ReactionMessage(id, GetString(root, "emoji"), GetString(root, "image")));
                    break;
                case "hello":
                    bool presenter = root.TryGetProperty("presenter", out var p) && p.ValueKind == JsonValueKind.True;
                    string ice = root.TryGetProperty("iceServers", out var i) && i.ValueKind == JsonValueKind.Array ? i.GetRawText() : "[]";
                    HelloReceived?.Invoke(presenter, ice);
                    goto case "stats";
                case "stats":
                    if (root.TryGetProperty("audience", out var a) && a.TryGetInt32(out int count))
                        AudienceChanged?.Invoke(count);
                    break;
                case "hands":
                    if (root.TryGetProperty("hands", out var list) && list.ValueKind == JsonValueKind.Array)
                        HandsChanged?.Invoke(ParseHands(list));
                    break;
                case "rtc-offer":
                    if (GetString(root, "id") is { } offerId && GetString(root, "sdp") is { } sdp)
                        RtcOffer?.Invoke(offerId, GetString(root, "name") ?? "", sdp);
                    break;
                case "rtc-close":
                    if (GetString(root, "id") is { } closeId) RtcClose?.Invoke(closeId);
                    break;
            }
        }
        catch (JsonException)
        {
            // ignore malformed messages
        }
    }

    private static List<HandInfo> ParseHands(JsonElement list)
    {
        var hands = new List<HandInfo>();
        foreach (var h in list.EnumerateArray())
        {
            if (GetString(h, "id") is not { } id) continue;
            hands.Add(new HandInfo(id, GetString(h, "name") ?? "?", GetBool(h, "active"), GetBool(h, "talking"), GetBool(h, "online")));
        }
        return hands;
    }

    private static bool GetBool(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static string? GetString(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    public void Dispose()
    {
        _cts.Cancel();
        try { _loop?.Wait(TimeSpan.FromSeconds(2)); } catch { /* shutting down */ }
        _cts.Dispose();
    }
}
