using System.Windows;
using System.Windows.Controls;

namespace Reflection.Desktop;

public static class ThinkingExpansion
{
    public static readonly DependencyProperty ActiveProperty = DependencyProperty.RegisterAttached("Active", typeof(bool), typeof(ThinkingExpansion), new PropertyMetadata(false, Changed));
    public static bool GetActive(DependencyObject element) => (bool)element.GetValue(ActiveProperty);
    public static void SetActive(DependencyObject element, bool value) => element.SetValue(ActiveProperty, value);
    private static void Changed(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is Expander expander) expander.SetCurrentValue(Expander.IsExpandedProperty, (bool)e.NewValue);
    }
}
