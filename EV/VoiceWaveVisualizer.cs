using System.Windows;
using System.Windows.Media;

namespace EV;

public sealed class VoiceWaveVisualizer : FrameworkElement
{
    private double _level;

    public void SetLevel(double level)
    {
        _level = Math.Clamp(level, 0, 1);
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);

        var size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 0)
            return;

        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        var radius = size * 0.25;
        var glowRadius = radius + 8 + _level * 10;

        var brush = TryFindResource("AccentBrush") as Brush ?? Brushes.DeepSkyBlue;
        var muted = TryFindResource("BorderBrush") as Brush ?? Brushes.Gray;

        dc.DrawEllipse(null, new Pen(muted, 1.5), center, glowRadius, glowRadius);
        dc.DrawEllipse(null, new Pen(brush, 2.5), center, radius, radius);

        const int bars = 32;
        for (var i = 0; i < bars; i++)
        {
            var angle = (Math.PI * 2 * i / bars) - Math.PI / 2;
            var variation = 0.55 + 0.45 * Math.Abs(Math.Sin(i * 1.7));
            var length = 5 + _level * 30 * variation;

            var inner = radius + 10;
            var outer = inner + length;

            var start = new Point(
                center.X + Math.Cos(angle) * inner,
                center.Y + Math.Sin(angle) * inner);

            var end = new Point(
                center.X + Math.Cos(angle) * outer,
                center.Y + Math.Sin(angle) * outer);

            dc.DrawLine(
                new Pen(brush, 3) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round },
                start,
                end);
        }

        var core = 5 + _level * 8;
        dc.DrawEllipse(brush, null, center, core, core);
    }
}