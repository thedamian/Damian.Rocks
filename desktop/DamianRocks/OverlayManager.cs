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
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly DispatcherTimer _rebuildDebounce;
    private bool _ticking;
    private string _target;

    public OverlayManager(string target)
    {
        _target = target;
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

        var screens = Forms.Screen.AllScreens;
        var targets = screens.Where(s => s.DeviceName == _target).ToList();
        if (targets.Count == 0) targets = screens.ToList(); // "all", or the chosen monitor was unplugged

        foreach (var screen in targets)
        {
            var window = new OverlayWindow(screen);
            window.Show();
            _windows.Add(window);
        }
    }

    public void Dispose()
    {
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        _rebuildDebounce.Stop();
        StopTicking();
        foreach (var w in _windows) w.Close();
        _windows.Clear();
    }
}
