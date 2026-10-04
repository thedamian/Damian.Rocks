using System;
using System.Drawing;
using System.IO;
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
        _images = new ReactionImages(_options.HttpBase);
        _overlays = new OverlayManager(_settings.Screen);

        _tray = new TrayIcon(LoadAppIcon(), _options.DisplayUri.ToString())
        {
            CurrentScreen = () => _overlays.Target,
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

        _connection = new DisplayConnection(_options.DisplayUri);
        _connection.StateChanged += (state, detail) => Dispatcher.BeginInvoke(() => _tray?.SetState(state, detail));
        _connection.AudienceChanged += count => Dispatcher.BeginInvoke(() => _tray?.SetAudience(count));
        _connection.ReactionReceived += reaction => Dispatcher.BeginInvoke(() => ShowReaction(reaction));
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
        var emoji = ReactionImages.BundledEmojis[Random.Shared.Next(ReactionImages.BundledEmojis.Length)];
        _overlays?.Spawn(_images?.GetEmoji(emoji), emoji);
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
