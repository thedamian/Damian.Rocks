using System;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DamianRocks;

public enum ConnectionState { Connecting, Connected, Disconnected }

public sealed record ReactionMessage(string Id, string? Emoji, string? Image);

/// <summary>
/// Keeps a WebSocket connection to the server's /display endpoint alive, reconnecting with
/// exponential backoff. Events are raised on a background thread.
/// </summary>
public sealed class DisplayConnection : IDisposable
{
    private readonly Uri _uri;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    public event Action<ConnectionState, string?>? StateChanged;
    public event Action<ReactionMessage>? ReactionReceived;
    public event Action<int>? AudienceChanged;

    public DisplayConnection(Uri uri) => _uri = uri;

    public void Start() => _loop ??= Task.Run(() => RunAsync(_cts.Token));

    private async Task RunAsync(CancellationToken ct)
    {
        int attempt = 0;
        while (!ct.IsCancellationRequested)
        {
            string? error = null;
            using (var ws = new ClientWebSocket())
            {
                ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
                ws.Options.SetRequestHeader("User-Agent", "DamianRocks-Overlay/1.0");
                try
                {
                    StateChanged?.Invoke(ConnectionState.Connecting, null);
                    using (var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
                    {
                        connectTimeout.CancelAfter(TimeSpan.FromSeconds(15));
                        await ws.ConnectAsync(_uri, connectTimeout.Token).ConfigureAwait(false);
                    }
                    attempt = 0;
                    StateChanged?.Invoke(ConnectionState.Connected, null);
                    await ReceiveLoopAsync(ws, ct).ConfigureAwait(false);
                    error = ws.CloseStatusDescription is { Length: > 0 } d ? d : "Server closed the connection";
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    error = ex.InnerException?.Message ?? ex.Message;
                }
            }

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
                case "stats":
                    if (root.TryGetProperty("audience", out var a) && a.TryGetInt32(out int count))
                        AudienceChanged?.Invoke(count);
                    break;
            }
        }
        catch (JsonException)
        {
            // ignore malformed messages
        }
    }

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
