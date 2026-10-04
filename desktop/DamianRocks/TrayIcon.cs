using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Windows.Forms;

namespace DamianRocks;

/// <summary>The system tray icon (notification area) with its right-click menu.</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _notify;
    private readonly ContextMenuStrip _menu = new();
    private readonly ToolStripMenuItem _status = new() { Enabled = false };
    private readonly ToolStripMenuItem _screens = new("Show on");
    private readonly ToolStripMenuItem _pause = new("Pause reactions") { CheckOnClick = true };
    private readonly ToolStripMenuItem _startup = new("Start with Windows") { CheckOnClick = true };
    private readonly Icon _onlineIcon, _offlineIcon;
    private readonly string _server;
    private ConnectionState _state = ConnectionState.Connecting;
    private string? _detail;
    private int _audience;

    public event Action? QuitRequested;
    public event Action? TestRequested;
    public event Action<bool>? PauseChanged;
    public event Action<bool>? StartupChanged;
    public event Action<string>? ScreenSelected;

    public Func<string> CurrentScreen { get; set; } = () => OverlayManager.AllScreens;

    public TrayIcon(Icon appIcon, string server)
    {
        _server = server;
        _onlineIcon = appIcon;
        _offlineIcon = MakeGrayscale(appIcon);

        var title = new ToolStripMenuItem("Damian.Rocks overlay") { Enabled = false, Font = new Font(_menu.Font, FontStyle.Bold) };
        var serverItem = new ToolStripMenuItem(server) { Enabled = false };
        var test = new ToolStripMenuItem("Send test emoji", null, (_, _) => TestRequested?.Invoke());
        var quit = new ToolStripMenuItem("Quit", null, (_, _) => QuitRequested?.Invoke());

        _pause.CheckedChanged += (_, _) => { PauseChanged?.Invoke(_pause.Checked); UpdateText(); };
        _startup.Click += (_, _) => StartupChanged?.Invoke(_startup.Checked);
        _screens.DropDownItems.Add("(loading)");
        _screens.DropDownOpening += (_, _) => BuildScreenMenu();
        _menu.Opening += (_, _) => _startup.Checked = StartupRegistration.IsEnabled;

        _menu.Items.AddRange(new ToolStripItem[]
        {
            title, serverItem, _status, new ToolStripSeparator(),
            _screens, _pause, test, _startup, new ToolStripSeparator(),
            quit,
        });

        _notify = new NotifyIcon { ContextMenuStrip = _menu, Icon = _offlineIcon, Visible = true };
        _notify.DoubleClick += (_, _) => TestRequested?.Invoke();
        UpdateText();
    }

    public void SetState(ConnectionState state, string? detail)
    {
        _state = state;
        _detail = detail;
        _notify.Icon = state == ConnectionState.Connected ? _onlineIcon : _offlineIcon;
        UpdateText();
    }

    public void SetAudience(int count)
    {
        _audience = count;
        UpdateText();
    }

    public void ShowBalloon(string title, string text) => _notify.ShowBalloonTip(5000, title, text, ToolTipIcon.Info);

    private void UpdateText()
    {
        string status = _state switch
        {
            ConnectionState.Connected => $"● Connected — {_audience} in the audience",
            ConnectionState.Connecting => "○ Connecting…",
            _ => $"○ Disconnected: {_detail}",
        };
        if (_pause.Checked) status += " (paused)";
        _status.Text = status;

        string tip = $"Damian.Rocks — {status.TrimStart('●', '○', ' ')}";
        _notify.Text = tip.Length > 127 ? tip[..124] + "..." : tip;
    }

    private void BuildScreenMenu()
    {
        _screens.DropDownItems.Clear();
        string current = CurrentScreen();
        var screens = Screen.AllScreens;
        bool matched = screens.Any(s => s.DeviceName == current);

        var all = new ToolStripMenuItem("All screens") { Checked = !matched };
        all.Click += (_, _) => ScreenSelected?.Invoke(OverlayManager.AllScreens);
        _screens.DropDownItems.Add(all);
        _screens.DropDownItems.Add(new ToolStripSeparator());

        for (int i = 0; i < screens.Length; i++)
        {
            var s = screens[i];
            string label = $"Screen {i + 1}: {s.Bounds.Width}×{s.Bounds.Height}{(s.Primary ? " (primary)" : "")}";
            var item = new ToolStripMenuItem(label) { Checked = s.DeviceName == current };
            string device = s.DeviceName;
            item.Click += (_, _) => ScreenSelected?.Invoke(device);
            _screens.DropDownItems.Add(item);
        }
    }

    private static Icon MakeGrayscale(Icon icon)
    {
        using var source = icon.ToBitmap();
        using var gray = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(gray))
        {
            var matrix = new ColorMatrix(new[]
            {
                new[] { 0.30f, 0.30f, 0.30f, 0, 0 },
                new[] { 0.59f, 0.59f, 0.59f, 0, 0 },
                new[] { 0.11f, 0.11f, 0.11f, 0, 0 },
                new[] { 0f, 0, 0, 0.55f, 0 },
                new[] { 0f, 0, 0, 0, 1 },
            });
            using var attrs = new ImageAttributes();
            attrs.SetColorMatrix(matrix);
            g.DrawImage(source, new Rectangle(0, 0, source.Width, source.Height), 0, 0, source.Width, source.Height, GraphicsUnit.Pixel, attrs);
        }
        IntPtr hIcon = gray.GetHicon();
        try { return (Icon)Icon.FromHandle(hIcon).Clone(); }
        finally { NativeMethods.DestroyIcon(hIcon); }
    }

    public void Dispose()
    {
        _notify.Visible = false;
        _notify.Dispose();
        _menu.Dispose();
        _offlineIcon.Dispose();
    }
}
