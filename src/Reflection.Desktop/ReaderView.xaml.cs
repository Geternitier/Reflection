using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Reflection.Core;

namespace Reflection.Desktop;

public sealed class ReadingNode(string path, bool directory, bool placeholder = false) : Observable
{
    public string Path { get; } = path;
    public bool Directory { get; } = directory;
    public bool Loaded { get; set; }
    public bool Placeholder { get; } = placeholder;
    public string Label => Placeholder ? Path : (System.IO.Path.GetFileName(Path.TrimEnd('\\', '/')) is { Length: > 0 } name ? name : Path);
    public ObservableCollection<ReadingNode> Children { get; } = directory && !placeholder ? [new("正在读取…", false, true)] : [];
}

public partial class ReaderView : UserControl, INotifyPropertyChanged
{
    private ReaderStore store = new(LocalStore.ResolveRoot());
    internal void ConfigureRoot(string root)
    {
        if (initialization is not null) throw new InvalidOperationException("Reading workspace is already initialized");
        store = new(root);
    }
    private ReaderState state = new();
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource? opening;
    private Task? initialization;
    private ReadingDocument? document;
    private bool settingsReady, restoring, outlineTracking, libraryOpen = true, focus, suspended;
    private bool? outlineOverride;
    private bool retired;
    private bool typographyOpen;
    private readonly DispatcherTimer positionTimer = new() { Interval = TimeSpan.FromMilliseconds(180) };
    private readonly DispatcherTimer typographyTimer = new() { Interval = TimeSpan.FromMilliseconds(120) };
    private readonly DispatcherTimer progressTimer = new() { Interval = TimeSpan.FromMilliseconds(75) };
    private readonly DispatcherTimer filterTimer = new() { Interval = TimeSpan.FromMilliseconds(160) };
    private readonly Dictionary<string, ReadingNode> knownFiles = new(StringComparer.OrdinalIgnoreCase);
    private Task saving = Task.CompletedTask;
    private string status = "", progressLabel = "";
    public ObservableCollection<ReadingNode> Sources { get; } = [];
    public IReadOnlyList<ReadingPiece> Pieces => document?.Pieces ?? [];
    public IReadOnlyList<ReadingHeading> Outline => document?.Outline ?? [];
    public string DocumentName => document is null ? "" : Path.GetFileName(document.Path);
    public double FontSizeValue => state.FontSize;
    public double LeadingValue => state.LineHeight;
    public double WidthValue => state.Width;
    public bool SerifValue => state.Serif;
    public string Status { get => status; private set { status = value; Notify(); } }
    public string ProgressLabel { get => progressLabel; private set { if (progressLabel == value) return; progressLabel = value; Notify(); } }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
    internal ReadingDocument? CurrentDocument => document;
    internal ListBox DocumentList => Pages;
    internal FrameworkElement HeaderToolbar => ReaderToolbar;
    internal FrameworkElement TakeHeader()
    {
        ReaderShell.Children.Remove(ReaderToolbar);
        ReaderToolbar.DataContext = this;
        ReaderToolbar.PreviewKeyDown += KeyPressed;
        return ReaderToolbar;
    }

    public ReaderView()
    {
        InitializeComponent(); DataContext = this;
        positionTimer.Tick += async (_, _) => { positionTimer.Stop(); if (!restoring && !suspended) { RememberPosition(); await SaveQuietlyAsync(); } };
        typographyTimer.Tick += async (_, _) => { typographyTimer.Stop(); await ApplyTypographyAsync(); };
        filterTimer.Tick += async (_, _) => { filterTimer.Stop(); await FilterNamesAsync(); };
        progressTimer.Tick += (_, _) => { progressTimer.Stop(); if (!retired && !restoring && IsVisible) UpdateProgress(Position()); };
        SizeChanged += (_, _) => UpdatePanels();
        IsVisibleChanged += async (_, e) =>
        {
            if (retired) return;
            if ((bool)e.NewValue) { suspended = false; await InitializeAsync(); UpdatePanels(); }
            else { RememberPosition(); suspended = true; positionTimer.Stop(); typographyTimer.Stop(); filterTimer.Stop(); progressTimer.Stop(); _ = SaveQuietlyAsync(); }
        };
        PreviewKeyDown += KeyPressed;
    }
    internal Task InitializeAsync() => initialization ??= InitializeCoreAsync();
    private async Task InitializeCoreAsync()
    {
        try
        {
            state = await store.LoadAsync(); focus = state.FocusMode;
            foreach (var path in state.Sources) { var node = new ReadingNode(path, !ReaderDocuments.Supported(path)); Sources.Add(node); if (!node.Directory) knownFiles[path] = node; }
            SizeSlider.Value = state.FontSize; LeadingSlider.Value = state.LineHeight; WidthSlider.Value = state.Width;
            SerifCheck.IsChecked = state.Serif; SnapshotCheck.IsChecked = state.KeepSnapshot; settingsReady = true;
            NotifyTypography(); UpdatePanels();
            if (state.LastPath is { } last) await OpenDocumentAsync(last, fallback: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { Status = "读取阅读设置失败：" + ex.Message; }
    }
    internal async Task AddPathsAsync(IEnumerable<string> paths)
    {
        await InitializeAsync(); string? firstFile = null;
        foreach (var raw in paths.Take(64))
        {
            try
            {
                var path = Path.GetFullPath(raw); var folder = Directory.Exists(path);
                if (!folder && (!ReaderDocuments.Supported(path) || !File.Exists(path))) continue;
                if (!state.Sources.Contains(path, StringComparer.OrdinalIgnoreCase))
                {
                    if (Sources.Count >= 64) { Status = "最多保留 64 个来源，请先移除不用的来源。"; break; }
                    state.Sources.Add(path); var node = new ReadingNode(path, folder); Sources.Add(node);
                    if (folder) { await LoadNodeAsync(node); SourceTree.UpdateLayout(); if (SourceTree.ItemContainerGenerator.ContainerFromItem(node) is TreeViewItem item) item.IsExpanded = true; }
                    else knownFiles[path] = node;
                }
                if (!folder) firstFile ??= path;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { Status = "无法添加来源：" + ex.Message; }
        }
        if (firstFile is not null) await OpenDocumentAsync(firstFile);
        else if (Sources.Count > 0) { libraryOpen = true; focus = false; UpdatePanels(); Status = ""; }
        await SaveQuietlyAsync();
    }
    private static readonly HashSet<string> IgnoredFolders = new(StringComparer.OrdinalIgnoreCase) { ".git", "node_modules", ".svn", ".hg", "bin", "obj", "dist", "target" };
    private async Task LoadNodeAsync(ReadingNode node, bool refresh = false)
    {
        if (!node.Directory || node.Placeholder || node.Loaded && !refresh) return;
        node.Loaded = true;
        try
        {
            var entries = await Task.Run(() =>
            {
                var result = new List<(string Path, bool Directory)>(); var truncated = false;
                foreach (var info in new DirectoryInfo(node.Path).EnumerateFileSystemInfos())
                {
                    lifetime.Token.ThrowIfCancellationRequested();
                    if (info.Name.StartsWith('.') || (info.Attributes & (FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint)) != 0) continue;
                    var dir = (info.Attributes & FileAttributes.Directory) != 0;
                    if (dir ? IgnoredFolders.Contains(info.Name) : !ReaderDocuments.Supported(info.Name)) continue;
                    if (result.Count >= 2000) { truncated = true; break; }
                    result.Add((info.FullName, dir));
                }
                return (Nodes: result.OrderByDescending(x => x.Directory).ThenBy(x => x.Path, StringComparer.CurrentCultureIgnoreCase).ToArray(), Truncated: truncated);
            }, lifetime.Token);
            if (refresh) foreach (var key in knownFiles.Keys.Where(p => p.StartsWith(node.Path.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)).ToArray()) knownFiles.Remove(key);
            node.Children.Clear(); foreach (var entry in entries.Nodes) { var child = new ReadingNode(entry.Path, entry.Directory); node.Children.Add(child); if (!child.Directory) knownFiles[child.Path] = child; }
            if (entries.Truncated) node.Children.Add(new("仅显示前 2,000 项，请添加子目录来源", false, true));
            if (node.Children.Count == 0) node.Children.Add(new("此目录没有支持的文档", false, true));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { node.Loaded = false; node.Children.Clear(); node.Children.Add(new("无法读取，点击刷新重试", false, true)); Status = ex.Message; }
    }
    internal async Task OpenDocumentAsync(string path, bool fallback = false)
    {
        RememberPosition(); opening?.Cancel();
        var request = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); opening = request;
        Status = "正在读取 " + Path.GetFileName(path) + "…";
        try
        {
            ReadingDocument loaded; var snapshot = false;
            try { loaded = await ReaderDocuments.OpenAsync(path, request.Token); }
            catch (Exception ex) when (fallback && ex is IOException or UnauthorizedAccessException)
            {
                var cached = state.KeepSnapshot ? await store.LoadSnapshotAsync() : null;
                if (cached is null || !string.Equals(cached.Path, path, StringComparison.OrdinalIgnoreCase)) throw;
                loaded = await Task.Run(() => ReaderDocuments.Parse(cached.Path, cached.Text, cached.Encoding, request.Token), request.Token); snapshot = true;
            }
            if (request.IsCancellationRequested || opening != request) return;
            SmoothScroll.Stop(Pages); restoring = true; positionTimer.Stop();
            document = loaded; state.LastPath = loaded.Path;
            Notify(nameof(Pieces)); Notify(nameof(Outline)); Notify(nameof(DocumentName));
            Welcome.Visibility = Visibility.Collapsed; SnapshotBanner.Visibility = snapshot ? Visibility.Visible : Visibility.Collapsed;
            UpdatePanels();
            await RestorePositionAsync(state.Positions.GetValueOrDefault(path) ?? new());
            if (opening != request) return;
            Status = $"{loaded.Encoding} · {loaded.Text.Length:N0} 字符" + (snapshot ? " · 本地快照" : "");
            if (!snapshot && state.KeepSnapshot) await store.SaveSnapshotAsync(loaded);
            await SaveQuietlyAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { if (opening == request) Status = "无法打开文档：" + ex.Message; }
        finally { if (opening == request) { opening = null; restoring = false; } request.Dispose(); }
    }
    private IEnumerable<(ListBoxItem Container, ReadingPiece Piece, double Top)> Realized()
    {
        if (SmoothScroll.Viewer(Pages) is not { } viewer) yield break;
        var panel = FindPanel(Pages);
        if (panel is null) yield break;
        foreach (var child in panel.Children.OfType<ListBoxItem>())
            if (child.DataContext is ReadingPiece piece && child.ActualHeight > 0)
                yield return (child, piece, child.TransformToAncestor(viewer).Transform(new Point()).Y);
    }
    private static VirtualizingStackPanel? FindPanel(DependencyObject node)
    {
        if (node is VirtualizingStackPanel panel) return panel;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) if (FindPanel(VisualTreeHelper.GetChild(node, i)) is { } child) return child;
        return null;
    }
    private ReadingPosition Position() => RealizedPosition() ?? new();
    private ReadingPosition? RealizedPosition()
    {
        var visible = Realized().Where(x => x.Top + x.Container.ActualHeight > 1).OrderBy(x => x.Top).FirstOrDefault();
        return visible.Piece is null ? null : new(visible.Piece.Offset, Math.Clamp(-visible.Top / visible.Container.ActualHeight, 0, 1));
    }
    internal void RememberPosition()
    {
        if (document is null || restoring || suspended || RealizedPosition() is not { } position) return;
        state.Positions[document.Path] = position;
        while (state.Positions.Count > 100) state.Positions.Remove(state.Positions.Keys.First());
    }
    private async Task RestorePositionAsync(ReadingPosition position)
    {
        if (document is null) return;
        var current = document;
        var index = 0;
        for (var i = 0; i < current.Pieces.Count; i++) { if (current.Pieces[i].Offset > position.Offset) break; index = i; }
        var piece = current.Pieces[index];
        Pages.ScrollIntoView(piece);
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        if (document != current || lifetime.IsCancellationRequested) return;
        Pages.UpdateLayout();
        if (Pages.ItemContainerGenerator.ContainerFromItem(piece) is ListBoxItem container && SmoothScroll.Viewer(Pages) is { } viewer)
        {
            var top = container.TransformToAncestor(viewer).Transform(new Point()).Y;
            viewer.ScrollToVerticalOffset(viewer.VerticalOffset + top + Math.Clamp(double.IsFinite(position.Fraction) ? position.Fraction : 0, 0, 1) * container.ActualHeight);
        }
        UpdateProgress(position);
    }
    private void UpdateProgress(ReadingPosition position)
    {
        if (document is null) return;
        var piece = document.Pieces.LastOrDefault(x => x.Offset <= position.Offset);
        var character = position.Offset + (piece?.Length ?? 0) * position.Fraction;
        ProgressLabel = $"{Math.Clamp(character / Math.Max(1, document.Text.Length) * 100, 0, 100):0}%";
        var heading = document.Outline.LastOrDefault(h => document.Pieces[h.Piece].Offset <= position.Offset);
        outlineTracking = true; OutlineList.SelectedItem = heading; outlineTracking = false;
    }
    private void Pages_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (restoring || suspended || document is null || e.VerticalChange == 0 && e.ExtentHeightChange == 0) return;
        if (!progressTimer.IsEnabled) progressTimer.Start(); positionTimer.Stop(); positionTimer.Start();
    }
    private async Task SaveQuietlyAsync()
    {
        if (!settingsReady) return;
        try { saving = store.SaveAsync(state); await saving; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Status = "阅读设置保存失败：" + ex.Message; }
    }
    internal async Task PersistAsync() { RememberPosition(); if (settingsReady) { saving = store.SaveAsync(state); await saving; } }
    internal void Retire() { retired = true; positionTimer.Stop(); typographyTimer.Stop(); filterTimer.Stop(); progressTimer.Stop(); lifetime.Cancel(); opening?.Cancel(); }
    private MainWindow Owner => (MainWindow)Window.GetWindow(this);
    private async void Open_Click(object sender, RoutedEventArgs e)
    {
        var files = await FileDialogs.PickAsync(Owner, "打开阅读文件", [".md", ".markdown", ".txt"], multiple: true);
        if (files is not null) await AddPathsAsync(files);
    }
    private async void Folder_Click(object sender, RoutedEventArgs e)
    {
        var paths = await FileDialogs.PickAsync(Owner, "添加阅读文件夹", [], directory: true);
        if (paths is not null) await AddPathsAsync(paths);
    }
    private async void Source_Expanded(object sender, RoutedEventArgs e) { if (e.OriginalSource is TreeViewItem { DataContext: ReadingNode node }) await LoadNodeAsync(node); }
    private async void Source_Selected(object sender, RoutedPropertyChangedEventArgs<object> e) { if (e.NewValue is ReadingNode { Directory: false, Placeholder: false } node) await OpenDocumentAsync(node.Path); }
    private async void Outline_Selected(object sender, SelectionChangedEventArgs e)
    {
        if (outlineTracking || document is null || OutlineList.SelectedItem is not ReadingHeading heading) return;
        SmoothScroll.Stop(Pages); restoring = true;
        try { await RestorePositionAsync(new(document.Pieces[heading.Piece].Offset)); }
        finally { restoring = false; }
        RememberPosition(); await SaveQuietlyAsync();
    }
    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        var selected = SourceTree.SelectedItem as ReadingNode;
        if (selected is { Directory: true }) await LoadNodeAsync(selected, true);
        else foreach (var node in Sources.Where(n => n.Directory)) await LoadNodeAsync(node, true);
        if (document is not null) await OpenDocumentAsync(document.Path);
    }
    private async void Reload_Click(object sender, RoutedEventArgs e) { if (document is not null) await OpenDocumentAsync(document.Path); }
    private async void Remove_Click(object sender, RoutedEventArgs e)
    {
        if ((NameMatches.IsVisible ? NameMatches.SelectedItem : SourceTree.SelectedItem) is not ReadingNode selected) { return; }
        var root = Sources.FirstOrDefault(n => n == selected || n.Directory && selected.Path.StartsWith(n.Path.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase));
        if (root is null) return;
        Sources.Remove(root); state.Sources.Remove(root.Path);
        foreach (var key in knownFiles.Keys.Where(p => p.Equals(root.Path, StringComparison.OrdinalIgnoreCase) || p.StartsWith(root.Path.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)).ToArray()) knownFiles.Remove(key);
        await FilterNamesAsync(); await SaveQuietlyAsync();
    }
    private void Settings_Click(object sender, RoutedEventArgs e) => ShowInspector(true, true);
    private void Library_Click(object sender, RoutedEventArgs e) { libraryOpen = !libraryOpen; focus = false; if (libraryOpen && ActualWidth < 1000) outlineOverride = false; UpdatePanels(); }
    private void Outline_Click(object sender, RoutedEventArgs e) => ShowInspector(false, true);
    private void InspectorOutline_Click(object sender, RoutedEventArgs e) => ShowInspector(false, false);
    private void InspectorTypography_Click(object sender, RoutedEventArgs e) => ShowInspector(true, false);
    private void InspectorClose_Click(object sender, RoutedEventArgs e) { outlineOverride = false; UpdatePanels(); }
    private void ShowInspector(bool typography, bool toggle)
    {
        var close = toggle && InspectorPanel.IsVisible && typographyOpen == typography;
        typographyOpen = typography; outlineOverride = !close; focus = false; UpdatePanels();
    }
    private void ToggleFocusMode() { focus = !focus; state.FocusMode = focus; UpdatePanels(); _ = SaveQuietlyAsync(); }
    private void UpdatePanels()
    {
        var showLibrary = libraryOpen && !focus;
        var showOutline = !focus && (typographyOpen || Outline.Count > 0 || outlineOverride == true) && (outlineOverride ?? ActualWidth >= (showLibrary ? 1000 : 760));
        if (showOutline && ActualWidth < 1000) showLibrary = false;
        LibraryColumn.Width = new GridLength(showLibrary ? Math.Min(260, Math.Max(190, ActualWidth * .28)) : 0);
        OutlineColumn.Width = new GridLength(showOutline ? 280 : 0);
        LibraryPanel.Visibility = showLibrary ? Visibility.Visible : Visibility.Collapsed;
        InspectorPanel.Visibility = showOutline ? Visibility.Visible : Visibility.Collapsed;
        OutlinePanel.Visibility = showOutline && !typographyOpen ? Visibility.Visible : Visibility.Collapsed;
        SettingsPanel.Visibility = showOutline && typographyOpen ? Visibility.Visible : Visibility.Collapsed;
    }
    private void Typography_Changed(object sender, RoutedPropertyChangedEventArgs<double> e) { if (settingsReady) { typographyTimer.Stop(); typographyTimer.Start(); } }
    private void Typography_Click(object sender, RoutedEventArgs e) { if (settingsReady) { typographyTimer.Stop(); typographyTimer.Start(); } }
    private void NotifyTypography() { foreach (var name in new[] { nameof(FontSizeValue), nameof(LeadingValue), nameof(WidthValue), nameof(SerifValue) }) Notify(name); }
    private async Task ApplyTypographyAsync()
    {
        var anchor = Position(); SmoothScroll.Stop(Pages); restoring = true;
        state.FontSize = SizeSlider.Value; state.LineHeight = LeadingSlider.Value; state.Width = WidthSlider.Value; state.Serif = SerifCheck.IsChecked == true;
        NotifyTypography();
        try { await RestorePositionAsync(anchor); } finally { restoring = false; }
        RememberPosition(); await SaveQuietlyAsync();
    }
    private async void Snapshot_Click(object sender, RoutedEventArgs e)
    {
        state.KeepSnapshot = SnapshotCheck.IsChecked == true;
        try { if (!state.KeepSnapshot) await store.ClearSnapshotAsync(); else if (document is not null && !SnapshotBanner.IsVisible) await store.SaveSnapshotAsync(document); await SaveQuietlyAsync(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Status = ex.Message; }
    }
    private async void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (!await Owner.ConfirmAsync("清除阅读记录", "清除保存的阅读位置和最近文档快照，源文件保持原样。", "清除")) return;
        state.Positions.Clear(); state.LastPath = null;
        try { await store.ClearSnapshotAsync(); await SaveQuietlyAsync(); Status = "已清除保存的阅读记录。"; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Status = ex.Message; }
    }
    private void Copy_Click(object sender, RoutedEventArgs e) { if (document is null) return; try { Clipboard.SetText(document.Text); Status = "已复制全文。"; } catch (System.Runtime.InteropServices.ExternalException) { Status = "剪贴板暂不可用，请重试。"; } }
    private async void Link_Requested(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not ReadingText text || document is null) return;
        if (text.RequestedFootnoteOffset is { } offset)
        {
            e.Handled = true;
            await RestorePositionAsync(new(offset, 0));
            return;
        }
        if (text.RequestedLink is not { } link) return;
        try
        {
            if (link.StartsWith('#'))
            {
                await JumpToHeadingAsync(link[1..]);
                return;
            }
            if (Uri.TryCreate(link, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "mailto")
            { Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); return; }
            if (Uri.TryCreate(link, UriKind.Absolute, out uri) && !uri.IsFile) { Status = "此链接类型不受支持。"; return; }
            var path = uri?.IsFile == true ? uri.LocalPath : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(document.Path)!, Uri.UnescapeDataString(link.Split('#')[0])));
            if (!ReaderDocuments.Supported(path)) { Status = "仅打开 Markdown 和 TXT 文档链接。"; return; }
            var fragment = link.IndexOf('#');
            await OpenDocumentAsync(path);
            if (fragment >= 0 && string.Equals(document?.Path, path, StringComparison.OrdinalIgnoreCase))
                await JumpToHeadingAsync(link[(fragment + 1)..]);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or System.ComponentModel.Win32Exception or UriFormatException) { Status = ex.Message; }
    }
    private async Task JumpToHeadingAsync(string fragment)
    {
        if (document is not { } current) return;
        var anchor = ReaderDocuments.HeadingAnchor(Uri.UnescapeDataString(fragment));
        var heading = current.Outline.FirstOrDefault(h => h.Anchor.Equals(anchor, StringComparison.OrdinalIgnoreCase));
        if (heading is not null) await RestorePositionAsync(new(current.Pieces[heading.Piece].Offset, 0));
    }

    private void KeyPressed(object sender, KeyEventArgs e)
    {
        if (e.Handled) return;
        if (e.Key == Key.O && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        { if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) Folder_Click(this, new()); else Open_Click(this, new()); e.Handled = true; }
        else if (e.Key == Key.K && Keyboard.Modifiers == ModifierKeys.Control) { libraryOpen = true; focus = false; outlineOverride = false; UpdatePanels(); NameFilter.Focus(); NameFilter.SelectAll(); e.Handled = true; }
        else if (e.Key == Key.F11) { ToggleFocusMode(); e.Handled = true; }
        else if (e.Key == Key.Escape && InspectorPanel.IsVisible) { InspectorClose_Click(this, new()); e.Handled = true; }
        else if (e.Key == Key.Escape && focus) { ToggleFocusMode(); e.Handled = true; }
        else if (Keyboard.FocusedElement is not System.Windows.Controls.Primitives.TextBoxBase || Keyboard.FocusedElement is ReadingText)
        {
            if (Keyboard.Modifiers is not (ModifierKeys.None or ModifierKeys.Control)) return;
            if (SmoothScroll.Viewer(Pages) is not { } viewer) return;
            double? target = e.Key switch
            {
                Key.PageDown or Key.Space => viewer.VerticalOffset + viewer.ViewportHeight * .9,
                Key.PageUp => viewer.VerticalOffset - viewer.ViewportHeight * .9,
                Key.Home when Keyboard.Modifiers == ModifierKeys.Control => 0,
                Key.End when Keyboard.Modifiers == ModifierKeys.Control => viewer.ScrollableHeight,
                _ => null
            };
            if (target.HasValue) { SmoothScroll.Stop(Pages); viewer.ScrollToVerticalOffset(target.Value); e.Handled = true; }
        }
    }
    internal void SetDropHint(bool visible) => DropHint.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    private void NameFilter_Changed(object sender, TextChangedEventArgs e) { if (!settingsReady) return; filterTimer.Stop(); filterTimer.Start(); }
    private async Task FilterNamesAsync()
    {
        var query = NameFilter.Text.Trim();
        SourceTree.Visibility = query.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        NameMatches.Visibility = query.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (query.Length == 0) return;
        var candidates = knownFiles.Values.ToArray();
        var matches = await Task.Run(() => candidates.Where(n => Path.GetFileName(n.Path).Contains(query, StringComparison.CurrentCultureIgnoreCase)).Take(500).ToArray());
        if (query != NameFilter.Text.Trim() || lifetime.IsCancellationRequested) return;
        NameMatches.ItemsSource = matches;
        Status = matches.Length == 0 ? "无匹配文件" : $"已加载文件名：{matches.Length} 项" + (matches.Length == 500 ? "（最多显示 500 项）" : "");
    }
    private async void NameMatches_Selected(object sender, SelectionChangedEventArgs e) { if (NameMatches.SelectedItem is ReadingNode node) await OpenDocumentAsync(node.Path); }
}
