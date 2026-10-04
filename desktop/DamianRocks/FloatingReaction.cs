using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DamianRocks;

public enum Side { Left, Right }

/// <summary>
/// One reaction drifting up an overlay like a leaf on a slow breeze: a lazy rise, a gentle
/// side-to-side sway, a small up/down bob, and a slight tilt in the direction of travel.
/// Its horizontal motion is confined to the outer 10% band of the screen on its side.
/// </summary>
public sealed class FloatingReaction
{
    private const double BandFraction = 0.10;

    private readonly double _startTime, _duration;
    private readonly double _width, _height, _size, _baseX, _startY, _endY;
    private readonly double _swayAmp, _swayFreq, _bobAmp, _bobFreq, _phase, _phase2, _maxTilt;
    private readonly Side _side;
    private readonly TranslateTransform _translate = new();
    private readonly RotateTransform _rotate = new();
    private readonly ScaleTransform _scale = new();

    public FrameworkElement Element { get; }

    public FloatingReaction(ImageSource? image, string fallbackText, Side side, double width, double height, double now)
    {
        var rnd = Random.Shared;
        double Rand(double min, double max) => min + rnd.NextDouble() * (max - min);

        _side = side;
        _width = width;
        _height = height;
        _startTime = now;
        _duration = Rand(9.5, 12.5);

        double band = width * BandFraction;
        _size = Math.Clamp(band * 0.5, 40, 140);
        if (_size > band) _size = band;

        // Keep the whole emoji (including sway) inside its band: |sway| <= 1.3 * amp.
        double maxOffset = Math.Max(0, (band - _size) / 2);
        _swayAmp = Rand(0.55, 0.75) * maxOffset / 1.3;
        double jitter = Math.Max(0, maxOffset - _swayAmp * 1.3);
        _baseX = band / 2 + Rand(-jitter, jitter);

        _swayFreq = Rand(0.17, 0.26);
        _bobAmp = Rand(6, 12);
        _bobFreq = Rand(0.55, 0.85);
        _phase = Rand(0, Math.PI * 2);
        _phase2 = Rand(0, Math.PI * 2);
        _maxTilt = Rand(8, 14);
        _startY = height + _size * 0.6;
        _endY = -_size * 0.8;

        Element = image is not null
            ? new Image { Source = image, Stretch = Stretch.Uniform }
            : new TextBlock
            {
                Text = fallbackText,
                FontFamily = new FontFamily("Segoe UI Emoji"),
                FontSize = _size * 0.8,
                TextAlignment = TextAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
        RenderOptions.SetBitmapScalingMode(Element, BitmapScalingMode.HighQuality);
        Element.Width = _size;
        Element.Height = _size;
        Element.IsHitTestVisible = false;
        Element.RenderTransformOrigin = new Point(0.5, 0.5);
        Element.RenderTransform = new TransformGroup { Children = { _scale, _rotate, _translate } };
        Element.Opacity = 0;
        Canvas.SetLeft(Element, 0);
        Canvas.SetTop(Element, 0);
        Update(now);
    }

    /// <summary>Advances the animation. Returns false once it has floated off the top.</summary>
    public bool Update(double now)
    {
        double s = now - _startTime;
        double t = s / _duration;
        if (t >= 1) return false;

        double p = 0.7 * t + 0.3 * (0.5 - 0.5 * Math.Cos(Math.PI * t));
        double w = 2 * Math.PI * _swayFreq * s;
        double sway = Math.Sin(w + _phase) + 0.3 * Math.Sin(2.3 * w + _phase2);
        double offset = _baseX + _swayAmp * sway;
        double cx = _side == Side.Left ? offset : _width - offset;
        double cy = _startY + (_endY - _startY) * p + _bobAmp * Math.Sin(2 * Math.PI * _bobFreq * s + _phase2);

        double pop = s < 0.5 ? 0.55 + 0.45 * EaseOutBack(s / 0.5) : 1;
        _scale.ScaleX = _scale.ScaleY = pop;
        _rotate.Angle = _maxTilt * Math.Cos(w + _phase) * (_side == Side.Left ? 1 : -1);
        _translate.X = cx - _size / 2;
        _translate.Y = cy - _size / 2;
        Element.Opacity = Math.Clamp(Math.Min(t / 0.05, (1 - t) / 0.12), 0, 1);
        return true;
    }

    private static double EaseOutBack(double x)
    {
        const double c1 = 1.70158, c3 = c1 + 1;
        return 1 + c3 * Math.Pow(x - 1, 3) + c1 * Math.Pow(x - 1, 2);
    }
}
