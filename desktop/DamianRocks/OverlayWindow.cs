using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Forms = System.Windows.Forms;

namespace DamianRocks;

/// <summary>
/// A transparent, click-through, always-on-top window covering one monitor. It never takes
/// focus and never appears in the taskbar or Alt+Tab.
/// </summary>
public sealed class OverlayWindow : Window
{
    private const int MaxActive = 60;

    private readonly Canvas _canvas = new() { IsHitTestVisible = false, ClipToBounds = true };
    private readonly List<FloatingReaction> _active = new();
    private IntPtr _hwnd;

    public Forms.Screen Screen { get; }
    public int ActiveCount => _active.Count;

    public OverlayWindow(Forms.Screen screen)
    {
        Screen = screen;
        Title = "Damian.Rocks overlay";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        IsHitTestVisible = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        // Rough placement (DIPs at system DPI) so the window is born on the right monitor;
        // ApplyBounds then sets the exact physical-pixel bounds.
        Left = screen.Bounds.Left;
        Top = screen.Bounds.Top;
        Width = 200;
        Height = 200;
        Content = _canvas;

        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            long ex = NativeMethods.GetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
            ex |= NativeMethods.WS_EX_TRANSPARENT | NativeMethods.WS_EX_LAYERED |
                  NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE;
            ex &= ~NativeMethods.WS_EX_APPWINDOW;
            NativeMethods.SetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(ex));
            ApplyBounds();
        };
        // WPF resizes the window itself when it lands on a monitor with a different DPI.
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(ApplyBounds);
    }

    private void ApplyBounds()
    {
        if (_hwnd == IntPtr.Zero) return;
        var b = Screen.Bounds;
        NativeMethods.SetWindowPos(_hwnd, NativeMethods.HWND_TOPMOST, b.Left, b.Top, b.Width, b.Height,
            NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOOWNERZORDER);
    }

    /// <summary>Re-asserts top-most z-order (e.g. above a slideshow that started after us).</summary>
    public void BringToTop()
    {
        if (_hwnd == IntPtr.Zero) return;
        NativeMethods.SetWindowPos(_hwnd, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOOWNERZORDER);
    }

    public bool Spawn(ImageSource? image, string fallbackText, double now)
    {
        double w = _canvas.ActualWidth, h = _canvas.ActualHeight;
        if (w < 1 || h < 1 || _active.Count >= MaxActive) return false;
        if (_active.Count == 0) BringToTop();

        var side = Random.Shared.Next(2) == 0 ? Side.Left : Side.Right;
        var reaction = new FloatingReaction(image, fallbackText, side, w, h, now);
        _active.Add(reaction);
        _canvas.Children.Add(reaction.Element);
        return true;
    }

    public void Tick(double now)
    {
        for (int i = _active.Count - 1; i >= 0; i--)
        {
            if (_active[i].Update(now)) continue;
            _canvas.Children.Remove(_active[i].Element);
            _active.RemoveAt(i);
        }
    }

    public void ClearAll()
    {
        _active.Clear();
        _canvas.Children.Clear();
    }
}
