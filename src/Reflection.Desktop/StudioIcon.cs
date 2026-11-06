using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace Reflection.Desktop;

/// <summary>Optically sized, two-tone navigation symbols in a shared 32-unit grid.</summary>
public sealed class StudioIcon : FrameworkElement
{
    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(nameof(Kind), typeof(string), typeof(StudioIcon), new FrameworkPropertyMetadata("spark", FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(typeof(StudioIcon), new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.Inherits));
    public string Kind { get => (string)GetValue(KindProperty); set => SetValue(KindProperty, value); }
    public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        var scale = Math.Min(ActualWidth, ActualHeight) / 32;
        dc.PushTransform(new TranslateTransform((ActualWidth - 32 * scale) / 2, (ActualHeight - 32 * scale) / 2));
        dc.PushTransform(new ScaleTransform(scale, scale));
        var pen = new Pen(Foreground, 1.8) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        void Shape(string path, bool fill = false) => dc.DrawGeometry(fill ? Foreground : null, fill ? null : pen, Geometry.Parse(path));
        switch (Kind)
        {
            case "club":
                dc.DrawRoundedRectangle(null, pen, new Rect(5, 5, 9, 9), 3, 3);
                dc.DrawRoundedRectangle(null, pen, new Rect(18, 5, 9, 9), 3, 3);
                dc.DrawRoundedRectangle(null, pen, new Rect(5, 18, 9, 9), 3, 3);
                dc.PushOpacity(.2); dc.DrawRoundedRectangle(Foreground, null, new Rect(18, 18, 9, 9), 3, 3); dc.Pop();
                dc.DrawRoundedRectangle(null, pen, new Rect(18, 18, 9, 9), 3, 3); break;
            case "chat":
                dc.PushOpacity(.25); dc.DrawRoundedRectangle(Foreground, null, new Rect(10, 10, 19, 15), 5, 5); dc.Pop();
                Shape("M 8,5 L 23,5 Q 27,5 27,9 L 27,19 Q 27,23 23,23 L 13,23 L 6,28 L 6,22 Q 3,21 3,18 L 3,10 Q 3,5 8,5 Z");
                Shape("M 9,12 L 21,12 M 9,17 L 17,17"); break;
            case "settings":
                Shape("M 4,9 L 28,9 M 4,23 L 28,23");
                dc.DrawRoundedRectangle(Foreground, null, new Rect(9, 4, 6, 10), 3, 3);
                dc.DrawRoundedRectangle(Foreground, null, new Rect(19, 18, 6, 10), 3, 3); break;
            case "plus": Shape("M 16,6 L 16,26 M 6,16 L 26,16"); break;
            default: Shape("M 16,1 L 19,11 L 28,5 L 22,14 L 32,17 L 22,20 L 27,29 L 18,23 L 15,32 L 12,22 L 3,28 L 9,19 L 0,16 L 10,13 L 5,4 L 14,10 Z", true); break;
        }
        dc.Pop(); dc.Pop();
    }
}
