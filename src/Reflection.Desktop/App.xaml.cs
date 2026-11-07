using System.Diagnostics;
using Reflection.Core;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Reflection.Desktop;

public partial class App : Application
{
    private async void Combo_PreviewMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is not System.Windows.Controls.ComboBox combo || !combo.IsEnabled) return;
        e.Handled = true; await InlineSelection.OpenAsync(combo);
    }
    private async void Combo_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (sender is System.Windows.Controls.ComboBox combo && e.Key is System.Windows.Input.Key.F4 or System.Windows.Input.Key.Space or System.Windows.Input.Key.Enter or System.Windows.Input.Key.Down or System.Windows.Input.Key.Up)
        { e.Handled = true; await InlineSelection.OpenAsync(combo); }
    }
    private FileStream? instanceLock;
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ThemeManager.Apply("light");
        var smoke = e.Args.Contains("--smoke");
        var output = Environment.GetEnvironmentVariable("REFLECTION_SMOKE_DIR") ?? Path.Combine(Environment.CurrentDirectory, "artifacts", "screenshots");
        try
        {
            var data = LocalStore.ResolveRoot();
            Directory.CreateDirectory(data);
            try { instanceLock = new FileStream(Path.Combine(data, ".instance.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) { if (!smoke) await ShowStartupNoticeAsync("应用已在运行", "请切换到已有的 Reflection 窗口。"); Shutdown(2); return; }
            if (smoke && e.Args.Contains("--workspace-only"))
            {
                var seed = new LocalStore(data);
                var chat = new Conversation { Title = "已有历史" }; chat.Messages.Add(new() { Role = "user", Content = "startup fixture" });
                await seed.SaveConversationAsync(chat);
            }
            var startupSettings = await new LocalStore(data).LoadSettingsAsync();
            ThemeManager.Apply(startupSettings.Theme);
            var window = new MainWindow(); MainWindow = window;
            if (smoke) { window.WindowStartupLocation = WindowStartupLocation.Manual; window.Left = -20000; window.Top = -20000; window.ShowInTaskbar = false; window.ShowActivated = false; }
            window.Show();
            if (!smoke) return;
            Directory.CreateDirectory(output);
            await window.WaitInitializedAsync();
            window.VerifyStartupChrome();
            await window.VM.DiscoveryTask;
            var initial = Process.GetCurrentProcess(); initial.Refresh();
            var startupWorkingSet = initial.WorkingSet64 / 1048576.0;
            var startupPrivateBytes = initial.PrivateMemorySize64 / 1048576.0;
            if (e.Args.Contains("--workspace-only"))
            {
                window = await window.VerifyWorkspaceIsolationAsync(output, e.Args.Contains("--slow-discovery")); window.Close(); return;
            }
            if (e.Args.Contains("--reader-only"))
            {
                await window.VerifyReaderAsync(output);
                await File.WriteAllTextAsync(Path.Combine(output, "smoke-result.txt"), "PASS: focused reader and virtual gallery regression");
                window.Close(); return;
            }
            await FormRegression.VerifyAsync(window, output);
            await window.VerifyOverlaysAsync(output);
            await window.VerifyChromeAsync(output);
            await window.VerifyStudioAsync(output);
            await window.VerifyCharacterFlowAsync(output);
            await window.VerifyCharacterSaveFailuresAsync(output);
            await window.VerifyImageAssetsAsync(output);
            await window.VerifyComponentsAsync(output);
            await window.VerifyMathAndFilesAsync(output);
            await window.VerifyAppearanceAndTablesAsync(output);
            await window.VerifyPortraitsAndHistoryAsync(output);
            await window.VerifyGenerationNavigationAsync(output);
            await window.VerifyCharacterUpdatesAsync();
            await window.VerifyScrollingAndStreamingAsync(output);
            await window.VerifyCompactLayoutAsync(output);
            await window.VerifyReaderAsync(output);
            window.SetSidebar(true);
            foreach (var page in new[] { "chat", "cards", "settings" })
            {
                window.ShowPage(page); await Task.Delay(200); window.UpdateLayout(); Capture(window, Path.Combine(output, page + ".png"));
            }
            if (e.Args.Contains("--smoke-chat"))
            {
                window.ShowPage("chat"); window.VM.Draft = "请简要介绍一下你能提供什么帮助。"; await window.VM.SendAsync();
                if (window.VM.Current.Messages.Count < 2 || window.VM.Current.Messages.Last().Incomplete || window.VM.Current.Messages.Last().HasError) throw new Exception("Chat smoke failed: " + window.VM.Status);
                await Task.Delay(200); Capture(window, Path.Combine(output, "conversation.png"));
                var prefix = window.VM.Current.Messages.Last().Content;
                await window.VM.ContinueAsync();
                if (window.VM.Current.Messages.Last().HasError || !window.VM.Current.Messages.Last().Content.StartsWith(prefix)) throw new Exception("Continuation failed");
                await window.VM.SendAsync(true);
                if (window.VM.Current.Messages.Count != 2 || window.VM.Current.Messages.Last().Incomplete || window.VM.Current.Messages.Last().HasError) throw new Exception("Regenerate failed");
                var id = window.VM.Current.Id; await window.VM.NewChatAsync(); await window.VM.OpenAsync(window.VM.Conversations.First(x => x.Id == id));
                if (window.VM.Current.Messages.Count != 2) throw new Exception("Reload failed");
                await window.VM.EditMessageAsync(window.VM.Current.Messages[0], "请给出一个简短示例。");
                if (window.VM.Current.Messages.Count != 2 || window.VM.Current.Messages[0].Content != "请给出一个简短示例。") throw new Exception("Edit and resend failed");
                window.VM.Draft = "测试停止生成。";
                var pending = window.VM.SendAsync(); await Task.Delay(100); window.VM.Stop(); await pending;
                if (window.VM.Busy || !window.VM.Status.Contains("已停止")) throw new Exception("Stop failed: " + window.VM.Status);
                var stopped = window.VM.Current.Messages.Last();
                if (InferenceClient.Trim(window.VM.Current.Messages, "", 24000).Contains(stopped)) throw new Exception("Stopped reply entered context");
                var pixel = new ChatImage { Mime = "image/png", Data = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a4V8AAAAASUVORK5CYII=" };
                window.VM.AddAttachment(pixel); window.VM.Draft = "image edit regression"; await window.VM.SendAsync();
                var pictureMessage = window.VM.Current.Messages.Last(x => x.Role == "user");
                await window.VM.EditMessageAsync(pictureMessage, "");
                if (window.VM.Current.Messages.Last().HasError || window.VM.Current.Messages.Last(x => x.Role == "user").Images.Count != 1) throw new Exception("Image-only edit failed");
                var snapshot = window.VM.Current.CharacterSnapshot;
                if (snapshot is null) throw new Exception("Missing character snapshot");
                var savedId = window.VM.Current.Id;
                var libraryCard = window.VM.Characters.First(x => x.Id == snapshot.Id);
                var oldPrompt = libraryCard.SystemPrompt; window.VM.Characters.Remove(libraryCard);
                await window.VM.NewChatAsync(); await window.VM.OpenAsync(window.VM.Conversations.First(x => x.Id == savedId));
                if (window.VM.Current.CharacterSnapshot?.SystemPrompt != oldPrompt) throw new Exception("Deleted character lost its fallback snapshot");
                window.VM.Characters.Add(libraryCard);

            }
            await window.VM.NewChatAsync(); window.ShowPage("chat"); window.Width = 1080; window.Height = 720;
            await Task.Delay(200); window.UpdateLayout(); Capture(window, Path.Combine(output, "compact.png"));
            window.Width = 820; window.Height = 680;
            await Task.Delay(200); window.UpdateLayout(); Capture(window, Path.Combine(output, "narrow.png"));
            window.Width = 720; window.Height = 620;
            foreach (var page in new[] { "chat", "cards", "settings" })
            {
                window.ShowPage(page); await Task.Delay(260); window.UpdateLayout(); Capture(window, Path.Combine(output, "minimum-" + page + ".png"));
            }
            var process = Process.GetCurrentProcess(); process.Refresh();
            await File.WriteAllTextAsync(Path.Combine(output, "smoke-result.txt"), $"PASS: startup, discovery, service form viewport/edit/validation/persistence, inline confirmations/editors/image viewer, Escape/backdrop cancel, service removal, three pages, normalized icons, component DPI renders, anchored model/reason/protocol selection and actual selection binding, independent concurrent conversations/background drafts/stop isolation/stable history, live character save across active/background/disk chats/prompt update and original opening preservation, persisted light/dark themes and hot formula/table repaint/no speaker labels/rounded window/narrow table layout, 3:4 portrait gallery/crop selection pixels/crop cancel/minimum crop layout/character history names/persisted collapsible history/compact scrollbar range, nested character wheel/middle auto scroll direction and Escape/navigation exit, live heading/bold/italic before completion/automatic thinking expansion and collapse/stop preservation, formula rendering/fallback/source export/reload, system file dialog requests/cancel/card file round trip/source export, fluid gallery, card/detail navigation, inline character create/edit/cancel/persistence, file/bitmap drops, desktop/overlay drawer, compact layout{(e.Args.Contains("--smoke-chat") ? ", chat, regenerate, reload, edit, cancel, continuation, image edit, character snapshot" : "")}\nStartup working set: {startupWorkingSet:F1} MiB\nStartup private bytes: {startupPrivateBytes:F1} MiB\nAfter rendering working set: {process.WorkingSet64 / 1048576.0:F1} MiB\n{window.VM.Connection}");
            if (e.Args.Contains("--smoke-close"))
            {
                var releaseImageImport = await window.PrepareClosingImageImportAsync(output);
                try
                {
                    window.VM.Draft = "navigation slow background close";
                    var background = window.VM.SendAsync(); await Task.Delay(100);
                    await window.VM.NewChatAsync();
                    window.VM.Draft = "close during generation";
                    var pending = window.VM.SendAsync(); await Task.Delay(100);
                    window.Close(); await releaseImageImport(); await Task.WhenAll(background, pending);
                }
                finally { await releaseImageImport(); }
            }
            else window.Close();
        }
        catch (Exception ex)
        {
            if (smoke) { Directory.CreateDirectory(output); await File.WriteAllTextAsync(Path.Combine(output, "smoke-error.txt"), ex.ToString()); }
            else await ShowStartupNoticeAsync("启动失败", ex.Message);
            Shutdown(1);
        }
    }
    private async Task ShowStartupNoticeAsync(string title, string message)
    {
        if (MainWindow is Reflection.Desktop.MainWindow existing && existing.IsVisible && !existing.OverlayOpen)
        {
            await existing.ShowNoticeAsync(title, message); return;
        }
        // If initialization failed before the workspace existed, use the same notice
        // inside an application shell, never a native MessageBox.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var notice = new InlineNotice(title, message);
        var shell = new Window
        {
            Title = "Reflection", Style = (Style)FindResource(typeof(Window)),
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
            AllowsTransparency = true, Background = Brushes.Transparent,
            SizeToContent = SizeToContent.Height, Width = 462,
            MaxHeight = Math.Max(240, SystemParameters.WorkArea.Height - 100),
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Content = new System.Windows.Controls.Border { Background = Brushes.White, CornerRadius = new CornerRadius(16), Child = notice }
        };
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        notice.Completed += (_, _) => shell.Close();
        shell.PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) { shell.Close(); e.Handled = true; } };
        shell.Closed += (_, _) => completion.TrySetResult();
        shell.Show(); notice.PrimaryButton.Focus();
        await completion.Task;
    }
    internal static void Capture(Window window, string path)
    {
        CaptureElement((FrameworkElement)window.Content, path);
    }
    internal static void CaptureElement(FrameworkElement content, string path, double dpi = 96)
    {
        var image = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth * dpi / 96), (int)Math.Ceiling(content.ActualHeight * dpi / 96), dpi, dpi, PixelFormats.Pbgra32);
        var drawing = new DrawingVisual();
        using (var context = drawing.RenderOpen())
            context.DrawRectangle(new VisualBrush(content) { Stretch = Stretch.Fill, ViewboxUnits = BrushMappingMode.Absolute, Viewbox = new Rect(0, 0, content.ActualWidth, content.ActualHeight) }, null, new Rect(0, 0, content.ActualWidth, content.ActualHeight));
        image.Render(drawing);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); using var file = File.Create(path); encoder.Save(file);
    }
    protected override void OnExit(ExitEventArgs e) { instanceLock?.Dispose(); base.OnExit(e); }
}
