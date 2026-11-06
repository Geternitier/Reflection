using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Reflection.Desktop;

public partial class MainWindow
{
    private TaskCompletionSource<object?>? choiceCompletion;
    private FrameworkElement? choiceAnchor;
    internal bool ChoiceOpen => choiceCompletion is not null;

    internal Task<object?> ShowChoicesAsync(FrameworkElement anchor, IEnumerable<object> items, object? selected, Func<object, string> label)
    {
        StopAutoScroll(); CloseChoices();
        choiceCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var completion = choiceCompletion; choiceAnchor = anchor;
        var values = items.ToArray();
        var list = new StackPanel();
        KeyboardNavigation.SetDirectionalNavigation(list, KeyboardNavigationMode.Cycle);
        KeyboardNavigation.SetTabNavigation(list, KeyboardNavigationMode.Cycle);
        Button? first = null, active = null;
        foreach (var value in values)
        {
            var button = new Button
            {
                Content = new TextBlock { Text = label(value), TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis }, ToolTip = label(value), Tag = value, Style = (Style)FindResource("Quiet"),
                HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(12, 9, 12, 9), Margin = new Thickness(0, 1, 0, 1),
                Background = Equals(value, selected) ? (Brush)FindResource("AccentSoft") : Brushes.Transparent
            };
            System.Windows.Automation.AutomationProperties.SetName(button, label(value));
            button.Click += (_, _) => CloseChoices(value);
            list.Children.Add(button); first ??= button;
            if (Equals(value, selected)) active = button;
        }
        if (values.Length == 0) list.Children.Add(new TextBlock { Text = "暂无可用选项", FontSize = 12, Margin = new Thickness(12) });
        var point = anchor.TranslatePoint(new Point(0, 0), WorkspaceRoot);
        var width = Math.Min(Math.Max(anchor.ActualWidth, 160), Math.Max(160, ActualWidth - 32));
        if (values.Any(value => label(value).Length > 25)) width = Math.Min(360, Math.Max(width, ActualWidth - 32));
        list.Measure(new Size(Math.Max(80, width - 12), double.PositiveInfinity));
        var height = Math.Min(290, list.DesiredSize.Height + 12);
        var above = ActualHeight - point.Y - anchor.ActualHeight < height + 12;
        var top = above ? point.Y - height - 7 : point.Y + anchor.ActualHeight + 7;
        ChoiceSurface.Width = width;
        ChoiceSurface.MaxHeight = Math.Max(60, Math.Min(290, ActualHeight - 32));
        ChoiceSurface.Margin = new Thickness(Math.Max(16, Math.Min(point.X, ActualWidth - width - 16)), Math.Max(16, top), 0, 0);
        ChoiceSurface.Child = new ScrollViewer { Content = list, MaxHeight = 278, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        ChoiceLayer.Visibility = Visibility.Visible; UpdateLayout();
        (active ?? first)?.Focus();
        return completion.Task;
    }
    internal void CloseChoices(object? selected = null)
    {
        if (choiceCompletion is not { } completion) return;
        choiceCompletion = null; ChoiceLayer.Visibility = Visibility.Collapsed; ChoiceSurface.Child = null;
        var anchor = choiceAnchor; choiceAnchor = null;
        if (anchor?.IsVisible == true && anchor.IsEnabled) anchor.Focus();
        completion.TrySetResult(selected);
    }
    private void ChoiceBackdrop_Down(object sender, MouseButtonEventArgs e) { CloseChoices(); e.Handled = true; }
}
