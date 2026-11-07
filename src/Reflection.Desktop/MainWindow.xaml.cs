using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Animation;
using Reflection.Core;

namespace Reflection.Desktop;

public partial class MainWindow : Window
{
    internal MainViewModel VM { get; private set; }
    internal WorkspaceProfile Profile { get; private set; }
    private readonly Dictionary<string, TextBox> paramBoxes = [];
    private bool followOutput = true;
    private bool initialized;
    private bool refreshingSettings;
    private bool sidebarOpen = true;
    private string currentPage = "cards";
    internal ScrollViewer CardsPage => SmoothScroll.Viewer(CharacterGallery)!;
    private bool savingCharacter;
    private string titleBeforeEdit = "";
    public MainWindow(WorkspaceProfile? profile = null)
    {
        Profile = profile ?? new WorkspaceProfile(LocalStore.ResolveRoot()); VM = new(Profile.Root);
        InitializeComponent(); ReaderPage.ConfigureRoot(Profile.Root); ReaderHeaderHost.Content = ReaderPage.TakeHeader(); DataContext = VM; InitializeScrolling(); InitializeWorkspaceMode(); ShowPage("cards");
        SourceInitialized += RefreshWindowAppearance;
        StateChanged += RefreshWindowAppearance;
        WorkspaceRoot.SizeChanged += (_, _) => RefreshWindowAppearance();
        ThemeManager.Changed += RefreshWindowAppearance;
        OverlayContent.SizeChanged += (_, _) => OverlayContent.Clip = new RectangleGeometry(new Rect(0, 0, OverlayContent.ActualWidth, OverlayContent.ActualHeight), 16, 16);
        VM.ScrollRequested += RequestChatFollow;
        VM.ModelInfoChanged += OnModelInfoChanged;
        Loaded += InitializeWindow;
        Closed += (_, _) => { ThemeManager.Changed -= RefreshWindowAppearance; ReaderPage.Retire(); VM.Dispose(); };
        InitializeCloseBehavior();
        SizeChanged += (_, _) => { StopAutoScroll(); CloseChoices(); if (ActualWidth < 1100) sidebarOpen = false; UpdateResponsiveLayout(); UpdateOverlaySize(); };
        PreviewKeyDown += (_, e) => { if (switchingWorkspace || savingClose) { e.Handled = true; return; } if (AutoScrollActive) { StopAutoScroll(); if (e.Key == Key.Escape) { e.Handled = true; return; } } if (ChoiceOpen) { if (e.Key == Key.Escape) { CloseChoices(); e.Handled = true; } return; } if (HandleOverlayKey(e)) return; if (e.Key == Key.N && Keyboard.Modifiers == ModifierKeys.Control) { NewChat_Click(this, new()); e.Handled = true; } if (e.Key == Key.Escape && currentPage != "reader") { if (VM.Busy) VM.Stop(); else if (sidebarOpen && ActualWidth < 1100) SetSidebar(false); } };
    }
    internal async Task VerifyChromeAsync(string output)
    {
        ShowPage("chat"); UpdateLayout();
        var height = TopBar.ActualHeight;
        var width = TopBar.ActualWidth;
        TitleButton.Focus(); TitleButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        UpdateLayout();
        if (TopBar.ActualHeight != height || TopBar.ActualWidth != width || TitleBox.Visibility != Visibility.Visible)
            throw new InvalidOperationException("Toolbar changed size during title editing");
        EndTitleEdit(false);
        ReasonToggle.IsChecked = true; await Task.Delay(100); UpdateLayout();
        if (!ChoiceOpen || OverlayOpen) throw new InvalidOperationException("Reason selection is not anchored inside workspace");
        App.Capture(this, Path.Combine(output, "reason-menu.png"));
        CloseChoices(); await Task.Delay(20);

    }
    internal async Task VerifyStudioAsync(string output)
    {
        ShowPage("cards"); await Task.Delay(260); UpdateLayout();
        var cards = VM.Characters.Take(3).ToArray();
        if (cards.Length < 2) throw new InvalidOperationException("Missing studio fixtures");
        var boxes = cards.Select(card => CharacterGallery.ItemContainerGenerator.ContainerFromItem(card) as ListBoxItem).ToArray();
        if (boxes.Any(box => box is null || box.ActualWidth < 220))
            throw new InvalidOperationException("Fluid character gallery has undersized columns");
        if (boxes.Max(box => box!.ActualWidth) - boxes.Min(box => box!.ActualWidth) > 1)
            throw new InvalidOperationException("Character columns have inconsistent widths");
        VM.DetailCard = cards[0];
        var action = FindChild<Button>(boxes[1]) ?? throw new InvalidOperationException("Card has no detail action");
        action.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if (VM.DetailCard?.Id != cards[1].Id || currentPage != "detail")
            throw new InvalidOperationException("Card did not open its own detail page");
        await Task.Delay(260); UpdateLayout();
        if (DetailImagePanel.ActualWidth < 200) throw new InvalidOperationException("Detail image has collapsed");
        App.Capture(this, Path.Combine(output, "character-detail.png"));
        StartDetailButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        for (var i = 0; i < 100 && (VM.Current.CharacterId != cards[1].Id || currentPage != "chat"); i++) await Task.Delay(20);
        if (VM.Current.CharacterId != cards[1].Id || currentPage != "chat")
            throw new InvalidOperationException("Detail page started a chat with the wrong character");
        await VM.NewChatAsync(cards[0]);
        SetSidebar(true); UpdateLayout();
        if (NavigationColumn.ActualWidth != 254 || SidebarPanel.Visibility != Visibility.Visible)
            throw new InvalidOperationException("Desktop conversation drawer has unexpected layout");
        var originalWidth = Width; var originalHeight = Height;
        Width = 720; Height = 620; await Task.Delay(100);
        SetSidebar(false); UpdateLayout(); var composerWidth = Composer.ActualWidth;
        SetSidebar(true); UpdateLayout();
        if (SidebarColumn.ActualWidth != 0 || Composer.ActualWidth != composerWidth || Grid.GetColumn(SidebarPanel) != 2 || DrawerScrim.Visibility != Visibility.Visible)
            throw new InvalidOperationException("Overlay drawer shifted the compact composer");
        await Task.Delay(260); App.Capture(this, Path.Combine(output, "drawer-overlay.png"));
        DrawerScrim.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = MouseLeftButtonDownEvent });
        if (SidebarPanel.Visibility != Visibility.Collapsed || DrawerScrim.Visibility != Visibility.Collapsed)
            throw new InvalidOperationException("Overlay backdrop did not dismiss the drawer");
        SetSidebar(false); Width = originalWidth; Height = originalHeight; await Task.Delay(100);
        SetSidebar(true); ShowPage("cards");
    }
    internal void SetSidebar(bool visible)
    {
        sidebarOpen = visible;
        UpdateResponsiveLayout();
    }
    internal async Task VerifyComponentsAsync(string output)
    {
        ShowPage("chat"); await Task.Delay(260); UpdateLayout();
        var keys = new[] { "Edit", "Copy", "Delete", "Export", "Import", "Continue", "Refresh", "Image", "Attach", "Close", "Menu", "Role", "Settings", "Send", "Stop", "Help", "Plus", "Minimize", "Maximize", "Chevron", "Reason" };
        var atlas = new System.Windows.Controls.Primitives.UniformGrid { Columns = 7, Width = 560, Background = Brushes.White };
        foreach (var key in keys)
        {
            var geometry = (Geometry)FindResource("Icon" + key);
            if (geometry.Bounds.Left < 1 || geometry.Bounds.Top < 1 || geometry.Bounds.Right > 23 || geometry.Bounds.Bottom > 23)
                throw new InvalidOperationException("Icon escapes its shared viewport: " + key);
            var cell = new StackPanel { Margin = new Thickness(10, 16, 10, 16) };
            cell.Children.Add(new Glyph { Data = geometry, Width = 24, Height = 24, Foreground = (Brush)FindResource("Ink"), HorizontalAlignment = HorizontalAlignment.Center });
            cell.Children.Add(new TextBlock { Text = key, FontSize = 9, Margin = new Thickness(0, 8, 0, 0), HorizontalAlignment = HorizontalAlignment.Center });
            atlas.Children.Add(cell);
        }
        atlas.Measure(new Size(560, double.PositiveInfinity)); atlas.Arrange(new Rect(atlas.DesiredSize)); atlas.UpdateLayout();
        App.CaptureElement(atlas, Path.Combine(output, "icon-atlas-200.png"), 192);
        foreach (var dpi in new[] { 96, 144, 192 })
            App.CaptureElement(ComposerPanel, Path.Combine(output, $"composer-{dpi}.png"), dpi);
        var originalModel = VM.SelectedModel;
        var alternate = new Reflection.Core.ModelOption(originalModel?.Service ?? new(), "dropdown-selection-fixture");
        VM.Models.Add(alternate);
        if (ModelPicker.InputHitTest(new Point(20, 10)) is null) throw new InvalidOperationException("Model picker has no mouse hit target");
        ModelPicker.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = PreviewMouseLeftButtonDownEvent });
        await Task.Delay(100); UpdateLayout();
        if (!ChoiceOpen || OverlayOpen || ModelPicker.IsDropDownOpen) throw new InvalidOperationException("Model selection is not an anchored menu");
        App.Capture(this, Path.Combine(output, "model-menu-200.png"));
        var menuButtons = ((StackPanel)((ScrollViewer)ChoiceSurface.Child).Content).Children.OfType<Button>();
        menuButtons.Single(button => ReferenceEquals(button.Tag, alternate)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitOverlayStateAsync(() => ReferenceEquals(VM.SelectedModel, alternate));
        if (ChoiceOpen) throw new InvalidOperationException("Model selection did not close");
        ModelPicker.SetCurrentValue(ComboBox.SelectedItemProperty, originalModel); VM.Models.Remove(alternate);
        var expanded = new Expander { Header = "高级设置", Content = new TextBlock { Text = "内容区域" }, Width = 240, IsExpanded = true };
        expanded.Measure(new Size(240, double.PositiveInfinity)); expanded.Arrange(new Rect(expanded.DesiredSize)); expanded.UpdateLayout();
        if (expanded.ActualHeight < 50) throw new InvalidOperationException("Expanded content is missing");
        App.CaptureElement(expanded, Path.Combine(output, "expander-200.png"), 192);
    }
    private void UpdateResponsiveLayout()
    {
        var showDrawer = sidebarOpen;
        var overlay = ActualWidth < 1100;
        NavigationColumn.Width = new GridLength(showDrawer && !overlay ? 254 : 0);
        SidebarColumn.Width = new GridLength(0);
        SidebarPanel.Visibility = showDrawer ? Visibility.Visible : Visibility.Collapsed;
        DrawerScrim.Visibility = showDrawer && overlay ? Visibility.Visible : Visibility.Collapsed;
        Grid.SetColumn(SidebarPanel, overlay ? 2 : 0);
        Grid.SetRow(SidebarPanel, overlay ? 1 : 0);
        Grid.SetRowSpan(SidebarPanel, overlay ? 1 : 2);
        Panel.SetZIndex(SidebarPanel, 10);
        SidebarPanel.Width = 254;
        SidebarPanel.HorizontalAlignment = HorizontalAlignment.Left;
        var narrow = ActualWidth < 1120;
        var compact = ActualHeight < 780;
        WelcomeHeadline.FontSize = 26;
        WelcomeContent.Margin = new Thickness(32, compact ? 8 : 20, 32, 20);
        CardsCanvas.Margin = new Thickness(narrow ? 28 : 40, 6, narrow ? 28 : 40, 32);
        SettingsCanvas.Margin = new Thickness(narrow ? 28 : 40, 12, narrow ? 28 : 40, 36);
        DetailImageColumn.Width = new GridLength(ActualWidth < 1000 ? 0 : 300);
        Grid.SetColumn(DetailImagePanel, ActualWidth < 1000 ? 1 : 0);
        Grid.SetRow(DetailContentPanel, ActualWidth < 1000 ? 1 : 0);
        DetailImagePanel.Width = ActualWidth < 1000 ? 280 : 272;
        DetailImagePanel.Margin = new Thickness(0, 0, ActualWidth < 1000 ? 0 : 28, ActualWidth < 1000 ? 24 : 0);
        DetailImagePanel.HorizontalAlignment = HorizontalAlignment.Left;
        SettingsAsideColumn.Width = new GridLength(narrow ? 0 : 300);
        Grid.SetColumn(SettingsAside, narrow ? 0 : 1);
        Grid.SetRow(SettingsAside, narrow ? 1 : 0);
        SettingsAside.Margin = new Thickness(narrow ? 0 : 24, narrow ? 24 : 0, 0, 0);
    }
    private async void ReasonToggle_Checked(object sender, RoutedEventArgs e)
    {
        var choice = await InlineSelection.ShowAsync(this, ReasonToggle, new object[] { "关", "低", "中", "高" }, VM.ReasonLabel, x => x.ToString()!);
        ReasonToggle.IsChecked = false;
        if (choice is string text) Reason_Click(new Button { Tag = text switch { "低" => "low", "中" => "medium", "高" => "high", _ => "off" } }, new());
    }
    private async void ToggleHistory_Click(object sender, RoutedEventArgs e) => await Run(VM.ToggleHistoryAsync);
    private void ToggleSidebar_Click(object sender, RoutedEventArgs e) => SetSidebar(!sidebarOpen);
    private void DismissDrawer_Click(object sender, MouseButtonEventArgs e) { SetSidebar(false); e.Handled = true; }
    private void Minimize_Click(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);
    private void Maximize_Click(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
        else SystemCommands.MaximizeWindow(this);
    }
    private void InitializeWindow(object sender, RoutedEventArgs e)
    {
        Loaded -= InitializeWindow;
        windowInitialization = Run(async () =>
        {
            await VM.InitializeAsync(waitForDiscovery: false);
            RefreshSettings();
            initialized = true;
            WorkspaceModeButton.IsEnabled = true;
            if (DesktopApp.TrayNotice is { } notice) VM.Status = notice;
        });
    }
    private async Task Run(Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException) { VM.Status = "操作已取消"; }
        catch (Exception ex) { VM.Status = "操作未完成：" + ex.Message; }
    }
    internal void ShowPage(string name, bool animate = true)
    {
        var changing = name != currentPage;
        if (currentPage == "editor" && name != "editor") CharacterEditor.CancelImageWork();
        StopAutoScroll(); CloseChoices();
        ChatPage.Visibility = name == "chat" ? Visibility.Visible : Visibility.Collapsed;
        CardsRoot.Visibility = name == "cards" ? Visibility.Visible : Visibility.Collapsed;
        ReaderPage.Visibility = name == "reader" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = name == "settings" ? Visibility.Visible : Visibility.Collapsed;
        CharacterDetailPage.Visibility = name == "detail" ? Visibility.Visible : Visibility.Collapsed;
        CharacterEditor.Visibility = name == "editor" ? Visibility.Visible : Visibility.Collapsed;
        ComposerDropHint.Visibility = Visibility.Collapsed;
        currentPage = name;
        if (name == "chat") PageLabel.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("Current.Title"));
        else { System.Windows.Data.BindingOperations.ClearBinding(PageLabel, TextBlock.TextProperty); PageLabel.Text = name switch { "cards" => "角色", "reader" => "阅读", "detail" => VM.DetailCard?.Name ?? "角色详情", "editor" => CharacterEditor.Original is null ? "创建角色" : "编辑角色", _ => "设置" }; }
        PageLabel.Visibility = Visibility.Visible;
        CardsHeaderActions.Visibility = name == "cards" ? Visibility.Visible : Visibility.Collapsed;
        ReaderHeaderHost.Visibility = name == "reader" ? Visibility.Visible : Visibility.Collapsed;
        ConnectionBadge.Visibility = name is "chat" or "settings" ? Visibility.Visible : Visibility.Collapsed;
        TitleButton.Visibility = name == "chat" ? Visibility.Visible : Visibility.Collapsed;
        TitleBox.Visibility = Visibility.Collapsed;
        HistoryToggle.Visibility = Visibility.Visible;
        ChatHeaderActions.Visibility = name == "chat" ? Visibility.Visible : Visibility.Collapsed;
        foreach (var (button, key) in new[] { (ChatNav, "chat"), (CardsNav, "cards"), (SettingsNav, "settings"), (ReaderNav, "reader") })
        {
            var active = name == key || key == "cards" && name is "detail" or "editor";
            if (active) button.SetResourceReference(BackgroundProperty, "Yellow"); else button.Background = Brushes.Transparent;
            button.SetResourceReference(ForegroundProperty, "Ink");
        }
        UpdateResponsiveLayout();
        FrameworkElement page = name switch { "chat" => ChatPage, "cards" => CardsRoot, "reader" => ReaderPage, "detail" => CharacterDetailPage, "editor" => CharacterEditor, _ => SettingsPage };
        // Brief entry transition; no perpetual animation or layout movement.
        if (!animate) page.BeginAnimation(OpacityProperty, null);
        if (animate && changing && IsLoaded && SystemParameters.ClientAreaAnimation)
            page.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)) { FillBehavior = FillBehavior.Stop });
    }
    private void ReaderNav_Click(object sender, RoutedEventArgs e) { VM.Status = ""; ShowPage("reader"); ReaderPage.Focus(); }
    private void ChatNav_Click(object sender, RoutedEventArgs e) => ShowPage("chat");
    private void CardsNav_Click(object sender, RoutedEventArgs e) { VM.Status = ""; ShowPage("cards"); }
    private void OnModelInfoChanged() => Dispatcher.Invoke(UpdateParamDefaults);
    private async void SettingsNav_Click(object sender, RoutedEventArgs e)
    {
        var owner = VM; RefreshSettings(); ShowPage("settings");
        try
        {
            if (owner.SelectedModel is { } model) await owner.EnsureModelInfoAsync(model);
            if (ReferenceEquals(VM, owner) && currentPage == "settings") RefreshSettings();
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (Exception ex) { if (ReferenceEquals(VM, owner)) owner.Status = ex.Message; }
    }
    private async void NewChat_Click(object sender, RoutedEventArgs e) { await Run(() => VM.NewChatAsync()); HistoryList.SelectedItem = null; followOutput = true; ShowPage("chat"); Composer.Focus(); }
    private async void History_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (HistoryList.SelectedItem is ConversationSummary item) await Run(async () => { followOutput = true; await VM.OpenAsync(item); ShowPage("chat"); });
    }
    private async void Send_Click(object sender, RoutedEventArgs e) => await Run(async () => { followOutput = true; await VM.SendAsync(); });
    private async void Regenerate_Click(object sender, RoutedEventArgs e) { if (VM.Idle) await Run(() => VM.SendAsync(true)); }
    private async void Continue_Click(object sender, RoutedEventArgs e) { if (VM.Idle || VM.Busy) await Run(VM.ContinueAsync); }
    private async void Help_Click(object sender, RoutedEventArgs e) => await Run(async () => { followOutput = false; await VM.HelpDraftAsync(); Composer.Focus(); Composer.CaretIndex = Composer.Text.Length; });
    private void Composer_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Down && Keyboard.Modifiers == ModifierKeys.None && Composer.SelectionLength == 0)
        {
            var line = Composer.GetLineIndexFromCharacterIndex(Composer.CaretIndex);
            if (line >= 0 && line == Composer.LineCount - 1)
            {
                Composer.CaretIndex = Composer.Text.Length;
                Composer.ScrollToEnd(); e.Handled = true; return;
            }
        }
        if (e.Key == Key.Enter && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) { e.Handled = true; Send_Click(sender, new()); }
    }
    private async void Composer_Pasting(object sender, DataObjectPastingEventArgs e)
    {
        if (e.FormatToApply != DataFormats.Bitmap && e.FormatToApply != DataFormats.Dib && e.FormatToApply != DataFormats.FileDrop) return;
        e.CancelCommand();
        if (!VM.Idle || savingClose || switchingWorkspace) return;
        if (VM.AvailableImageSlots == 0) { VM.Status = "一次最多 4 张图片"; return; }
        try
        {
            var data = e.DataObject;
            if (!data.GetDataPresent(DataFormats.FileDrop) && Clipboard.GetImage() is BitmapSource bitmap) data = new DataObject(DataFormats.Bitmap, bitmap);
            await ReceiveComposerDrop(data);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException or InvalidOperationException or System.Runtime.InteropServices.COMException) { VM.Status = ex.Message; }
    }
    private void Messages_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (AutoScrollActive) { followOutput = false; return; }
        if (e.OriginalSource is ScrollViewer scroll && e.ExtentHeightChange == 0 && e.VerticalChange != 0)
            followOutput = scroll.VerticalOffset >= scroll.ScrollableHeight - 48;
    }
    private async void CopyMessage_Click(object sender, RoutedEventArgs e) => await Run(() => { if (((FrameworkElement)sender).DataContext is ChatMessage msg) { Clipboard.SetText(msg.Content); VM.Status = "消息已复制"; } return Task.CompletedTask; });
    private async void EditMessage_Click(object sender, RoutedEventArgs e)
    {
        if (VM.Busy || ((FrameworkElement)sender).DataContext is not ChatMessage msg) return;
        var edited = await EditorDialog.EditMessageAsync(this, msg);
        if (edited is not null) await Run(() => VM.EditMessageAsync(msg, edited));
    }
    private void BeginTitleEdit(object sender, RoutedEventArgs e)
    {
        if (!VM.Idle) return;
        titleBeforeEdit = VM.Current.Title; TitleBox.Text = titleBeforeEdit; TitleBox.Visibility = Visibility.Visible; TitleButton.Visibility = Visibility.Collapsed; TitleBox.Focus(); TitleBox.SelectAll();
    }
    private async void TitleBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { TitleBox.Text = titleBeforeEdit; EndTitleEdit(false); e.Handled = true; }
        else if (e.Key == Key.Enter) { e.Handled = true; await CommitTitleAsync(); }
    }
    private async void TitleBox_LostFocus(object sender, RoutedEventArgs e) { if (TitleBox.Visibility == Visibility.Visible) await CommitTitleAsync(); }
    private async Task CommitTitleAsync()
    {
        var title = TitleBox.Text.Trim();
        EndTitleEdit(false);
        if (title.Length == 0 || title == VM.Current.Title) return;
        VM.Current.Title = title; await Run(VM.SaveCurrentAsync);
    }
    private void EndTitleEdit(bool _) { TitleBox.Visibility = Visibility.Collapsed; TitleButton.Visibility = Visibility.Visible; }
    private void RenameHistory_Down(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (!VM.Idle) return;
        var summary = (ConversationSummary)((FrameworkElement)sender).DataContext;
        foreach (var item in VM.Conversations) item.Renaming = item == summary;
        Dispatcher.BeginInvoke(() =>
        {
            if (HistoryList.ItemContainerGenerator.ContainerFromItem(summary) is not ListBoxItem container) return;
            var box = FindChild<TextBox>(container); if (box is null) return;
            box.Tag = summary.Title; box.Focus(); box.SelectAll();
        });
    }
    private async void RenameBox_KeyDown(object sender, KeyEventArgs e)
    {
        var box = (TextBox)sender; var summary = (ConversationSummary)box.DataContext;
        if (e.Key == Key.Escape) { summary.Title = box.Tag as string ?? summary.Title; summary.Renaming = false; e.Handled = true; return; }
        if (e.Key != Key.Enter) return;
        e.Handled = true; await CommitHistoryRename(box, summary);
    }
    private async void RenameBox_LostFocus(object sender, RoutedEventArgs e)
    {
        var box = (TextBox)sender; var summary = (ConversationSummary)box.DataContext;
        if (summary.Renaming) await CommitHistoryRename(box, summary);
    }
    private async Task CommitHistoryRename(TextBox box, ConversationSummary summary)
    {
        summary.Renaming = false;
        if (string.IsNullOrWhiteSpace(summary.Title)) summary.Title = box.Tag as string ?? summary.Title;
        await Run(() => VM.RenameAsync(summary));
    }
    private async void DeleteHistory_Down(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (!VM.Idle) return;
        var summary = (ConversationSummary)((FrameworkElement)sender).DataContext;
        if (!await ConfirmAsync("删除对话", $"删除「{summary.Title}」及其本地备份？此操作无法撤销。")) return;
        await Run(() => VM.DeleteAsync(summary.Id));
    }
    private async void DeleteChat_Click(object sender, RoutedEventArgs e)
    {
        var id = VM.Current.Id;
        if (VM.Idle && await ConfirmAsync("删除对话", "删除当前对话及其本地备份？此操作无法撤销。")) await Run(() => VM.DeleteAsync(id));
    }
    private async void ExportChat_Click(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        var paths = await FileDialogs.PickAsync(this, "导出对话", new[] { ".md" }, save: true, suggested: "Reflection-chat.md");
        if (paths is not null)
        {
            var text = new StringBuilder("# " + VM.Current.Title + "\n\n");
            foreach (var message in VM.Current.Messages) text.AppendLine("## " + message.Author).AppendLine().AppendLine(message.Content).AppendLine();
            await File.WriteAllTextAsync(paths[0], text.ToString()); VM.Status = "对话已导出";
        }
    });
    private async void Attach_Click(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        var paths = await FileDialogs.PickAsync(this, "添加文本附件", new[] { ".txt", ".md", ".json", ".cs", ".py", ".js", ".ts", ".csv", ".log", ".yaml", ".yml" });
        if (paths is null) return;
        if (new FileInfo(paths[0]).Length > 100_000) throw new ArgumentException("请选择小于 100 KB 的文本文件。");
        VM.Draft += $"\n\n文件：{Path.GetFileName(paths[0])}\n```\n{await File.ReadAllTextAsync(paths[0])}\n```\n";
        VM.Status = "文本已加入输入框，可编辑后发送";
    });
    private async void AddImage_Click(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        if (!VM.Idle || savingClose || switchingWorkspace) return;
        if (VM.AvailableImageSlots == 0) { VM.Status = "一次最多 4 张图片"; return; }
        var paths = await FileDialogs.PickAsync(this, "添加图片", new[] { ".png", ".jpg", ".jpeg", ".webp", ".bmp" }, multiple: true);
        if (paths is not null) await ReceiveComposerDrop(new DataObject(DataFormats.FileDrop, paths));
    });
    private static bool IsInside(FrameworkElement target, DragEventArgs e)
    {
        if (!target.IsVisible) return false;
        var point = e.GetPosition(target);
        return point.X >= 0 && point.Y >= 0 && point.X <= target.ActualWidth && point.Y <= target.ActualHeight;
    }
    private void Window_DragOver(object sender, DragEventArgs e)
    {
        if (OverlayOpen) { e.Effects = DragDropEffects.None; e.Handled = true; return; }
        if (currentPage == "reader" && e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            var valid = ((string[])e.Data.GetData(DataFormats.FileDrop)).Any(p => Directory.Exists(p) || ReaderDocuments.Supported(p));
            ReaderPage.SetDropHint(valid); e.Effects = valid ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; return;
        }
        var composer = currentPage == "chat" && IsInside(ComposerPanel, e);
        var editor = currentPage == "editor" && !savingCharacter;
        var image = VM.Idle && (composer || editor) && (!composer || VM.AvailableImageSlots > 0) && ImageDrop.CanRead(e.Data);
        ComposerDropHint.Visibility = image && composer ? Visibility.Visible : Visibility.Collapsed;
        CharacterEditor.SetDropHighlight(image && editor);
        if (!composer && !editor) return;
        // Text drag remains native; file/bitmap drags are owned by the image target.
        if (!e.Data.GetDataPresent(DataFormats.FileDrop) && !e.Data.GetDataPresent(DataFormats.Bitmap)) return;
        e.Effects = image ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }
    private void Window_DragLeave(object sender, DragEventArgs e) { ReaderPage.SetDropHint(false); ComposerDropHint.Visibility = Visibility.Collapsed; CharacterEditor.SetDropHighlight(false); }
    private async void Window_Drop(object sender, DragEventArgs e)
    {
        if (OverlayOpen) { e.Handled = true; return; }
        ComposerDropHint.Visibility = Visibility.Collapsed; CharacterEditor.SetDropHighlight(false); ReaderPage.SetDropHint(false);
        if (currentPage == "reader" && e.Data.GetDataPresent(DataFormats.FileDrop))
        { e.Handled = true; await ReaderPage.AddPathsAsync((string[])e.Data.GetData(DataFormats.FileDrop)); return; }
        if (!e.Data.GetDataPresent(DataFormats.FileDrop) && !e.Data.GetDataPresent(DataFormats.Bitmap)) return;
        if (currentPage == "editor" && !savingCharacter && VM.Idle)
        {
            e.Handled = true; await CharacterEditor.ReceiveImageDropAsync(e.Data);
        }
        else if (currentPage == "chat" && IsInside(ComposerPanel, e))
        {
            e.Handled = true; await ReceiveComposerDrop(e.Data);
        }
    }
    internal Task<bool> ReceiveComposerDrop(IDataObject data)
    {
        if (!VM.Idle || savingClose || switchingWorkspace) return Task.FromResult(false);
        if (VM.AvailableImageSlots == 0) { VM.Status = "一次最多 4 张图片"; return Task.FromResult(false); }
        try
        {
            var inputs = ImageDrop.Capture(data, VM.AvailableImageSlots);
            return inputs.Count == 0 ? Task.FromResult(false) : VM.ImportImagesAsync(inputs);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException or InvalidOperationException)
        { VM.Status = ex.Message; return Task.FromResult(false); }
    }
    private void RemoveAttachment_Click(object sender, RoutedEventArgs e) { if (((FrameworkElement)sender).DataContext is ChatImage image) VM.RemoveAttachment(image); }
    private async void ReuseImage_Click(object sender, RoutedEventArgs e)
    {
        if (savingClose || switchingWorkspace || !VM.Idle || ((FrameworkElement)sender).DataContext is not ChatImage image) return;
        if (VM.AvailableImageSlots == 0) { VM.Status = "一次最多 4 张图片"; return; }
        var owner = VM; var chat = VM.Current;
        await Run(async () =>
        {
            // Old embedded images are normalized through the same asset pipeline.
            var normalized = await owner.Images.NormalizeAsync(image.Snapshot(), CancellationToken.None);
            if (!ReferenceEquals(VM, owner) || !ReferenceEquals(owner.Current, chat) || savingClose || switchingWorkspace) return;
            if (owner.AvailableImageSlots > 0) owner.AddAttachment(normalized.Snapshot());
        });
    }
    private async void ZoomImage_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not ChatImage image) return;
        var owner = VM; var page = currentPage;
        await Run(async () =>
        {
            var source = await owner.Images.DisplayAsync(image.Snapshot(), 2560, true, CancellationToken.None);
            if (ReferenceEquals(VM, owner) && currentPage == page && !savingClose && !switchingWorkspace) await ShowImageAsync(source);
        });
    }
    private async void Reason_Click(object sender, RoutedEventArgs e) { ReasonToggle.IsChecked = false; await Run(() => VM.SetReasonAsync((string)((FrameworkElement)sender).Tag)); }
    private void OpenCard_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not CharacterCard card) return;
        VM.DetailCard = card; VM.Status = ""; CharacterDetailPage.ScrollToTop(); ShowPage("detail");
    }
    private void CreateCard_Click(object sender, RoutedEventArgs e) { if (VM.Idle && !savingCharacter) BeginCharacterEdit(null); }
    private void EditDetail_Click(object sender, RoutedEventArgs e) { if (VM.Idle && !savingCharacter && VM.DetailCard is not null) BeginCharacterEdit(VM.DetailCard); }
    private void BeginCharacterEdit(CharacterCard? original)
    {
        VM.Status = ""; CharacterEditor.BeginEdit(original); ShowPage("editor");
        Dispatcher.BeginInvoke(() => CharacterEditor.NameField.Focus());
    }
    private void CancelCharacter_Requested(object? sender, EventArgs e)
    {
        if (savingCharacter) return;
        CharacterEditor.CancelImageWork();
        if (CharacterEditor.Original is { } original) { VM.DetailCard = original; ShowPage("detail"); }
        else ShowPage("cards");
    }
    private async void SaveCharacter_Requested(object? sender, EventArgs e) => await SaveCharacterAsync();
    private async Task<bool> SaveCharacterAsync()
    {
        if (savingCharacter || switchingWorkspace || savingClose || !VM.Idle || !CharacterEditor.TryBuildResult() || CharacterEditor.Result is not { } edited) return false;
        BeginCharacterSave(); CharacterEditor.IsEnabled = false;
        try
        {
            var original = CharacterEditor.Original;
            await CommitCharacterLibraryAsync(original, edited);
            VM.DetailCard = edited; VM.Status = "角色已保存";
            if (original is not null)
            {
                try { await VM.ApplyCharacterUpdateAsync(edited); }
                catch (Exception ex) { VM.Status = "角色已保存，但部分会话同步失败：" + ex.Message + "。可重新编辑保存以重试同步。"; }
            }
            ShowPage("detail"); CharacterDetailPage.ScrollToTop(); return true;
        }
        catch (Exception ex) { CharacterEditor.ErrorText.Text = "保存失败：" + ex.Message; return false; }
        finally { EndCharacterSave(); CharacterEditor.IsEnabled = true; }
    }
    private async void UseCard_Click(object sender, RoutedEventArgs e)
    {
        if (!VM.Idle) return;
        var card = (sender as FrameworkElement)?.DataContext as CharacterCard ?? VM.DetailCard;
        if (card is null) return; await Run(() => VM.NewChatAsync(card)); followOutput = true; ShowPage("chat"); Composer.Focus();
    }
    private async void DeleteDetail_Click(object sender, RoutedEventArgs e)
    {
        if (VM.Busy || savingCharacter || VM.DetailCard is not CharacterCard card) return;
        if (!await ConfirmAsync("删除角色", $"删除「{card.Name}」？已有对话和角色快照会保留。")) return;
        if (savingCharacter || switchingWorkspace || savingClose) return;
        BeginCharacterSave();
        try { await Run(async () => { await CommitCharacterLibraryAsync(card, null); ShowPage("cards"); }); }
        finally { EndCharacterSave(); }
    }
    private async void ImportCard_Click(object sender, RoutedEventArgs e)
    {
        if (VM.Busy || savingCharacter || switchingWorkspace || savingClose) return;
        BeginCharacterSave();
        var ct = characterSaveCancellation!.Token;
        try
        {
            await Run(async () =>
            {
                var paths = await FileDialogs.PickAsync(this, "导入角色卡", new[] { ".json" }); if (paths is null) return;
                ct.ThrowIfCancellationRequested();
                if (new FileInfo(paths[0]).Length > 8_000_000) throw new ArgumentException("角色卡不能超过 8 MB。");
                var json = await File.ReadAllTextAsync(paths[0], ct);
                var card = await Task.Run(() => CharacterExchange.Import(json), ct);
                if (card.Image is not null)
                {
                    var original = await VM.Images.NormalizeAsync(card.Image, ct);
                    var cropped = await CharacterImageCropper.ShowAsync(this, original, ct);
                    if (cropped is null) return;
                    card.Image = cropped;
                }
                ct.ThrowIfCancellationRequested();
                await CommitCharacterLibraryAsync(null, card);
                VM.DetailCard = card; VM.Status = "角色卡已导入"; ShowPage("detail");
            });
        }
        finally { EndCharacterSave(); }
    }
    private async void ExportDetail_Click(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        if (VM.DetailCard is not CharacterCard card) return;
        var paths = await FileDialogs.PickAsync(this, "导出角色卡", new[] { ".json" }, save: true, suggested: "character.json");
        if (paths is not null)
        {
            await File.WriteAllTextAsync(paths[0], await CharacterExchange.ExportAsync(card)); VM.Status = "角色卡已导出";
        }
    });
    private void RefreshSettings()
    {
        ConfiguredServices.ItemsSource = VM.Settings.Services.ToList();
        AutoDiscover.IsChecked = VM.Settings.AutoDiscover;
        SystemPrompt.Text = VM.Settings.SystemPrompt ?? "";
        ContextBudget.Text = VM.Settings.ContextCharacters.ToString(CultureInfo.InvariantCulture);
        ParamHost.Children.Clear(); paramBoxes.Clear();
        var info = VM.SelectedModel?.Info;
        refreshingSettings = true;
        foreach (var field in Prompting.Fields)
        {
            var shown = Prompting.ShownDefault(field, info);
            var stored = VM.Settings.ParamDefaults.TryGetValue(field.Key, out var value) ? value : "";
            var box = new TextBox { MinHeight = 44, Text = stored.Length > 0 ? stored : shown, Tag = stored.Length > 0 ? "custom" : "default", Margin = new Thickness(0, 8, 0, 0) };
            if (stored.Length == 0) box.Foreground = (Brush)FindResource("Muted");
            box.TextChanged += (_, _) => { if (refreshingSettings) return; box.Tag = "custom"; box.ClearValue(ForegroundProperty); };
            paramBoxes[field.Key] = box;
            ParamHost.Children.Add(new TextBlock { Text = field.Label, FontSize = 12, Foreground = (Brush)FindResource("Muted"), Margin = new Thickness(0, 14, 0, 0) });
            ParamHost.Children.Add(box);
            ParamHost.Children.Add(new TextBlock { Text = field.Hint, FontSize = 12, Foreground = (Brush)FindResource("Muted"), Margin = new Thickness(0, 4, 0, 0) });
        }
        refreshingSettings = false;
    }
    private void UpdateParamDefaults()
    {
        if (SettingsPage.Visibility != Visibility.Visible) return;
        var info = VM.SelectedModel?.Info;
        refreshingSettings = true;
        foreach (var field in Prompting.Fields)
        {
            if (!paramBoxes.TryGetValue(field.Key, out var box) || box.Tag as string != "default") continue;
            box.Text = Prompting.ShownDefault(field, info); box.Tag = "default"; box.Foreground = (Brush)FindResource("Muted");
        }
        refreshingSettings = false;
    }
    private async void Discover_Click(object sender, RoutedEventArgs e) => await Run(VM.DiscoverAsync);
    private async void AddService_Click(object sender, RoutedEventArgs e) { if (VM.Idle) await EditService(null); }
    private async void ConnectService_Click(object sender, RoutedEventArgs e)
    {
        if (!VM.Idle) return;
        ShowPage("settings"); RefreshSettings(); await EditService(null);
    }
    private async void EditService_Click(object sender, RoutedEventArgs e) { if (VM.Idle) await EditService((ServiceConfig)((FrameworkElement)sender).DataContext); }
    private async Task EditService(ServiceConfig? original) => await Run(async () =>
    {
        var service = await EditorDialog.ServiceAsync(this, original); if (service is null) return;
        if (original is not null) VM.Settings.Services[VM.Settings.Services.IndexOf(original)] = service; else VM.Settings.Services.Add(service);
        RefreshSettings(); await VM.SaveSettingsAsync(); VM.Status = "服务配置已保存";
    });
    private async void DeleteService_Click(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        if (VM.Busy) return;
        var service = (ServiceConfig)((FrameworkElement)sender).DataContext;
        if (!await ConfirmAsync("移除服务", $"移除连接「{service.Name}」？不会停止本机推理服务。", "移除")) return;
        VM.Settings.Services.Remove(service); RefreshSettings(); await VM.SaveSettingsAsync();
    });
    private async void SavePreferences_Click(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        if (VM.Busy) { VM.Status = "请等待当前回复完成后保存设置。"; return; }
        var prompt = SystemPrompt.Text.Trim();
        if (prompt.Length > 20000) throw new ArgumentException("系统提示词不能超过 20000 字。");
        if (!int.TryParse(ContextBudget.Text, out var budget) || budget < 1000 || budget > 200000) throw new ArgumentException("上下文预算需为 1000–200000 字符。");
        var info = VM.SelectedModel?.Info;
        var defaults = new Dictionary<string, string>();
        foreach (var field in Prompting.Fields)
        {
            var text = paramBoxes[field.Key].Text.Trim();
            var shown = Prompting.ShownDefault(field, info);
            if (text.Length == 0 || (shown.Length > 0 && Prompting.Same(field, text, shown))) continue;
            if (field.Lines)
            {
                var lines = text.Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0).Take(16).ToArray();
                if (lines.Length > 0) defaults[field.Key] = string.Join('\n', lines);
                continue;
            }
            if (!Prompting.TryNumber(field, text, out var number)) throw new ArgumentException($"{field.Label}需为 {field.Min.ToString(CultureInfo.InvariantCulture)}–{field.Max.ToString(CultureInfo.InvariantCulture)} 之间的数值。");
            defaults[field.Key] = Prompting.Format(field, number);
        }
        VM.Settings.ParamDefaults = defaults;
        VM.Settings.SystemPrompt = prompt;
        VM.Settings.ContextCharacters = budget;
        VM.Settings.AutoDiscover = AutoDiscover.IsChecked == true;
        var temperature = Prompting.Fields.First(x => x.Key == "temperature");
        var predict = Prompting.Fields.First(x => x.Key == "num_predict");
        if (defaults.TryGetValue("temperature", out var customTemperature) && Prompting.TryNumber(temperature, customTemperature, out var temperatureValue)) VM.Settings.Temperature = temperatureValue;
        else if (Prompting.TryNumber(temperature, Prompting.ShownDefault(temperature, info), out var shownTemperature)) VM.Settings.Temperature = shownTemperature;
        if (defaults.TryGetValue("num_predict", out var customPredict) && Prompting.TryNumber(predict, customPredict, out var predictValue)) VM.Settings.MaxOutputTokens = (int)predictValue;
        else if (Prompting.TryNumber(predict, Prompting.ShownDefault(predict, info), out var shownPredict)) VM.Settings.MaxOutputTokens = (int)shownPredict;
        await VM.SaveSettingsAsync(); VM.Status = "偏好已保存";
    });
    private async void OpenData_Click(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        Process.Start(new ProcessStartInfo(VM.Store.Root) { UseShellExecute = true });
        await Task.CompletedTask;
    });
    internal async Task WaitInitializedAsync() { for (var i = 0; i < 200 && !initialized; i++) await Task.Delay(100); if (!initialized) throw new TimeoutException(VM.Status); }
    private static T? FindChild<T>(DependencyObject? parent) where T : DependencyObject
    {
        if (parent is null) return null;
        var count = VisualTreeHelper.GetChildrenCount(parent);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) return match;
            var nested = FindChild<T>(child);
            if (nested is not null) return nested;
        }
        return null;
    }
}
