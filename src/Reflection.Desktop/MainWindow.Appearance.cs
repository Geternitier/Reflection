using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;

namespace Reflection.Desktop;

public partial class MainWindow
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint handle, int attribute, ref int value, int size);

    private void RefreshWindowAppearance(object? sender = null, EventArgs? e = null)
    {
        var radius = WindowState == WindowState.Maximized ? 0 : 8;
        WorkspaceRoot.Clip = new RectangleGeometry(new Rect(0, 0, Math.Max(0, WorkspaceRoot.ActualWidth), Math.Max(0, WorkspaceRoot.ActualHeight)), radius, radius);
        if (WindowChrome.GetWindowChrome(this) is { } chrome) chrome.CornerRadius = new CornerRadius(radius);
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)) return;
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == 0) return;
        var preference = radius == 0 ? 1 : 3; // DWM: do not round / small rounded corners.
        _ = DwmSetWindowAttribute(handle, 33, ref preference, sizeof(int));
        var dark = ThemeManager.IsDark ? 1 : 0;
        _ = DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int));
    }
    private async void ToggleTheme_Click(object sender, RoutedEventArgs e) => await Run(VM.ToggleThemeAsync);
}
