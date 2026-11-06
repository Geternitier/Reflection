using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Reflection.Desktop;

public sealed class ReaderIndentConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => new Thickness(value is int level ? Math.Clamp(level - 1, 0, 5) * 10 : 0, 0, 0, 0);
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
