using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace Reflection.Desktop;

// Shorten the actual draggable thumb, keeping the scroll range and page commands intact.
public sealed class CompactScrollViewportConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length != 6 || values.Take(5).Any(value => value is not double)) return 0d;
        var range = Math.Max(0, (double)values[0] - (double)values[1]);
        var viewport = Math.Max(0, (double)values[2]);
        var length = values[5] is Orientation.Horizontal ? (double)values[4] : (double)values[3];
        if (range <= 0 || viewport <= 0 || length < 20) return viewport;
        var natural = length * viewport / (range + viewport);
        var cap = Math.Min(96, length * .8);
        var thumb = Math.Min(natural, cap);
        return thumb * range / Math.Max(1, length - thumb);
    }
    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
