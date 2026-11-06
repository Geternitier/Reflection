using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace Reflection.Desktop;

internal sealed class InlineNotice : UserControl
{
    internal Button PrimaryButton { get; }
    internal Button? CancelButton { get; }
    internal Button CloseButton { get; }
    public event EventHandler<bool>? Completed;

    internal InlineNotice(string title, string message, bool confirmation = false, string action = "确定", bool dangerous = false)
    {
        Width = 460;
        AutomationProperties.SetName(this, title);
        var layout = new Grid { Margin = new Thickness(24) };
        layout.RowDefinitions.Add(new() { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 14) };
        CloseButton = new Button { Content = FindResource("IconClose"), Style = (Style)FindResource("MiniIconButton"), ToolTip = "关闭", Margin = new Thickness(12, -4, -6, 0) };
        AutomationProperties.SetName(CloseButton, "关闭提示");
        CloseButton.Click += (_, _) => Completed?.Invoke(this, false);
        DockPanel.SetDock(CloseButton, Dock.Right); header.Children.Add(CloseButton);
        header.Children.Add(new TextBlock { Text = title, FontSize = 19, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        layout.Children.Add(header);
        var body = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 260, Margin = new Thickness(0, 0, 0, 26), Content = new TextBlock { Text = message, FontSize = 13, LineHeight = 23, TextWrapping = TextWrapping.Wrap, Foreground = (System.Windows.Media.Brush)FindResource("Muted") } };
        Grid.SetRow(body, 1); layout.Children.Add(body);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        if (confirmation)
        {
            CancelButton = new Button { Content = "取消", IsDefault = true, MinWidth = 78, Margin = new Thickness(0, 0, 10, 0) };
            CancelButton.Click += (_, _) => Completed?.Invoke(this, false); buttons.Children.Add(CancelButton);
        }
        PrimaryButton = new Button { Content = action, Style = (Style)FindResource("Primary"), MinWidth = 88, IsDefault = !confirmation };
        if (dangerous) PrimaryButton.Background = (System.Windows.Media.Brush)FindResource("Danger");
        PrimaryButton.Click += (_, _) => Completed?.Invoke(this, true); buttons.Children.Add(PrimaryButton);
        Grid.SetRow(buttons, 2); layout.Children.Add(buttons);
        Content = layout;
    }
}
