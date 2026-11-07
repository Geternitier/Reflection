using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Threading;

namespace Reflection.Desktop;

public partial class MainWindow
{
    private App DesktopApp => (App)Application.Current;
    private Task windowInitialization = Task.CompletedTask;
    private Task trayPreferenceSave = Task.CompletedTask;
    private WindowState lastRestoredState;
    private bool savingClose;
    private bool closeReady;
    private bool exitRequested;
    private bool hiddenInTray;

    private void InitializeCloseBehavior()
    {
        Closing += OnClosing;
        StateChanged += (_, _) =>
        {
            if (WindowState != WindowState.Minimized) lastRestoredState = WindowState;
        };
        RefreshTrayPreference();
    }

    private void RefreshTrayPreference()
    {
        CloseToTrayPreference.IsChecked = DesktopApp.CloseToTray;
        CloseToTrayPreference.IsEnabled = DesktopApp.TrayAvailable;
        var caption = DesktopApp.TrayAvailable && DesktopApp.CloseToTray ? "关闭到托盘" : "关闭";
        CloseWindowButton.ToolTip = caption;
        AutomationProperties.SetName(CloseWindowButton, caption);
    }

    private async void CloseToTrayPreference_Click(object sender, RoutedEventArgs e)
    {
        if (savingClose || !trayPreferenceSave.IsCompleted) return;
        CloseToTrayPreference.IsEnabled = false;
        trayPreferenceSave = SaveAsync(CloseToTrayPreference.IsChecked == true);
        await trayPreferenceSave;

        async Task SaveAsync(bool enabled)
        {
            await Run(() => DesktopApp.SaveTrayPreferenceAsync(enabled));
            RefreshTrayPreference();
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    internal void RequestExit()
    {
        if (closeReady || switchingWorkspace) return;
        exitRequested = true;
        Close();
    }

    internal bool RestoreFromTray()
    {
        if (closeReady || DesktopApp.IsSessionEnding) return false;
        var wasHidden = hiddenInTray;
        if (wasHidden) { hiddenInTray = false; Show(); }
        if (WindowState == WindowState.Minimized) WindowState = lastRestoredState;
        Activate();
        if (wasHidden) RequestChatFollow();
        return true;
    }

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (closeReady || DesktopApp.IsSessionEnding) return;
        e.Cancel = true;
        if (savingClose || switchingWorkspace) return;
        savingClose = true;
        WorkspaceRoot.IsHitTestVisible = false;
        try
        {
            // Let WPF finish processing Closing before hiding or closing the window.
            await Dispatcher.Yield(DispatcherPriority.Normal);
            await trayPreferenceSave;
            if (DesktopApp.IsSessionEnding) return;
            if (!exitRequested && DesktopApp.TrayAvailable && DesktopApp.CloseToTray)
            {
                StopAutoScroll();
                CloseChoices();
                Hide();
                hiddenInTray = true;
                return;
            }

            await windowInitialization;
            characterSaveCancellation?.Cancel();
            CloseChoices();
            while (OverlayOpen) CompleteOverlay(false);
            if (characterSaveCompleted is { } pendingCharacter) await pendingCharacter.Task;
            await CharacterEditor.CancelImageWorkAsync();
            await VM.CancelImageImportsAsync();
            await VM.StopAllAsync();
            await VM.PersistDraftAsync();
            await ReaderPage.PersistAsync();
            await VM.Images.DrainAsync();
            await VM.CollectImagesAsync();
            await VM.Images.DisposeAsync();
            closeReady = true;
            await DesktopApp.StopActivationAsync();
            _ = Dispatcher.BeginInvoke(new Action(Close));
        }
        catch (Exception ex)
        {
            exitRequested = false;
            VM.Status = "关闭前保存失败：" + ex.Message;
            if (hiddenInTray) RestoreFromTray();
        }
        finally
        {
            if (!closeReady)
            {
                savingClose = false;
                WorkspaceRoot.IsHitTestVisible = true;
            }
        }
    }
}
