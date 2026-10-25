using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace DamianRocks;

/// <summary>Owns one overlay window per targeted monitor and drives the animation clock.</summary>
public sealed class OverlayManager : IDisposable
{
    public const string AllScreens = "all";

    private readonly List<OverlayWindow> _windows = new();
    private readonly List<HandsWindow> _handsWindows = new();
    private readonly ImageSource? _handImage;
    private IReadOnlyList<HandInfo> _hands = Array.Empty<HandInfo>();
    private string _handsSide;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly DispatcherTimer _rebuildDebounce;
    private bool _ticking;
    private string _target;

    public event Action<string>? HandPicked;
    public event Action<string>? HandLowered;

    public OverlayManager(string target, string handsSide, ImageSource? handImage)
    {
        _target = target;
        _handsSide = handsSide;
        _handImage = handImage;
        _rebuildDebounce = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _rebuildDebounce.Tick += (_, _) => { _rebuildDebounce.Stop(); Rebuild(); };
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        Rebuild();
    }

    /// <summary>"all" or a monitor device name such as \\.\DISPLAY2.</summary>
    public string Target
    {
        get => _target;
        set
        {
            if (_target == value) return;
            _target = value;
            Rebuild();
        }
    }

    /// <summary>Which edge of the screen the raised-hands list sits on: "left" or "right".</summary>
    public string HandsSide
    {
        get => _handsSide;
        set
        {
            if (_handsSide == value) return;
            _handsSide = value;
            Rebuild();
        }
    }

    public IReadOnlyList<HandInfo> Hands => _hands;

    public void UpdateHands(IReadOnlyList<HandInfo> hands)
    {
        _hands = hands;
        foreach (var w in _handsWindows) w.Update(hands);
    }

    public void Spawn(ImageSource? image, string fallbackText)
    {
        double now = _clock.Elapsed.TotalSeconds;
        bool any = false;
        foreach (var w in _windows) any |= w.Spawn(image, fallbackText, now);
        if (any && !_ticking)
        {
            _ticking = true;
            CompositionTarget.Rendering += OnRendering;
        }
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        double now = _clock.Elapsed.TotalSeconds;
        int active = 0;
        foreach (var w in _windows)
        {
            w.Tick(now);
            active += w.ActiveCount;
        }
        if (active == 0) StopTicking();
    }

    private void StopTicking()
    {
        if (!_ticking) return;
        _ticking = false;
        CompositionTarget.Rendering -= OnRendering;
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e) =>
        Application.Current?.Dispatcher.BeginInvoke(() => { _rebuildDebounce.Stop(); _rebuildDebounce.Start(); });

    private void Rebuild()
    {
        StopTicking();
        foreach (var w in _windows) w.Close();
        _windows.Clear();
        foreach (var w in _handsWindows) w.Close();
        _handsWindows.Clear();

        var screens = Forms.Screen.AllScreens;
        var targets = screens.Where(s => s.DeviceName == _target).ToList();
        if (targets.Count == 0) targets = screens.ToList(); // "all", or the chosen monitor was unplugged

        foreach (var screen in targets)
        {
            var window = new OverlayWindow(screen);
            window.Show();
            _windows.Add(window);

            var hands = new HandsWindow(screen, _handsSide, _handImage);
            hands.Picked += id => HandPicked?.Invoke(id);
            hands.Lowered += id => HandLowered?.Invoke(id);
            hands.Show();
            hands.Update(_hands);
            _handsWindows.Add(hands);
        }
    }

    public void Dispose()
    {
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        _rebuildDebounce.Stop();
        StopTicking();
        foreach (var w in _windows) w.Close();
        _windows.Clear();
        foreach (var w in _handsWindows) w.Close();
        _handsWindows.Clear();
    }
}
