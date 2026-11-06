using System.Windows;
using System.Windows.Media;

namespace Reflection.Desktop;

public sealed class MemphisArt : FrameworkElement
{
    public static readonly DependencyProperty VariantProperty = DependencyProperty.Register(nameof(Variant), typeof(string), typeof(MemphisArt), new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));
    public MemphisArt() { Loaded += (_, _) => { ThemeManager.Changed += ThemeChanged; InvalidateVisual(); }; Unloaded += (_, _) => ThemeManager.Changed -= ThemeChanged; }
    private void ThemeChanged(object? sender, EventArgs e) => InvalidateVisual();
    public string Variant { get => (string)GetValue(VariantProperty); set => SetValue(VariantProperty, value); }
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc); if (ActualWidth <= 0 || ActualHeight <= 0) return;
        var seed = 0; foreach (var c in Variant ?? "") seed = (seed * 31 + c) & 0x7fffffff;
        var type = seed % 3;
        Brush Color(string hex) => new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        var grounds = ThemeManager.IsDark ? new[] { "#35332A", "#2D352C", "#3B302B" } : new[] { "#E8E5DB", "#E5E8DF", "#EAE2DD" };
        var inks = ThemeManager.IsDark ? new[] { "#9F947A", "#8C9A80", "#AC8F7A" } : new[] { "#99917D", "#8C9785", "#A48C7E" };
        dc.PushClip(new RectangleGeometry(new Rect(RenderSize), 12, 12));
        dc.DrawRectangle(Color(grounds[type]), null, new Rect(RenderSize));
        var scale = Math.Min(ActualWidth / 260, ActualHeight / 170);
        dc.PushTransform(new TranslateTransform((ActualWidth - 260 * scale) / 2, (ActualHeight - 170 * scale) / 2));
        dc.PushTransform(new ScaleTransform(scale, scale));
        var pen = new Pen(Color(inks[type]), 1.25) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        void Shape(string p) => dc.DrawGeometry(null, pen, Geometry.Parse(p));
        if (type == 0)
        {
            dc.DrawEllipse(Color("#CFC5AB"), null, new Point(126, 57), 12, 12);
            Shape("M 51,140 L 206,140 M 68,140 L 68,32 L 178,32 L 178,140 M 78,140 L 78,42 L 168,42 L 168,140 M 123,42 L 123,140 M 78,86 L 168,86 M 87,140 L 87,124 Q 87,97 112,97 Q 139,97 139,124 L 139,140 M 172,140 Q 190,112 177,85 Q 205,102 187,134 Q 204,108 222,111 Q 214,133 187,140");
        }
        else if (type == 1)
        {
            dc.DrawEllipse(Color("#C3CBB8"), null, new Point(72, 45), 13, 13);
            Shape("M 39,139 L 221,139 M 53,139 L 91,64 L 134,139 M 104,139 L 156,35 L 210,139 M 140,67 L 156,35 L 173,67 M 74,96 L 91,64 L 109,96 M 175,139 L 175,102 M 161,120 L 175,102 L 189,120 M 45,153 Q 68,143 91,153 Q 114,162 137,153 Q 160,143 183,153 Q 199,159 218,152");
        }
        else
        {
            dc.DrawEllipse(Color("#D1B6A5"), null, new Point(68, 42), 14, 14);
            Shape("M 47,142 L 211,142 M 71,142 L 71,66 L 130,66 L 130,142 M 80,77 L 93,77 L 93,94 L 80,94 Z M 107,77 L 120,77 L 120,94 L 107,94 Z M 80,106 L 93,106 L 93,123 L 80,123 Z M 107,106 L 120,106 L 120,123 L 107,123 Z M 130,142 L 130,33 L 174,33 L 174,142 M 140,48 L 148,48 L 148,61 L 140,61 Z M 158,48 L 166,48 L 166,61 L 158,61 Z M 140,76 L 148,76 L 148,89 L 140,89 Z M 158,76 L 166,76 L 166,89 L 158,89 Z M 140,104 L 148,104 L 148,117 L 140,117 Z M 158,104 L 166,104 L 166,117 L 158,117 Z");
        }
        dc.Pop(); dc.Pop(); dc.Pop();
    }
}
