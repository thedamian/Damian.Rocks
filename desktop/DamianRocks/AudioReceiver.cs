using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Windows.Interop;
using Microsoft.Web.WebView2.Core;

namespace DamianRocks;

/// <summary>
/// Receives an audience member's push-to-talk audio over WebRTC and plays it on this PC's
/// speakers. WebRTC comes from an invisible WebView2 (the Edge engine built into Windows),
/// so there's no native media stack to ship. Must be used on the UI thread.
/// </summary>
public sealed class AudioReceiver : IDisposable
{
    private HwndSource? _host;
    private CoreWebView2Controller? _controller;
    private CoreWebView2? _web;
    private Task<bool>? _init;
    private bool _disposed;

    /// <summary>The answer SDP for a hand id, to send back through the server.</summary>
    public event Action<string, string>? AnswerReady;
    /// <summary>Connection state changes for a hand id ("connected", "failed", …).</summary>
    public event Action<string, string>? StateChanged;

    public string? Error { get; private set; }

    public static string LogPath => Path.Combine(Settings.Folder, "audio.log");

    public Task<bool> StartAsync() => _init ??= InitAsync();

    private async Task<bool> InitAsync()
    {
        try
        {
            Log("starting WebView2");
            // A never-shown popup window as the WebView2 parent: nothing appears on screen.
            var parameters = new HwndSourceParameters("Damian.Rocks audio", 1, 1)
            {
                WindowStyle = unchecked((int)0x80000000), // WS_POPUP, not WS_VISIBLE
                ExtendedWindowStyle = (int)(NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE),
                PositionX = -32000,
                PositionY = -32000,
            };
            _host = new HwndSource(parameters);

            string dataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DamianRocks", "WebView2");
            var options = new CoreWebView2EnvironmentOptions("--autoplay-policy=no-user-gesture-required");
            var env = await CoreWebView2Environment.CreateAsync(null, dataFolder, options);
            _controller = await env.CreateCoreWebView2ControllerAsync(_host.Handle);
            if (_disposed) return false;
            _web = _controller.CoreWebView2;
            _web.Settings.AreDefaultContextMenusEnabled = false;
            _web.Settings.AreDevToolsEnabled = false;
            _web.Settings.IsStatusBarEnabled = false;
            _web.WebMessageReceived += OnWebMessage;

            var loaded = new TaskCompletionSource<bool>();
            _web.NavigationCompleted += (_, e) => loaded.TrySetResult(e.IsSuccess);
            _web.NavigateToString(Page);
            if (!await loaded.Task) throw new InvalidOperationException("Audio page failed to load");
            Log($"WebView2 ready ({env.BrowserVersionString})");
            return true;
        }
        catch (WebView2RuntimeNotFoundException)
        {
            Error = "The Microsoft Edge WebView2 Runtime is missing. Install it from https://go.microsoft.com/fwlink/p/?LinkId=2124703 to hear audience questions.";
        }
        catch (Exception ex)
        {
            Error = $"Couldn't start audio: {ex.Message}";
        }
        Log(Error);
        return false;
    }

    /// <summary>Answers an audience member's offer and starts playing their audio when it arrives.</summary>
    public async Task<bool> AcceptOfferAsync(string id, string sdp, string iceServersJson)
    {
        if (!await StartAsync() || _web is null) return false;
        JsonNode? ice;
        try { ice = JsonNode.Parse(iceServersJson); }
        catch (JsonException) { ice = new JsonArray(); }
        var message = new JsonObject { ["type"] = "offer", ["id"] = id, ["sdp"] = sdp, ["iceServers"] = ice };
        _web.PostWebMessageAsJson(message.ToJsonString());
        Log($"offer {Short(id)} ({sdp.Length} bytes)");
        return true;
    }

    /// <summary>Hangs up. With an id, only if that call is the current one.</summary>
    public void Close(string? id = null)
    {
        if (_web is null) return;
        _web.PostWebMessageAsJson(new JsonObject { ["type"] = "close", ["id"] = id }.ToJsonString());
        Log($"close {Short(id)}");
    }

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            var msg = JsonNode.Parse(e.WebMessageAsJson);
            string? type = msg?["type"]?.GetValue<string>();
            string id = msg?["id"]?.GetValue<string>() ?? "";
            switch (type)
            {
                case "answer":
                    string? sdp = msg?["sdp"]?.GetValue<string>();
                    if (sdp is not null) AnswerReady?.Invoke(id, sdp);
                    Log($"answer {Short(id)}");
                    break;
                case "state":
                    string state = msg?["state"]?.GetValue<string>() ?? "";
                    StateChanged?.Invoke(id, state);
                    Log($"state {Short(id)} {state}");
                    break;
                case "stats":
                    Log($"stats {Short(id)} packets={msg?["packets"]} energy={msg?["energy"]} level={msg?["level"]}");
                    break;
                case "log":
                    Log($"page: {msg?["text"]}");
                    break;
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            // ignore
        }
    }

    private static string Short(string? id) => id is null ? "*" : id.Length > 8 ? id[..8] : id;

    private static void Log(string? text)
    {
        try
        {
            Directory.CreateDirectory(Settings.Folder);
            var info = new FileInfo(LogPath);
            if (info.Exists && info.Length > 512 * 1024) info.Delete();
            File.AppendAllText(LogPath, $"[{DateTime.Now:HH:mm:ss.fff}] {text}\n");
        }
        catch
        {
            // logging is best-effort
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _controller?.Close();
        _controller = null;
        _web = null;
        _host?.Dispose();
        _host = null;
    }

    private const string Page = """
        <!doctype html>
        <html><head><meta charset="utf-8"><title>Damian.Rocks audio</title></head>
        <body>
        <audio id="speaker" autoplay></audio>
        <script>
        const speaker = document.getElementById('speaker');
        const post = (m) => window.chrome.webview.postMessage(m);
        let pc = null;
        let current = null;
        let statsTimer = 0;

        function hangUp() {
          clearInterval(statsTimer);
          if (pc) pc.close();
          pc = null;
          current = null;
          speaker.srcObject = null;
        }

        function gathered(peer) {
          return new Promise((resolve) => {
            if (peer.iceGatheringState === 'complete') return resolve();
            const timer = setTimeout(resolve, 3000);
            peer.addEventListener('icegatheringstatechange', () => {
              if (peer.iceGatheringState === 'complete') { clearTimeout(timer); resolve(); }
            });
          });
        }

        async function answer(m) {
          hangUp();
          const id = m.id;
          current = id;
          const peer = new RTCPeerConnection({ iceServers: Array.isArray(m.iceServers) ? m.iceServers : [] });
          pc = peer;
          peer.ontrack = (ev) => {
            speaker.srcObject = ev.streams[0] || new MediaStream([ev.track]);
            speaker.play().catch((err) => post({ type: 'log', text: 'play failed: ' + err }));
          };
          peer.onconnectionstatechange = () => {
            if (pc === peer) post({ type: 'state', id, state: peer.connectionState });
          };
          try {
            await peer.setRemoteDescription({ type: 'offer', sdp: m.sdp });
            await peer.setLocalDescription(await peer.createAnswer());
            await gathered(peer);
            if (pc !== peer) return;
            post({ type: 'answer', id, sdp: peer.localDescription.sdp });
          } catch (err) {
            post({ type: 'log', text: 'answer failed: ' + err });
            if (pc === peer) post({ type: 'state', id, state: 'failed' });
            return;
          }
          let lastEnergy = -1;
          statsTimer = setInterval(async () => {
            if (pc !== peer) return;
            let s = null;
            (await peer.getStats()).forEach((r) => { if (r.type === 'inbound-rtp' && r.kind === 'audio') s = r; });
            if (!s) return;
            const energy = s.totalAudioEnergy || 0;
            if (energy !== lastEnergy) {
              lastEnergy = energy;
              post({ type: 'stats', id, packets: s.packetsReceived || 0, energy: +energy.toFixed(4), level: +(s.audioLevel || 0).toFixed(3) });
            }
          }, 2000);
        }

        window.chrome.webview.addEventListener('message', (e) => {
          const m = e.data;
          if (!m || typeof m !== 'object') return;
          if (m.type === 'offer') answer(m);
          else if (m.type === 'close' && (!m.id || m.id === current)) hangUp();
        });
        </script>
        </body></html>
        """;
}
