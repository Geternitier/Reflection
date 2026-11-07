using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Reflection.Core;
using Forms = System.Windows.Forms;

namespace Reflection.Desktop;

public partial class App
{
    private async Task VerifyTrayAsync(MainWindow window, string output, bool disabled)
    {
        var icon = trayIcon ?? throw new InvalidOperationException(TrayNotice ?? "Tray icon was not initialized");
        var option = (CheckBox)window.FindName("CloseToTrayPreference");
        var close = (Button)window.FindName("CloseWindowButton");
        var result = Path.Combine(output, "smoke-result.txt");
        Require(icon.Visible == !disabled && option.IsChecked == !disabled, "Unexpected startup tray preference");
        if (disabled)
        {
            await File.WriteAllTextAsync(result, "PASS: persisted tray opt-out exits on window close");
            close.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            return;
        }

        window.ShowPage("chat");
        var viewModel = window.VM;
        var chat = viewModel.Current;
        viewModel.Draft = "navigation slow tray generation";
        var generation = viewModel.SendAsync();
        await WaitAsync(() => viewModel.Busy, "Tray regression generation did not start");
        close.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitAsync(() => !window.IsVisible, "Caption close did not hide the main window");
        Require(viewModel.Busy && icon.Visible, "Hiding the window interrupted generation or removed the tray icon");
        OnTrayClick(icon, new Forms.MouseEventArgs(Forms.MouseButtons.Right, 1, 0, 0, 0));
        Require(!window.IsVisible, "Right click reopened the window");
        await generation.WaitAsync(TimeSpan.FromSeconds(10));
        Require(chat.Messages.Last() is { Incomplete: false, HasError: false, Stopped: false }, "Background generation did not complete");
        viewModel.Draft = "tray draft";
        OnTrayClick(icon, new Forms.MouseEventArgs(Forms.MouseButtons.Left, 1, 0, 0, 0));
        Require(window.IsVisible && ReferenceEquals(window.VM, viewModel) && ReferenceEquals(viewModel.Current, chat)
            && viewModel.Draft == "tray draft", "Tray click did not restore the original window and draft");

        var reader = (ReaderView)window.FindName("ReaderPage");
        var book = Path.Combine(window.Profile.Root, "tray-book.md");
        await File.WriteAllTextAsync(book, string.Concat(Enumerable.Range(1, 80).Select(i => $"## 第 {i} 节\n\n托盘恢复应保留阅读位置。\n\n")));
        window.ShowPage("reader", animate: false);
        await reader.AddPathsAsync([book]);
        window.UpdateLayout();
        var viewer = SmoothScroll.Viewer(reader.DocumentList) ?? throw new InvalidOperationException("Reader has no scroll viewer");
        viewer.ScrollToVerticalOffset(750);
        window.UpdateLayout();
        var readingStore = new ReaderStore(window.Profile.Root);
        await reader.PersistAsync();
        var anchor = (await readingStore.LoadAsync()).Positions[book];
        Require(anchor.Offset > 0, "Tray reader fixture did not scroll");
        window.WindowState = WindowState.Minimized;
        SystemCommands.CloseWindow(window);
        await WaitAsync(() => !window.IsVisible, "System close did not hide the main window");
        await reader.PersistAsync();
        Require((await readingStore.LoadAsync()).Positions[book] == anchor, "Saving the hidden reader lost its source anchor");
        using (var duplicate = Process.Start(Environment.ProcessPath!, "--smoke --tray-activate")
            ?? throw new InvalidOperationException("Could not start a second instance"))
        {
            await duplicate.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8));
            Require(duplicate.ExitCode == 0 && window.IsVisible && window.WindowState == WindowState.Normal,
                $"Second instance did not restore the minimized window (exit {duplicate.ExitCode}, visible {window.IsVisible}, state {window.WindowState})");
            Require(reader.IsVisible && viewer.VerticalOffset > 0, "Restoring the window lost the reader viewport");
        }

        window = await window.SwitchWorkspaceAsync() ?? throw new InvalidOperationException("Could not switch to privacy mode");
        Require(window.Profile.IsPrivate && option.IsChecked == true, "Tray preference changed in privacy mode");
        await SetOptionAsync(false);
        Require(!icon.Visible && !File.Exists(Path.Combine(window.Profile.Root, "application.json")), "Tray preference leaked into workspace settings");
        var root = window.Profile.BaseRoot;
        window = await window.SwitchWorkspaceAsync() ?? throw new InvalidOperationException("Could not return to daily mode");
        Require(option.IsChecked == false && !(await new LocalStore(root).LoadApplicationPreferencesAsync()).CloseToTray,
            "Disabling tray was not shared or persisted");
        await SetOptionAsync(true);

        window.ShowPage("chat");
        window.VM.Draft = "preserve draft on tray exit failure";
        await window.VM.PersistDraftAsync();
        var chatPath = Path.Combine(root, "chats", window.VM.Current.Id + ".json");
        close.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitAsync(() => !window.IsVisible, "Main window did not hide before exit");
        using (File.Open(chatPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            icon.ContextMenuStrip!.Items[1].PerformClick();
            await WaitAsync(() => window.IsVisible && window.VM.Status.StartsWith("关闭前保存失败", StringComparison.Ordinal),
                "Tray exit did not show the window and the save error");
            Require(window.VM.Draft == "preserve draft on tray exit failure", "Failed exit lost the draft");
        }

        await File.WriteAllTextAsync(result, "PASS: tray hide, background generation, single-click restore, second instance activation, shared preference and exit retry");
        icon.ContextMenuStrip!.Items[1].PerformClick();

        async Task SetOptionAsync(bool value)
        {
            option.IsChecked = value;
            option.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitAsync(() => option.IsEnabled, "Tray preference write did not finish");
            Require(option.IsChecked == value, "Tray preference was not saved");
        }
    }

    private static async Task WaitAsync(Func<bool> condition, string message)
    {
        var elapsed = Stopwatch.StartNew();
        while (!condition())
        {
            if (elapsed.Elapsed > TimeSpan.FromSeconds(8)) throw new TimeoutException(message);
            await Task.Delay(20);
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
