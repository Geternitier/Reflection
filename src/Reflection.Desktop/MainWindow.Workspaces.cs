using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Windows.Media;
using System.Windows.Automation;
using Reflection.Core;

namespace Reflection.Desktop;

public partial class MainWindow
{
    private bool switchingWorkspace;
    internal double LastWorkspaceSwitchMilliseconds { get; private set; }
    private void InitializeWorkspaceMode()
    {
        WorkspaceModeButton.IsEnabled = initialized;
        WorkspaceModeButton.Content = Geometry.Parse(Profile.IsPrivate
            ? "M 12,3 L 20,6 L 20,12 Q 20,18 12,22 Q 4,18 4,12 L 4,6 Z M 9,12 L 11,14 L 15,10"
            : "M 12,3 L 20,6 L 20,12 Q 20,18 12,22 Q 4,18 4,12 L 4,6 Z");
        AutomationProperties.SetName(WorkspaceModeButton, Profile.IsPrivate ? "隐私模式，切换到日常模式" : "日常模式，切换到隐私模式");
        PrivateModeIndicator.Visibility = Profile.IsPrivate ? Visibility.Visible : Visibility.Collapsed;
        WorkspaceModeButton.ToolTip = Profile.IsPrivate ? "隐私模式 · 切换到日常模式" : "日常模式 · 切换到隐私模式";
        WorkspaceModeButton.SetResourceReference(BackgroundProperty, Profile.IsPrivate ? "AccentSoft" : "Surface");
        Title = Profile.IsPrivate ? "Reflection · 隐私模式" : "Reflection · 本地角色对话";
    }
    private async void WorkspaceMode_Click(object sender, RoutedEventArgs e) => await Run(async () => { await SwitchWorkspaceAsync(); });
    internal async Task<MainWindow?> SwitchWorkspaceAsync()
    {
        if (!initialized || switchingWorkspace || savingClose) return null;
        if (currentPage == "editor" && !await ConfirmAsync("切换模式", "未保存的角色修改将被放弃。", "切换")) return null;
        switchingWorkspace = true; StopAutoScroll(); CloseChoices();
        WorkspaceRoot.IsHitTestVisible = false; WorkspaceModeButton.IsEnabled = false;
        var timer = Stopwatch.StartNew(); var previous = VM; var oldReader = ReaderPage;
        MainViewModel? next = null;
        try
        {
            await previous.StopAllAsync(); await previous.PersistDraftAsync(); await oldReader.PersistAsync();
            var profile = Profile.Other;
            if (!File.Exists(Path.Combine(profile.Root, "settings.json")))
            {
                var services = JsonSerializer.Deserialize<List<ServiceConfig>>(JsonSerializer.Serialize(previous.Settings.Services))!;
                await new LocalStore(profile.Root).SaveSettingsAsync(new AppSettings { Services = services, Theme = previous.Settings.Theme });
            }
            next = new MainViewModel(profile.Root);
            // Local content readiness never waits for HTTP discovery or model inspection.
            await next.InitializeAsync(waitForDiscovery: false, applyTheme: false);
            var reader = new ReaderView { Visibility = Visibility.Collapsed };
            reader.ConfigureRoot(profile.Root);
            oldReader.Retire();
            previous.ScrollRequested -= RequestChatFollow; previous.ModelInfoChanged -= OnModelInfoChanged;
            previous.PropertyChanged -= WorkspacePropertyChanged;
            oldReader.DocumentList.PreviewMouseDown -= Messages_MouseDown;
            ReaderHeaderHost.Content = null;
            var parent = (Panel)oldReader.Parent; var index = parent.Children.IndexOf(oldReader);
            parent.Children.RemoveAt(index); Grid.SetRow(reader, 1); parent.Children.Insert(index, reader);
            UnregisterName("ReaderPage"); reader.Name = "ReaderPage"; RegisterName("ReaderPage", reader);
            ReaderPage = reader; ReaderHeaderHost.Content = reader.TakeHeader();
            reader.DocumentList.PreviewMouseDown += Messages_MouseDown;
            VM = next; Profile = profile; DataContext = next;
            VM.ScrollRequested += RequestChatFollow; VM.ModelInfoChanged += OnModelInfoChanged;
            VM.PropertyChanged += WorkspacePropertyChanged;
            ThemeManager.Apply(VM.Settings.Theme); FormulaRasterCache.Clear();
            CharacterEditor.BeginEdit(null); TitleBox.Text = ""; followOutput = true;
            InitializeWorkspaceMode(); RefreshSettings(); ShowPage("cards", animate: false);
            previous.Dispose();
            await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Loaded);
            LastWorkspaceSwitchMilliseconds = timer.Elapsed.TotalMilliseconds;
            return this;
        }
        catch { if (!ReferenceEquals(VM, next)) next?.Dispose(); throw; }
        finally { WorkspaceRoot.IsHitTestVisible = true; WorkspaceModeButton.IsEnabled = true; switchingWorkspace = false; }
    }
}
