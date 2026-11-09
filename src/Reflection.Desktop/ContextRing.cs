using System.Windows;
using System.Windows.Media;

namespace Reflection.Desktop;

public sealed class ContextRing : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(double), typeof(ContextRing),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty TrackProperty = DependencyProperty.Register(nameof(Track), typeof(Brush), typeof(ContextRing),
        new FrameworkPropertyMetadata(Brushes.Transparent, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(nameof(Fill), typeof(Brush), typeof(ContextRing),
        new FrameworkPropertyMetadata(Brushes.Transparent, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public Brush Track { get => (Brush)GetValue(TrackProperty); set => SetValue(TrackProperty, value); }
    public Brush Fill { get => (Brush)GetValue(FillProperty); set => SetValue(FillProperty, value); }

    protected override void OnRender(DrawingContext drawing)
    {
        var radius = Math.Min(ActualWidth, ActualHeight) / 2 - 1.5;
        if (radius <= 0) return;
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        drawing.DrawEllipse(null, new Pen(Track, 2), center, radius, radius);
        if (Value <= 0) return;
        var pen = new Pen(Fill, 2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        if (Value >= 1)
        {
            drawing.DrawEllipse(null, pen, center, radius, radius);
            return;
        }
        var angle = Value * Math.Tau;
        var arc = new StreamGeometry();
        using (var path = arc.Open())
        {
            path.BeginFigure(new Point(center.X, center.Y - radius), false, false);
            path.ArcTo(new Point(center.X + radius * Math.Sin(angle), center.Y - radius * Math.Cos(angle)),
                new Size(radius, radius), 0, Value > .5, SweepDirection.Clockwise, true, false);
        }
        drawing.DrawGeometry(null, pen, arc);
    }
}
