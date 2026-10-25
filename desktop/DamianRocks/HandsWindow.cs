using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using Forms = System.Windows.Forms;

namespace DamianRocks;

/// <summary>
/// The raised-hands list. A narrow, always-on-top strip on the left or right edge of one
/// monitor (never the middle). It doesn't steal focus, and its transparent parts let clicks
/// through to whatever is underneath, so only the cards themselves are clickable.
/// </summary>
public sealed class HandsWindow : Window
{
    public const string SideLeft = "left";
    public const string SideRight = "right";
    private const int MaxVisible = 7;

    private static readonly Brush CardBrush = Freeze(new SolidColorBrush(Color.FromArgb(0xEE, 0x22, 0x1B, 0x3D)));
    private static readonly Brush CardBorder = Freeze(new SolidColorBrush(Color.FromArgb(0x70, 0xFF, 0xC8, 0x57)));
    private static readonly Brush ActiveBorder = Freeze(new SolidColorBrush(Color.FromRgb(0x3D, 0xDC, 0x97)));
    private static readonly Brush Muted = Freeze(new SolidColorBrush(Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF)));
    private static readonly Brush DismissBrush = Freeze(new SolidColorBrush(Color.FromArgb(0xCC, 0x4A, 0x40, 0x6E)));
    private static readonly Brush TalkingBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0x5D, 0x73)));

    private readonly Forms.Screen _screen;
    private readonly string _side;
    private readonly ImageSource? _handImage;
    private readonly StackPanel _list = new();
    private readonly TextBlock _header = new();
    private readonly Border _headerPill;
    private readonly TextBlock _more = new();
    private readonly Dictionary<string, HandCard> _cards = new();
    private IntPtr _hwnd;

    public event Action<string>? Picked;
    public event Action<string>? Lowered;

    public HandsWindow(Forms.Screen screen, string side, ImageSource? handImage)
    {
        _screen = screen;
        _side = side == SideLeft ? SideLeft : SideRight;
        _handImage = handImage;
        Title = "Damian.Rocks questions";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = screen.WorkingArea.Left;
        Top = screen.WorkingArea.Top;
        Width = 150;
        Height = 200;

        _header.Foreground = Brushes.White;
        _header.FontSize = 12;
        _header.FontWeight = FontWeights.Bold;
        _headerPill = new Border
        {
            Child = _header,
            Background = CardBrush,
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10, 4, 10, 4),
            Margin = new Thickness(0, 0, 0, 6),
            HorizontalAlignment = _side == SideLeft ? HorizontalAlignment.Left : HorizontalAlignment.Right,
        };
        _more.Foreground = Brushes.White;
        _more.FontSize = 12;
        _more.Margin = new Thickness(4, 2, 4, 0);
        _more.HorizontalAlignment = _headerPill.HorizontalAlignment;
        _list.Margin = new Thickness(10, 10, 10, 10);
        _list.VerticalAlignment = VerticalAlignment.Center;
        Content = new Grid { Children = { _list } };
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);

        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            long ex = NativeMethods.GetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
            // Layered (per-pixel alpha → transparent areas click through) but NOT WS_EX_TRANSPARENT.
            ex |= NativeMethods.WS_EX_LAYERED | NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE;
            ex &= ~NativeMethods.WS_EX_APPWINDOW;
            NativeMethods.SetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(ex));
            ApplyBounds();
        };
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(ApplyBounds);
    }

    private void ApplyBounds()
    {
        if (_hwnd == IntPtr.Zero) return;
        var area = _screen.WorkingArea;
        double dpiScale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        // The outer ~10% of the screen, but wide enough for a name (≥165 DIP), capped at 15%.
        int width = (int)Math.Min(_screen.Bounds.Width * 0.15, Math.Max(_screen.Bounds.Width * 0.10, 165 * dpiScale));
        int x = _side == SideLeft ? area.Left : area.Right - width;
        NativeMethods.SetWindowPos(_hwnd, NativeMethods.HWND_TOPMOST, x, area.Top, width, area.Height,
            NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOOWNERZORDER);
    }

    public void BringToTop()
    {
        if (_hwnd == IntPtr.Zero) return;
        NativeMethods.SetWindowPos(_hwnd, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOOWNERZORDER);
    }

    public void Update(IReadOnlyList<HandInfo> hands)
    {
        foreach (var gone in _cards.Keys.Except(hands.Select(h => h.Id)).ToList())
            _cards.Remove(gone);

        // The person on the mic first, then everyone else in the order they raised their hands.
        var ordered = hands.Where(h => h.Active).Concat(hands.Where(h => !h.Active)).ToList();
        _list.Children.Clear();
        if (ordered.Count == 0) return;

        _header.Text = ordered.Count == 1 ? "1 QUESTION" : $"{ordered.Count} QUESTIONS";
        _list.Children.Add(_headerPill);
        int waitingNumber = 0;
        foreach (var hand in ordered.Take(MaxVisible))
        {
            if (!_cards.TryGetValue(hand.Id, out var card))
            {
                card = new HandCard(hand.Id, _handImage, _side);
                card.Picked += id => Picked?.Invoke(id);
                card.Lowered += id => Lowered?.Invoke(id);
                _cards[hand.Id] = card;
            }
            card.Update(hand, hand.Active ? 0 : ++waitingNumber);
            _list.Children.Add(card);
        }
        if (ordered.Count > MaxVisible)
        {
            _more.Text = $"+{ordered.Count - MaxVisible} more";
            _list.Children.Add(_more);
        }
        BringToTop();
    }

    private static T Freeze<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }

    /// <summary>One raised hand. Click it to call on the person; the active card has an End button.</summary>
    private sealed class HandCard : Button
    {
        private readonly string _id;
        private readonly Border _border;
        private readonly TextBlock _name = new();
        private readonly TextBlock _status = new();
        private readonly Ellipse _dot = new() { Width = 8, Height = 8, Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center };
        private readonly Button _end;
        private readonly Button _dismiss;
        private readonly ScaleTransform _scale = new(1, 1);
        private readonly DropShadowEffect _glow = new() { Color = Color.FromRgb(0x3D, 0xDC, 0x97), BlurRadius = 0, ShadowDepth = 0, Opacity = 0.9 };
        private bool? _wasActive;
        private bool? _wasTalking;

        public event Action<string>? Picked;
        public event Action<string>? Lowered;

        public HandCard(string id, ImageSource? handImage, string side)
        {
            _id = id;
            Template = PlainTemplate();
            Cursor = Cursors.Hand;
            Focusable = false;
            Margin = new Thickness(0, 4, 0, 4);
            HorizontalAlignment = HorizontalAlignment.Stretch;
            LayoutTransform = _scale;
            RenderTransformOrigin = new Point(side == SideLeft ? 0 : 1, 0.5);

            var hand = new Image
            {
                Source = handImage,
                Width = 30,
                Height = 30,
                Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center,
                RenderTransformOrigin = new Point(0.7, 0.8),
                RenderTransform = new RotateTransform(),
            };
            hand.RenderTransform.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(-9, 9, TimeSpan.FromSeconds(0.9))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            });

            _name.Foreground = Brushes.White;
            _name.FontSize = 15;
            _name.FontWeight = FontWeights.SemiBold;
            _name.TextWrapping = TextWrapping.Wrap;
            _name.TextTrimming = TextTrimming.CharacterEllipsis;
            _name.MaxHeight = 40;
            _status.Foreground = Muted;
            _status.FontSize = 11;
            _status.TextTrimming = TextTrimming.CharacterEllipsis;
            var statusRow = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(_dot, Dock.Left);
            statusRow.Children.Add(_dot);
            statusRow.Children.Add(_status);
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { _name, statusRow } };

            var row = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(hand, Dock.Left);
            row.Children.Add(hand);
            row.Children.Add(text);

            _end = SmallButton("End", TalkingBrush, new Thickness(0, 8, 0, 0));
            _end.HorizontalAlignment = HorizontalAlignment.Stretch;
            _end.ToolTip = "End this question and lower their hand";
            _end.Click += (_, e) => { e.Handled = true; Lowered?.Invoke(_id); };

            _dismiss = SmallButton("✕", DismissBrush, new Thickness(0));
            _dismiss.HorizontalAlignment = HorizontalAlignment.Right;
            _dismiss.VerticalAlignment = VerticalAlignment.Top;
            _dismiss.Margin = new Thickness(0, -6, -6, 0);
            _dismiss.ToolTip = "Lower this hand";
            _dismiss.Visibility = Visibility.Hidden;
            _dismiss.Click += (_, e) => { e.Handled = true; Lowered?.Invoke(_id); };

            var body = new StackPanel { Children = { row, _end } };
            _border = new Border
            {
                Background = CardBrush,
                BorderBrush = CardBorder,
                BorderThickness = new Thickness(1.5),
                CornerRadius = new CornerRadius(16),
                Padding = new Thickness(10, 8, 10, 8),
                Effect = _glow,
                Child = new Grid { Children = { body, _dismiss } },
            };
            Content = _border;

            MouseEnter += (_, _) => { if (_wasActive == false) _dismiss.Visibility = Visibility.Visible; };
            MouseLeave += (_, _) => _dismiss.Visibility = Visibility.Hidden;
            Click += (_, _) => { if (_wasActive != true) Picked?.Invoke(_id); };

            // Bubble in from below, like the emojis.
            var slide = new TranslateTransform(0, 30);
            RenderTransform = slide;
            Opacity = 0;
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            slide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(450)) { EasingFunction = ease });
            BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(350)));
        }

        public void Update(HandInfo hand, int position)
        {
            _name.Text = hand.Name;
            AutomationProperties.SetName(this, hand.Name);
            AutomationProperties.SetName(_end, $"End {hand.Name}");
            AutomationProperties.SetName(_dismiss, $"Lower {hand.Name}'s hand");
            ToolTip = hand.Active ? null : $"Let {hand.Name} speak";
            _border.Opacity = hand.Online ? 1 : 0.55;

            if (hand.Active)
            {
                _status.Text = !hand.Online ? "Reconnecting…" : hand.Talking ? "Speaking…" : "On the mic";
                _dot.Fill = hand.Talking ? TalkingBrush : ActiveBorder;
            }
            else
            {
                _status.Text = hand.Online ? $"#{position} · waiting" : $"#{position} · offline";
                _dot.Fill = Muted;
            }

            if (_wasActive != hand.Active)
            {
                _wasActive = hand.Active;
                _end.Visibility = hand.Active ? Visibility.Visible : Visibility.Collapsed;
                _dismiss.Visibility = Visibility.Hidden;
                _border.BorderBrush = hand.Active ? ActiveBorder : CardBorder;
                Cursor = hand.Active ? Cursors.Arrow : Cursors.Hand;
                // The person on the mic gets a little bigger.
                var grow = new DoubleAnimation(hand.Active ? 1.15 : 1, TimeSpan.FromMilliseconds(300))
                {
                    EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.4 },
                };
                _scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
                _scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
            }

            bool talking = hand.Active && hand.Talking;
            if (_wasTalking != talking || hand.Active && _glow.BlurRadius == 0)
            {
                _wasTalking = talking;
                _glow.Color = talking ? Color.FromRgb(0xFF, 0x5D, 0x73) : Color.FromRgb(0x3D, 0xDC, 0x97);
                if (talking)
                {
                    _glow.BeginAnimation(DropShadowEffect.BlurRadiusProperty, new DoubleAnimation(10, 28, TimeSpan.FromSeconds(0.55))
                    {
                        AutoReverse = true,
                        RepeatBehavior = RepeatBehavior.Forever,
                    });
                }
                else
                {
                    _glow.BeginAnimation(DropShadowEffect.BlurRadiusProperty, null);
                    _glow.BlurRadius = hand.Active ? 16 : 0;
                }
            }
        }

        private static Button SmallButton(string text, Brush background, Thickness margin) => new()
        {
            Template = PlainTemplate(),
            Focusable = false,
            Cursor = Cursors.Hand,
            Margin = margin,
            Content = new Border
            {
                Background = background,
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(8, 3, 8, 4),
                Child = new TextBlock
                {
                    Text = text,
                    Foreground = Brushes.White,
                    FontSize = 13,
                    FontWeight = FontWeights.Bold,
                    HorizontalAlignment = HorizontalAlignment.Center,
                },
            },
        };

        private static ControlTemplate PlainTemplate() =>
            new(typeof(Button)) { VisualTree = new FrameworkElementFactory(typeof(ContentPresenter)) };
    }
}
