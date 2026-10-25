using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Threading;

namespace DamianRocks;

/// <summary>Background tray application: no main window, just overlays + tray icon.</summary>
public sealed class OverlayApp : Application
{
    private readonly AppOptions _options;
    private Settings _settings = new();
    private ReactionImages? _images;
    private OverlayManager? _overlays;
    private TrayIcon? _tray;
    private DisplayConnection? _connection;
    private AudioReceiver? _audio;
    private volatile string? _presenterKey;
    private bool _presenter;
    private string _iceServers = "[]";
    private bool _warnedBadKey;
    private bool _warnedAudio;
    private bool _paused;
    private bool _shuttingDown;

    public OverlayApp(AppOptions options)
    {
        _options = options;
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        DispatcherUnhandledException += OnUnhandledException;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _settings = Settings.Load();
        if (_options.Key is not null)
        {
            _settings.PresenterKey = _options.Key; // remembered (encrypted) so --key is only needed once
            _settings.Save();
        }
        _presenterKey = _settings.PresenterKey;
        _images = new ReactionImages(_options.HttpBase);
        _overlays = new OverlayManager(_settings.Screen, _settings.HandsSide, _images.GetEmoji(ReactionImages.HandEmoji));
        _overlays.HandPicked += PickHand;
        _overlays.HandLowered += id => _ = _connection?.SendAsync(new { type = "lower", id });

        _tray = new TrayIcon(LoadAppIcon(), _options.DisplayUri.ToString())
        {
            CurrentScreen = () => _overlays.Target,
            CurrentHandsSide = () => _overlays.HandsSide,
            HasKey = () => _presenterKey is not null,
        };
        _tray.QuitRequested += Quit;
        _tray.TestRequested += SpawnTest;
        _tray.PauseChanged += paused => _paused = paused;
        _tray.StartupChanged += SetStartup;
        _tray.ScreenSelected += device =>
        {
            _overlays.Target = device;
            _settings.Screen = device;
            _settings.Save();
            SpawnTest();
        };
        _tray.HandsSideSelected += side =>
        {
            _overlays.HandsSide = side;
            _settings.HandsSide = side;
            _settings.Save();
        };
        _tray.KeyEntered += SetPresenterKey;

        _audio = new AudioReceiver();
        _audio.AnswerReady += (id, sdp) => _ = _connection?.SendAsync(new { type = "rtc-answer", id, sdp });
        _audio.StateChanged += (id, state) =>
        {
            if (state != "failed") return;
            string name = _overlays?.Hands.FirstOrDefault(h => h.Id == id)?.Name ?? "the audience member";
            _tray?.ShowWarning("Couldn't connect audio",
                $"No audio route to {name}'s phone. Their network may need a TURN server (see README), or they can tap Speak to retry.");
        };

        _connection = new DisplayConnection(_options.DisplayUri, () => _presenterKey);
        _connection.StateChanged += (state, detail) => Dispatcher.BeginInvoke(() => OnConnectionState(state, detail));
        _connection.AudienceChanged += count => Dispatcher.BeginInvoke(() => _tray?.SetAudience(count));
        _connection.ReactionReceived += reaction => Dispatcher.BeginInvoke(() => ShowReaction(reaction));
        _connection.HelloReceived += (presenter, ice) => Dispatcher.BeginInvoke(() => OnHello(presenter, ice));
        _connection.HandsChanged += hands => Dispatcher.BeginInvoke(() => _overlays?.UpdateHands(hands));
        _connection.RtcOffer += (id, name, sdp) => Dispatcher.BeginInvoke(() => AcceptOffer(id, name, sdp));
        _connection.RtcClose += id => Dispatcher.BeginInvoke(() => _audio?.Close(id));
        _connection.Start();

        if (!_settings.WelcomeShown)
        {
            _tray.ShowBalloon("Damian.Rocks is running",
                "Audience reactions will float up the sides of your screen. Right-click the tray icon to quit.");
            _settings.WelcomeShown = true;
            _settings.Save();
        }
    }

    private async void ShowReaction(ReactionMessage reaction)
    {
        if (_paused || _shuttingDown || _images is null) return;
        var image = await _images.GetAsync(reaction);
        if (_paused || _shuttingDown) return;
        _overlays?.Spawn(image, reaction.Emoji ?? "❔");
    }

    private void SpawnTest()
    {
        var emojis = ReactionImages.BundledEmojis.Where(e => e != ReactionImages.HandEmoji).ToArray();
        var emoji = emojis[Random.Shared.Next(emojis.Length)];
        _overlays?.Spawn(_images?.GetEmoji(emoji), emoji);
    }

    private void OnConnectionState(ConnectionState state, string? detail)
    {
        _tray?.SetState(state, detail);
        if (state == ConnectionState.Connected) return;
        // Offline: the server will resend the queue (and any offer) when we reconnect.
        _presenter = false;
        _tray?.SetPresenter(null);
        _overlays?.UpdateHands(Array.Empty<HandInfo>());
        _audio?.Close();
    }

    private async void OnHello(bool presenter, string iceServersJson)
    {
        _presenter = presenter;
        _iceServers = iceServersJson;
        _tray?.SetPresenter(presenter);
        if (!presenter)
        {
            if (_presenterKey is not null && !_warnedBadKey)
            {
                _warnedBadKey = true;
                _tray?.ShowWarning("Presenter key not accepted",
                    "The server didn't accept the saved presenter key. Right-click the tray icon → Presenter key… to fix it.");
            }
            return;
        }
        _warnedBadKey = false;
        // Warm up the audio engine now so the first question connects quickly.
        if (_audio is not null && !await _audio.StartAsync() && !_warnedAudio)
        {
            _warnedAudio = true;
            _tray?.ShowWarning("Question audio unavailable", _audio.Error ?? "Couldn't start the audio engine.");
        }
    }

    private void PickHand(string id)
    {
        if (!_presenter)
        {
            _tray?.ShowBalloon("Presenter key needed",
                "To call on people, enter the server's DISPLAY_KEY (tray icon → Presenter key…).");
            _tray?.AskForKey();
            return;
        }
        _ = _connection?.SendAsync(new { type = "pick", id });
    }

    private async void AcceptOffer(string id, string name, string sdp)
    {
        if (_audio is null || _shuttingDown) return;
        if (!await _audio.AcceptOfferAsync(id, sdp, _iceServers) && !_warnedAudio)
        {
            _warnedAudio = true;
            _tray?.ShowWarning($"Can't play {name}'s audio", _audio.Error ?? "Couldn't start the audio engine.");
        }
    }

    private void SetPresenterKey(string? key)
    {
        try
        {
            _settings.PresenterKey = key;
            _settings.Save();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Couldn't save the key:\n{ex.Message}", "Damian.Rocks", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _presenterKey = key;
        _warnedBadKey = false;
        _connection?.Reconnect();
    }

    private void SetStartup(bool enabled)
    {
        try
        {
            StartupRegistration.Set(enabled, _options);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Couldn't update the startup setting:\n{ex.Message}", "Damian.Rocks",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Quit()
    {
        if (_shuttingDown) return;
        _shuttingDown = true;
        Cleanup();
        Shutdown();
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        Cleanup();
        base.OnSessionEnding(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Cleanup();
        base.OnExit(e);
    }

    private void Cleanup()
    {
        _connection?.Dispose();
        _connection = null;
        _audio?.Dispose();
        _audio = null;
        _tray?.Dispose();
        _tray = null;
        _overlays?.Dispose();
        _overlays = null;
    }

    private static Icon LoadAppIcon()
    {
        var info = GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico"));
        using var stream = info!.Stream;
        return new Icon(stream, System.Windows.Forms.SystemInformation.SmallIconSize);
    }

    private static void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // A presentation tool should never crash mid-talk: log and keep going.
        try
        {
            Directory.CreateDirectory(Settings.Folder);
            File.AppendAllText(Path.Combine(Settings.Folder, "error.log"), $"[{DateTime.Now:O}] {e.Exception}\n\n");
        }
        catch
        {
            // ignore
        }
        e.Handled = true;
    }
}
