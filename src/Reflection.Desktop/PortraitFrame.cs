using System.Windows;
using System.Windows.Controls;

namespace Reflection.Desktop;

public sealed class PortraitFrame : Border
{
    protected override Size MeasureOverride(Size constraint)
    {
        var width = double.IsFinite(constraint.Width) ? constraint.Width : 240;
        if (!double.IsNaN(Width)) width = Math.Min(width, Width);
        var height = width * 4 / 3;
        base.MeasureOverride(new Size(width, height));
        return new Size(width, height);
    }
}
