using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using Reflection.Core;
using Forms = System.Windows.Forms;

namespace Reflection.Desktop;

public partial class App
{
    private LocalStore? applicationStore;
    private ApplicationPreferences preferences = new();
    private Forms.NotifyIcon? trayIcon;
    private CancellationTokenSource? activationCancellation;
    private Task activationListener = Task.CompletedTask;
    internal bool CloseToTray => preferences.CloseToTray;
    internal bool TrayAvailable => trayIcon is not null;
    internal string? TrayNotice { get; private set; }
    internal bool IsSessionEnding { get; private set; }

    private async Task InitializeTrayAsync(LocalStore store)
    {
        applicationStore = store;
        try
        {
            preferences = await store.LoadApplicationPreferencesAsync();
            TrayNotice = store.RecoveryNotice;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            TrayNotice = "读取托盘偏好失败：" + ex.Message;
        }

        Forms.NotifyIcon? icon = null;
        try
        {
            icon = new Forms.NotifyIcon { Text = "Reflection" };
            using var resource = GetResourceStream(new Uri("Assets/Reflection.ico", UriKind.Relative)).Stream;
            icon.Icon = new System.Drawing.Icon(resource);
            icon.ContextMenuStrip = new Forms.ContextMenuStrip();
            icon.ContextMenuStrip.Items.Add("打开 Reflection", null, (_, _) => RestoreWindow());
            icon.ContextMenuStrip.Items.Add("退出 Reflection", null, (_, _) => (MainWindow as MainWindow)?.RequestExit());
            icon.MouseClick += OnTrayClick;
            icon.Visible = CloseToTray;
            trayIcon = icon;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or InvalidOperationException or ExternalException)
        {
            DisposeTray(icon);
            TrayNotice = "无法使用系统托盘：" + ex.Message;
            Trace.TraceWarning(TrayNotice);
        }
    }

    internal async Task SaveTrayPreferenceAsync(bool enabled)
    {
        if (trayIcon is not { } icon || applicationStore is not { } store || CloseToTray == enabled) return;
        var next = new ApplicationPreferences(enabled);
        await store.SaveApplicationPreferencesAsync(next);
        preferences = next;
        icon.Visible = enabled;
    }

    private void OnTrayClick(object? sender, Forms.MouseEventArgs e)
    {
        if (e.Button == Forms.MouseButtons.Left) RestoreWindow();
    }

    private bool RestoreWindow() => MainWindow is MainWindow window && window.RestoreFromTray();

    private void ListenForActivation(string pipeName)
    {
        activationCancellation = new CancellationTokenSource();
        var cancellation = activationCancellation.Token;
        activationListener = InstanceActivation.ListenAsync(pipeName, () =>
            Dispatcher.InvokeAsync(RestoreWindow, System.Windows.Threading.DispatcherPriority.Normal, cancellation).Task, cancellation);
    }

    internal async Task StopActivationAsync()
    {
        activationCancellation?.Cancel();
        try { await activationListener; }
        catch (Exception ex) { Trace.TraceWarning("Could not stop Reflection activation: {0}", ex.Message); }
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        base.OnSessionEnding(e);
        IsSessionEnding = !e.Cancel;
    }

    private void ReleaseTray()
    {
        activationCancellation?.Cancel();
        activationCancellation?.Dispose();
        DisposeTray(trayIcon);
        trayIcon = null;
    }

    private void DisposeTray(Forms.NotifyIcon? icon)
    {
        if (icon is null) return;
        var menu = icon.ContextMenuStrip;
        var image = icon.Icon;
        icon.MouseClick -= OnTrayClick;
        icon.Dispose();
        menu?.Dispose();
        image?.Dispose();
    }
}
