using System.IO;
using System.Windows;
using Reflection.Core;

namespace Reflection.Desktop;

public partial class MainWindow
{
    internal async Task<MainWindow> VerifyWorkspaceIsolationAsync(string output, bool slowDiscovery = false)
    {
        var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        var switches = new List<double>();
        if (currentPage != "cards" || ChatPage.IsVisible || VM.Current.Messages.Count != 0 || VM.Conversations.Count == 0)
            throw new InvalidOperationException("Startup restored a chat instead of staying on characters");
        var dailyCard = new CharacterCard { Name = "日常独立角色", SystemPrompt = "daily only" };
        VM.Characters.Add(dailyCard); await VM.SaveCharactersAsync(); await VM.NewChatAsync(dailyCard);
        VM.Current.Title = "日常独立对话"; VM.Current.Messages.Add(new() { Role = "user", Content = "daily-only message" });
        VM.Draft = "daily draft"; var dailyId = VM.Current.Id; await VM.SaveCurrentAsync();
        var dailyFile = Path.Combine(Profile.Root, "daily-reading.md"); await File.WriteAllTextAsync(dailyFile, "# 日常阅读\n\n日常正文");
        ShowPage("reader"); await ReaderPage.AddPathsAsync([dailyFile]);
        var privacy = await SwitchWorkspaceAsync() ?? throw new InvalidOperationException("Privacy transition cancelled");
        switches.Add(LastWorkspaceSwitchMilliseconds);
        if (!ReferenceEquals(privacy, this) || new System.Windows.Interop.WindowInteropHelper(this).Handle != handle) throw new InvalidOperationException("Mode switching recreated the shell window");
        if (slowDiscovery && (VM.DiscoveryTask.IsCompleted || LastWorkspaceSwitchMilliseconds >= 1500)) throw new InvalidOperationException("Mode switching waited for slow HTTP discovery");
        await privacy.ReaderPage.InitializeAsync();
        if (!privacy.Profile.IsPrivate || privacy.currentPage != "cards" || privacy.VM.Characters.Any(c => c.Name == dailyCard.Name) || privacy.VM.Conversations.Count != 0 || privacy.ReaderPage.Sources.Count != 0 || privacy.ReaderPage.CurrentDocument is not null)
            throw new InvalidOperationException("Daily content leaked into private mode");
        var privateCard = new CharacterCard { Name = "隐私独立角色", SystemPrompt = "private only" };
        privacy.VM.Characters.Add(privateCard); await privacy.VM.SaveCharactersAsync(); await privacy.VM.NewChatAsync(privateCard);
        privacy.VM.Current.Title = "隐私独立对话"; privacy.VM.Current.Messages.Add(new() { Role = "user", Content = "private-only message" });
        privacy.VM.Draft = "private draft"; var privateId = privacy.VM.Current.Id; await privacy.VM.SaveCurrentAsync();
        var privateFile = Path.Combine(privacy.Profile.Root, "private-reading.md"); await File.WriteAllTextAsync(privateFile, "# 隐私阅读\n\n隐私正文");
        privacy.ShowPage("reader"); await privacy.ReaderPage.AddPathsAsync([privateFile]); await Task.Delay(220);
        App.Capture(privacy, Path.Combine(output, "workspace-private.png"));
        var daily = await privacy.SwitchWorkspaceAsync() ?? throw new InvalidOperationException("Daily transition cancelled");
        switches.Add(LastWorkspaceSwitchMilliseconds);
        await daily.ReaderPage.InitializeAsync();
        if (daily.Profile.IsPrivate || daily.currentPage != "cards" || daily.VM.Characters.Any(c => c.Name == privateCard.Name) || daily.VM.Conversations.Any(c => c.Id == privateId) || daily.ReaderPage.CurrentDocument?.Path != dailyFile || daily.ReaderPage.Sources.Any(n => n.Path == privateFile))
            throw new InvalidOperationException("Private content leaked into daily mode or reading restore failed");
        await daily.VM.OpenAsync(daily.VM.Conversations.Single(c => c.Id == dailyId));
        if (daily.VM.Draft != "daily draft" || daily.VM.Current.Messages.Single().Content != "daily-only message") throw new InvalidOperationException("Daily draft/history did not survive mode switching");
        var restored = await daily.SwitchWorkspaceAsync() ?? throw new InvalidOperationException("Private restore cancelled");
        switches.Add(LastWorkspaceSwitchMilliseconds);
        await restored.ReaderPage.InitializeAsync();
        await restored.VM.OpenAsync(restored.VM.Conversations.Single(c => c.Id == privateId));
        if (restored.currentPage != "cards" || restored.ReaderPage.CurrentDocument?.Path != privateFile || restored.VM.Draft != "private draft" || restored.VM.Current.Messages.Single().Content != "private-only message")
            throw new InvalidOperationException("Private state did not restore independently");
        var final = await restored.SwitchWorkspaceAsync() ?? throw new InvalidOperationException("Final mode restore cancelled");
        switches.Add(LastWorkspaceSwitchMilliseconds);
        await Task.Delay(220); App.Capture(final, Path.Combine(output, "workspace-daily.png"));
        await File.WriteAllTextAsync(Path.Combine(output, "workspace-result.txt"), "PASS: startup with existing history stays on characters; daily/private chat, character, draft, reading source and document isolation; repeated mode restore; same native window; background discovery.\nSwitch local-ready times (ms): " + string.Join(", ", switches.Select(ms => ms.ToString("F1"))) + "\nSlow discovery fixture: " + slowDiscovery);
        return final;
    }
}
