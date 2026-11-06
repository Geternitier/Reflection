using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows.Data;
using Reflection.Core;

namespace Reflection.Desktop;

public sealed class MainViewModel : Observable, IDisposable
{
    public LocalStore Store { get; }
    private readonly InferenceClient inference = new();
    private readonly CancellationTokenSource lifetime = new();
    private sealed class GenerationRun(Conversation chat, ModelOption model, AppSettings settings, CancellationTokenSource cancel, string activity)
    {
        public Conversation Chat { get; } = chat;
        public ModelOption Model { get; } = model;
        public AppSettings Settings { get; } = settings;
        public CancellationTokenSource Cancel { get; } = cancel;
        public string Activity { get; } = activity;
        public string Status { get; set; } = "";
        public TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private readonly Dictionary<Guid, GenerationRun> runs = [];
    private GenerationRun? CurrentRun => runs.GetValueOrDefault(Current.Id);
    private readonly SemaphoreSlim navigation = new(1, 1);
    private bool discovering;
    public AppSettings Settings { get; private set; } = new();
    public ObservableCollection<ConversationSummary> Conversations { get; } = [];
    public ICollectionView History { get; }
    public ObservableCollection<CharacterCard> Characters { get; } = [];
    public ObservableCollection<ModelOption> Models { get; } = [];
    public ObservableCollection<ServiceStatus> Services { get; } = [];
    private Conversation current = new();
    public Conversation Current { get => current; private set => ReplaceCurrent(value); }
    private ModelOption? selectedModel;
    public ModelOption? SelectedModel
    {
        get => selectedModel;
        set
        {
            if (!Set(ref selectedModel, value)) return;
            Raise(nameof(VisionWarning));
            if (value is { Info.Inspected: false } model) _ = EnsureModelInfoAsync(model);
        }
    }
    private CharacterCard? selectedCharacter;
    public CharacterCard? SelectedCharacter
    {
        get => selectedCharacter;
        set
        {
            Set(ref selectedCharacter, value);
            Current.CharacterId = value?.Id;
            Current.CharacterSnapshot = ConversationOperations.Snapshot(value);
            Raise(nameof(Current));
            Raise(nameof(CharacterName)); Raise(nameof(CharacterDescription));
        }
    }
    public string CharacterName => Current.CharacterSnapshot?.Name ?? "本地助手";
    public string CharacterDescription => Current.CharacterSnapshot?.Description is { Length: > 0 } description ? description : "";
    private CharacterCard? detailCard;
    public CharacterCard? DetailCard { get => detailCard; set => Set(ref detailCard, value); }
    private long draftRevision, savedDraftRevision;
    private string draft = "";
    public string Draft { get => draft; set { if (Set(ref draft, value)) draftRevision++; } }
    private string search = "";
    public string Search
    {
        get => search;
        set { if (!Set(ref search, value)) return; History.Refresh(); Raise(nameof(HistoryEmpty)); Raise(nameof(HistoryEmptyText)); }
    }
    private string status = "正在准备本地工作区…";
    public string Status { get => CurrentRun?.Status is { Length: > 0 } text ? text : status; set => Set(ref status, value); }
    private string connection = "正在检测服务";
    public string Connection { get => connection; set => Set(ref connection, value); }
    private bool loading;
    public bool Busy => CurrentRun is not null;
    public bool AnyBusy => runs.Count > 0;
    public bool Idle => !Busy && !loading;
    public string SendLabel => Busy && CurrentRun?.Activity != "help" ? "■  停止" : "发送  ↑";
    public string HelpLabel => Busy && CurrentRun?.Activity == "help" ? "停止" : "帮我写";
    private void NotifyActivity()
    {
        Raise(nameof(Busy)); Raise(nameof(AnyBusy)); Raise(nameof(Idle));
        Raise(nameof(SendLabel)); Raise(nameof(HelpLabel)); Raise(nameof(Status));
    }
    private GenerationRun BeginRun(string activity)
    {
        RefreshCharacter(Current);
        var original = SelectedModel!;
        var service = original.Service;
        var model = new ModelOption(new ServiceConfig { Id = service.Id, Name = service.Name, BaseUrl = service.BaseUrl, Protocol = service.Protocol, Enabled = service.Enabled }, original.Model);
        model.Info.CopyFrom(original.Info);
        var run = new GenerationRun(Current, model, JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(Settings))!, CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token), activity);
        runs.Add(Current.Id, run); Current.UpdatedAt = DateTimeOffset.Now; NotifyActivity(); return run;
    }
    private void SetRunStatus(GenerationRun run, string text)
    {
        run.Status = text;
        if (ReferenceEquals(Current, run.Chat)) Raise(nameof(Status));
    }
    private void ScrollRun(GenerationRun run) { if (ReferenceEquals(Current, run.Chat)) ScrollRequested?.Invoke(); }
    private async Task StopRunAsync(Guid id)
    {
        if (!runs.TryGetValue(id, out var run)) return;
        run.Cancel.Cancel(); await run.Finished.Task;
    }
    public async Task StopAllAsync()
    {
        var pending = runs.Values.ToArray();
        foreach (var run in pending) run.Cancel.Cancel();
        await Task.WhenAll(pending.Select(run => run.Finished.Task));
    }
    public string ReasonLabel => Settings.Reason switch { "low" => "低", "medium" => "中", "high" => "高", _ => "关" };
    public bool IsEmpty => Current.Messages.Count == 0;
    public bool HasAttachments => Current.Attachments.Count > 0;
    public bool VisionWarning => HasAttachments && SelectedModel?.Info is { CapsChecked: true } info && info.Capabilities.Count > 0 && !info.Capabilities.Contains("vision");
    public bool HistoryEmpty => History.IsEmpty;
    public string HistoryEmptyText => string.IsNullOrWhiteSpace(Search) ? "还没有对话" : "没有匹配的对话";
    public event Action? ScrollRequested;
    public event Action? ModelInfoChanged;
    public MainViewModel(string? dataRoot = null)
    {
        Store = new(dataRoot ?? LocalStore.ResolveRoot());
        Hook(current);
        History = CollectionViewSource.GetDefaultView(Conversations);
        History.Filter = item => item is ConversationSummary chat && (chat.DisplayTitle.Contains(Search, StringComparison.OrdinalIgnoreCase) || chat.Title.Contains(Search, StringComparison.OrdinalIgnoreCase) || chat.Preview.Contains(Search, StringComparison.OrdinalIgnoreCase));
        History.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ConversationSummary.DayKey)));
        ((INotifyCollectionChanged)History).CollectionChanged += (_, _) => { Raise(nameof(HistoryEmpty)); Raise(nameof(HistoryEmptyText)); };
    }
    public Task DiscoveryTask { get; private set; } = Task.CompletedTask;
    public async Task InitializeAsync(bool waitForDiscovery = true, bool applyTheme = true)
    {
        Settings = await Store.LoadSettingsAsync();
        if (applyTheme) ThemeManager.Apply(Settings.Theme);
        Raise(nameof(HistoryExpanded));
        var seedCharacters = !Store.HasCharactersFile;
        foreach (var card in await Store.LoadCharactersAsync()) Characters.Add(card);
        if (seedCharacters) await Store.SaveCharactersAsync(Characters.ToList());
        var summaries = await Store.LoadIndexAsync();
        foreach (var chat in summaries.OrderByDescending(x => x.UpdatedAt)) Conversations.Add(chat);
        SelectedCharacter = Characters.FirstOrDefault();
        DetailCard = Characters.FirstOrDefault();
        Raise(nameof(ReasonLabel));
        Status = Store.RecoveryNotice ?? "";
        DiscoveryTask = DiscoverInitiallyAsync();
        if (waitForDiscovery) await DiscoveryTask;
        _ = DiscoveryLoopAsync();
        _ = DraftLoopAsync();
    }
    private async Task DiscoverInitiallyAsync()
    {
        try { await DiscoverAsync(); }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (Exception ex) { Status = "连接检测失败：" + ex.Message; }
    }
    public void RestoreCharacter(CharacterCard? snapshot, Guid? id)
    {
        Current.CharacterSnapshot = snapshot; Current.CharacterId = id;
        Raise(nameof(CharacterName)); Raise(nameof(CharacterDescription)); Raise(nameof(Current));
    }
    private async Task DraftLoopAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(3));
        try
        {
            while (await timer.WaitForNextTickAsync(lifetime.Token))
            {
                if (!Idle || draftRevision == savedDraftRevision) continue;
                var revision = draftRevision;
                try { await PersistDraftAsync(); savedDraftRevision = revision; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Status = "草稿保存失败：" + ex.Message; }
            }
        }
        catch (OperationCanceledException) { }
    }
    private async Task DiscoveryLoopAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        try { while (await timer.WaitForNextTickAsync(lifetime.Token)) if (Settings.AutoDiscover && !AnyBusy) await DiscoverAsync(); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Status = "自动检测已暂停：" + ex.Message; }
    }
    public async Task DiscoverAsync()
    {
        if (discovering || AnyBusy) return;
        discovering = true;
        try
        {
            Connection = "正在检测已配置的服务…";
            using var concurrency = new SemaphoreSlim(3);
            var results = await Task.WhenAll(Settings.Services.Where(x => x.Enabled).Select(async x =>
            {
                await concurrency.WaitAsync(lifetime.Token);
                try { return await inference.DiscoverAsync(x, lifetime.Token); }
                finally { concurrency.Release(); }
            }));
            var old = SelectedModel; Services.Clear(); Models.Clear();
            foreach (var service in results) { Services.Add(service); foreach (var model in service.Models) Models.Add(model); }
            SelectedModel = Models.FirstOrDefault(x => x.Service.Id == old?.Service.Id && x.Model == old.Model)
                ?? Models.FirstOrDefault(x => x.Service.Id == Current.ServiceId && x.Model == Current.Model) ?? Models.FirstOrDefault();
            var online = results.Count(x => x.Online);
            Connection = online > 0 ? $"{online} 个服务在线 · {Models.Count} 个模型" : "暂无在线服务 · 前往设置连接";
        }
        finally { discovering = false; }
    }
    private readonly Dictionary<string, Task<ModelInfo>> inspections = [];
    public async Task EnsureModelInfoAsync(ModelOption model)
    {
        var loaded = model.Info.Inspected;
        if (!loaded)
        {
            var key = model.Service.Id + "\n" + model.Model;
            if (!inspections.TryGetValue(key, out var pending))
            {
                pending = inference.InspectAsync(model, lifetime.Token);
                inspections[key] = pending;
            }
            model.Info.CopyFrom(await pending);
            if (!model.Info.Inspected) inspections.Remove(key);
        }
        if (!loaded && SelectedModel == model) { Raise(nameof(VisionWarning)); ModelInfoChanged?.Invoke(); }
    }
    public async Task OpenAsync(ConversationSummary summary)
    {
        await navigation.WaitAsync();
        try
        {
            if (summary.Id == Current.Id) return;
            loading = true; Raise(nameof(Idle));
            await PersistDraftAsync();
            Current = runs.TryGetValue(summary.Id, out var active) ? active.Chat : await Store.LoadConversationAsync(summary.Id);
            var snapshot = Current.CharacterSnapshot;
            var characterId = Current.CharacterId;
            SelectedCharacter = Characters.FirstOrDefault(x => x.Id == characterId);
            if (snapshot is not null) { Current.CharacterSnapshot = snapshot; Current.CharacterId = characterId; }
            RefreshCharacter(Current);
            Raise(nameof(CharacterName)); Raise(nameof(CharacterDescription));
            SelectedModel = Models.FirstOrDefault(x => x.Service.Id == Current.ServiceId && x.Model == Current.Model) ?? SelectedModel;
            Draft = Current.Draft ?? "";
            if (Store.RecoveryNotice is { } notice) Status = notice;
            ScrollRequested?.Invoke();
        }
        finally { loading = false; Raise(nameof(Idle)); navigation.Release(); }
    }
    public async Task NewChatAsync(CharacterCard? character = null)
    {
        await navigation.WaitAsync();
        try
        {
            loading = true; Raise(nameof(Idle));
            var reuse = IsUnused();
            if (!reuse) await PersistDraftAsync();
            if (reuse && character is null) { Status = ""; return; }
            if (!reuse) Current = new();
            else Current.Messages.Clear();
            Draft = ""; Current.Draft = ""; Current.Attachments.Clear();
            ApplyCharacter(character ?? SelectedCharacter ?? Characters.FirstOrDefault());
            if (!IsUnused()) await SaveCurrentAsync();
            Status = "";
        }
        finally { loading = false; Raise(nameof(Idle)); navigation.Release(); }
    }
    public async Task SendAsync(bool regenerate = false, bool replyOnly = false)
    {
        if (loading) return;
        if (Busy) { CurrentRun?.Cancel.Cancel(); return; }
        if (SelectedModel is null) { Status = "尚未选择可用模型。请启动本地服务，然后在设置中检测连接。"; return; }
        var typed = !string.IsNullOrWhiteSpace(Draft) || Current.Attachments.Count > 0;
        var openingOnly = !typed && CanReplyToOpening();
        if (!regenerate && !replyOnly && !typed && !openingOnly) return;
        if ((regenerate || replyOnly) && !Current.Messages.Any(x => x.Role == "user" && InferenceClient.HasPayload(x))) return;
        if (regenerate) { while (Current.Messages.Count > 0 && Current.Messages[^1] is { Role: "assistant", Opening: false }) Current.Messages.RemoveAt(Current.Messages.Count - 1); }
        else if (typed)
        {
            var text = Draft.Trim();
            var message = new ChatMessage { Role = "user", Content = text };
            foreach (var image in Current.Attachments) message.Images.Add(image);
            Current.Messages.Add(message); Current.Attachments.Clear(); Draft = ""; Current.Draft = "";
            if (Current.Title == "新的对话") { var first = text.Length > 0 ? text.ReplaceLineEndings(" ") : "图片"; Current.Title = first.Length > 28 ? first[..28] + "…" : first; }
        }
        else if (replyOnly) { while (Current.Messages.Count > 0 && Current.Messages[^1] is { Role: "assistant", Opening: false } tail && string.IsNullOrWhiteSpace(tail.Content)) Current.Messages.RemoveAt(Current.Messages.Count - 1); }
        await CompleteAsync();
    }
    public async Task ContinueAsync()
    {
        if (Busy) { CurrentRun?.Cancel.Cancel(); return; }
        var last = Current.Messages.LastOrDefault();
        if (last is { Role: "assistant", Opening: false } message && !string.IsNullOrWhiteSpace(message.Content)) { await ExtendAsync(message); return; }
        await SendAsync(replyOnly: true);
    }
    public async Task HelpDraftAsync()
    {
        if (loading) return;
        if (Busy) { Stop(); return; }
        if (SelectedModel is null) { Status = "尚未选择可用模型。请启动本地服务，然后在设置中检测连接。"; return; }
        var existing = Draft.Trim();
        if (existing.Length == 0 && !Current.Messages.Any(InferenceClient.HasPayload)) { Status = "先写一点内容，或先开始一轮对话"; return; }
        Current.Draft = Draft;
        var run = BeginRun("help"); var model = run.Model;
        var raw = new StringBuilder(); var thoughts = new StringBuilder();
        void PaintDraft()
        {
            run.Chat.Draft = Prompting.SeparateThink(raw.ToString(), thoughts.ToString()).Content;
            if (ReferenceEquals(Current, run.Chat)) Draft = run.Chat.Draft;
        }
        try
        {
            await EnsureModelInfoAsync(model);
            var story = SystemFor(run);
            var guide = story.Length == 0 ? Prompting.HelpSystem : Prompting.HelpSystem + "\n\n" + story;
            var instruction = existing.Length > 0 ? "请把下面的草稿改写成一条更清楚、可以直接发送的消息。只输出改写结果。\n\n" + existing : "请根据以上对话，起草我接下来可以发送的一条消息。只输出消息正文。";
            SetRunStatus(run, "正在起草下一条消息…");
            var watch = Stopwatch.StartNew(); long painted = 0;
            await foreach (var delta in inference.StreamAsync(model, run.Chat.Messages.ToArray(), guide, run.Settings, run.Cancel.Token, model.Info, instruction))
            {
                raw.Append(delta.Text); thoughts.Append(delta.Thinking);
                if (raw.Length + thoughts.Length > 500_000) throw new IOException("草稿达到输出上限。");
                if (watch.ElapsedMilliseconds - painted > 50) { PaintDraft(); painted = watch.ElapsedMilliseconds; }
            }
            PaintDraft(); SetRunStatus(run, "草稿已写好，可以修改后发送");
        }
        catch (OperationCanceledException) { PaintDraft(); SetRunStatus(run, run.Cancel.IsCancellationRequested ? "已停止起草" : "服务响应超时"); }
        catch (Exception ex) when (ex is HttpRequestException or IOException or System.Text.Json.JsonException or ArgumentException) { SetRunStatus(run, ex.Message); }
        finally { await FinishRunAsync(run); }
    }
    public async Task SaveCurrentAsync()
    {
        var chat = Current; var revision = draftRevision;
        chat.Draft = Draft;
        await SaveConversationAsync(chat);
        if (ReferenceEquals(chat, Current) && revision == draftRevision) savedDraftRevision = revision;
    }
    private async Task SaveConversationAsync(Conversation chat)
    {
        RefreshCharacter(chat);
        await Store.SaveConversationAsync(chat);
        var summary = Conversations.FirstOrDefault(x => x.Id == chat.Id);
        if (summary is null) Conversations.Insert(0, new(chat.Id, chat.Title, chat.UpdatedAt, Conversation.PreviewOf(chat), chat.CharacterSnapshot?.Name ?? ""));
        else
        {
            summary.CharacterName = chat.CharacterSnapshot?.Name ?? ""; summary.Title = chat.Title; summary.Preview = Conversation.PreviewOf(chat); summary.UpdatedAt = chat.UpdatedAt;
        }
    }
    public async Task DeleteAsync(Guid id)
    {
        if (loading) return;
        await StopRunAsync(id);
        await Store.DeleteConversationAsync(id);
        var item = Conversations.FirstOrDefault(x => x.Id == id); if (item is not null) Conversations.Remove(item);
        if (Current.Id != id) return;
        Current = new(); Draft = "";
        ApplyCharacter(SelectedCharacter ?? Characters.FirstOrDefault());
        if (!IsUnused()) await SaveCurrentAsync();
    }
    public async Task RenameAsync(ConversationSummary summary)
    {
        var title = summary.Title.Trim();
        if (!Idle || title.Length == 0) return;
        summary.Title = title;
        if (Current.Id == summary.Id) { Current.Title = title; await SaveCurrentAsync(); return; }
        var chat = runs.TryGetValue(summary.Id, out var active) ? active.Chat : await Store.LoadConversationAsync(summary.Id);
        chat.Title = title; chat.UpdatedAt = DateTimeOffset.Now;
        await Store.SaveConversationAsync(chat);
        summary.Title = chat.Title; summary.UpdatedAt = chat.UpdatedAt; summary.Preview = Conversation.PreviewOf(chat);
    }
    public Task SaveCharactersAsync() => Store.SaveCharactersAsync(Characters.ToList());

    private void RefreshCharacter(Conversation chat)
    {
        if (Characters.FirstOrDefault(card => card.Id == chat.CharacterId) is { } latest)
            ConversationOperations.RefreshCharacter(chat, latest);
    }

    public async Task ApplyCharacterUpdateAsync(CharacterCard edited)
    {
        await navigation.WaitAsync();
        try
        {
            // Use live instances even if a background run finishes while saves await disk I/O.
            var live = runs.Values.Select(run => run.Chat).Append(Current).DistinctBy(chat => chat.Id).ToDictionary(chat => chat.Id);
            foreach (var chat in live.Values) ConversationOperations.RefreshCharacter(chat, edited);
            if (selectedCharacter?.Id == edited.Id) { selectedCharacter = edited; Raise(nameof(SelectedCharacter)); }
            Raise(nameof(CharacterName)); Raise(nameof(CharacterDescription));
            foreach (var summary in Conversations.ToArray())
            {
                var chat = live.GetValueOrDefault(summary.Id) ?? await Store.LoadConversationAsync(summary.Id);
                if (ConversationOperations.RefreshCharacter(chat, edited)) await SaveConversationAsync(chat);
            }
        }
        finally { navigation.Release(); }
    }
    public async Task EditMessageAsync(ChatMessage message, string text)
    {
        if (Busy || string.IsNullOrWhiteSpace(text) && message.Images.Count == 0) return;
        if (message.Opening || message.Role != "user")
        {
            message.Content = text.Trim();
            if (message.Role == "assistant") { message.Thinking = ""; message.Error = ""; message.Stopped = false; }
            MarkTail(); await SaveCurrentAsync(); Status = "消息已更新"; return;
        }
        if (SelectedModel is null) { Status = "请先连接模型，再编辑并重新发送。"; return; }
        ConversationOperations.ReplaceUserMessage(Current, message, text);
        await CompleteAsync();
    }
    public void AddAttachment(ChatImage image)
    {
        if (Current.Attachments.Count >= 4) { Status = "一次最多 4 张图片"; return; }
        Current.Attachments.Add(image); Status = "图片已加入，可移除后发送";
    }
    public void RemoveAttachment(ChatImage image) => Current.Attachments.Remove(image);
    public bool HistoryExpanded => Settings.HistoryExpanded;
    public async Task ToggleHistoryAsync()
    {
        Settings.HistoryExpanded = !Settings.HistoryExpanded; Raise(nameof(HistoryExpanded));
        await Store.SaveSettingsAsync(Settings);
    }
    public async Task ToggleThemeAsync()
    {
        Settings.Theme = Settings.Theme == "dark" ? "light" : "dark";
        ThemeManager.Apply(Settings.Theme);
        await Store.SaveSettingsAsync(Settings);
    }
    public async Task SetReasonAsync(string reason)
    {
        Settings.Reason = Prompting.NormalizeReason(reason);
        Raise(nameof(ReasonLabel));
        await Store.SaveSettingsAsync(Settings);
        Status = "";
    }
    public async Task SaveSettingsAsync(bool discover = true) { inspections.Clear(); await Store.SaveSettingsAsync(Settings); if (discover) await DiscoverAsync(); }
    public void Stop() => CurrentRun?.Cancel.Cancel();
    public bool IsUnused() => Current.Messages.Count == 0 && string.IsNullOrWhiteSpace(Draft) && Current.Attachments.Count == 0;
    public async Task PersistDraftAsync()
    {
        Current.Draft = Draft;
        if (!IsUnused() || Conversations.Any(x => x.Id == Current.Id)) await SaveCurrentAsync();
    }
    public void Dispose() { lifetime.Cancel(); foreach (var run in runs.Values.ToArray()) run.Cancel.Cancel(); inference.Dispose(); }
    private async Task CompleteAsync()
    {
        var run = BeginRun("chat"); var chat = run.Chat; var model = run.Model;
        chat.ServiceId = model.Service.Id; chat.Model = model.Model;
        var answer = new ChatMessage { Role = "assistant", Incomplete = true }; chat.Messages.Add(answer);
        await GenerateAsync(run, answer, "", "", null);
    }
    private async Task ExtendAsync(ChatMessage answer)
    {
        if (SelectedModel is null) { Status = "尚未选择可用模型。请启动本地服务，然后在设置中检测连接。"; return; }
        var run = BeginRun("chat");
        var history = ConversationOperations.Continuation(run.Chat.Messages, answer);
        var prefix = answer.Content; var prior = answer.Thinking;
        answer.Incomplete = true; answer.Error = ""; answer.Stopped = false;
        await GenerateAsync(run, answer, prefix, prior, history);
    }
    private async Task GenerateAsync(GenerationRun run, ChatMessage answer, string prefix, string prior, IReadOnlyList<ChatMessage>? continuation)
    {
        var raw = new StringBuilder(); var thoughts = new StringBuilder(); var watch = Stopwatch.StartNew(); long painted = 0;
        try
        {
            await SaveConversationAsync(run.Chat);
            await EnsureModelInfoAsync(run.Model);
            SetRunStatus(run, continuation is null ? $"{run.Model.Service.Name} 正在思考…" : "正在接着写…"); ScrollRun(run);
            var history = continuation ?? run.Chat.Messages.Where(x => x != answer).ToArray();
            await foreach (var delta in inference.StreamAsync(run.Model, history, SystemFor(run), run.Settings, run.Cancel.Token, run.Model.Info, continuation is null ? null : Prompting.ContinueCue))
            {
                raw.Append(delta.Text); thoughts.Append(delta.Thinking);
                if (prefix.Length + raw.Length + thoughts.Length > 500_000) throw new IOException("回复已达到 50 万字符上限，已停止生成。");
                if (watch.ElapsedMilliseconds - painted > 50) { Paint(answer, raw, thoughts, prefix, prior); painted = watch.ElapsedMilliseconds; ScrollRun(run); }
            }
            Paint(answer, raw, thoughts, prefix, prior); answer.Incomplete = false;
            SetRunStatus(run, $"已完成 · {watch.Elapsed.TotalSeconds:F1} 秒 · {answer.Content.Length:N0} 字符");
        }
        catch (OperationCanceledException)
        {
            answer.Stopped = true; Paint(answer, raw, thoughts, prefix, prior);
            SetRunStatus(run, run.Cancel.IsCancellationRequested ? "已停止生成 · 部分回复已保留" : "服务响应超时 · 可以重新生成");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or System.Text.Json.JsonException or ArgumentException)
        { answer.Error = ex.Message; Paint(answer, raw, thoughts, prefix, prior); SetRunStatus(run, ex.Message); }
        finally
        {
            answer.IsThinking = false; answer.Incomplete = false;
            if (answer.Content.Length == 0 && answer.Thinking.Length == 0 && !answer.HasError && !answer.Stopped) run.Chat.Messages.Remove(answer);
            MarkTail(run.Chat); await FinishRunAsync(run);
        }
    }
    private async Task FinishRunAsync(GenerationRun run)
    {
        try { await SaveConversationAsync(run.Chat); }
        finally
        {
            runs.Remove(run.Chat.Id); run.Cancel.Dispose();
            if (ReferenceEquals(Current, run.Chat)) { status = run.Status; NotifyActivity(); ScrollRun(run); }
            else Raise(nameof(AnyBusy));
            run.Finished.TrySetResult();
        }
    }
    private static void Paint(ChatMessage answer, StringBuilder raw, StringBuilder thoughts, string prefix, string priorThinking)
    {
        var rawText = raw.ToString();
        var separated = Prompting.SeparateThink(rawText, thoughts.ToString());
        var openTag = rawText.LastIndexOf("<think>", StringComparison.OrdinalIgnoreCase);
        var closedTag = rawText.LastIndexOf("</think>", StringComparison.OrdinalIgnoreCase);
        answer.IsThinking = answer.Incomplete && separated.Thinking.Length > 0 && string.IsNullOrWhiteSpace(separated.Content) && !(openTag >= 0 && closedTag > openTag);
        answer.Content = prefix.Length == 0 ? separated.Content : Prompting.JoinContinuation(prefix, separated.Content);
        if (priorThinking.Length == 0) answer.Thinking = separated.Thinking;
        else if (separated.Thinking.Length == 0) answer.Thinking = priorThinking;
        else answer.Thinking = priorThinking + "\n\n" + separated.Thinking;
    }
    private static string SystemFor(GenerationRun run) => Prompting.ResolveSystem(run.Chat.CharacterSnapshot, run.Settings.SystemPrompt, run.Model.Info.SystemPrompt, run.Model.Service.Protocol == ApiProtocol.Ollama);
    private bool CanReplyToOpening() => Current.Messages.Count == 1 && Current.Messages[0] is { Opening: true, Role: "user" } message && !string.IsNullOrWhiteSpace(message.Content);
    private void ApplyCharacter(CharacterCard? character)
    {
        SelectedCharacter = character;
        if (string.IsNullOrWhiteSpace(character?.Greeting)) return;
        Current.Messages.Add(new() { Role = character.OpeningRole == "user" ? "user" : "assistant", Content = character.Greeting, Opening = true });
        Raise(nameof(IsEmpty));
    }
    private void ReplaceCurrent(Conversation value)
    {
        if (ReferenceEquals(current, value)) return;
        Unhook(current); current = value; Hook(current);
        status = ""; NotifyActivity();
        Raise(nameof(Current)); Raise(nameof(IsEmpty)); Raise(nameof(HasAttachments)); Raise(nameof(VisionWarning));
        MarkTail();
    }
    private void Hook(Conversation chat)
    {
        chat.Messages ??= []; chat.Attachments ??= [];
        chat.Messages.CollectionChanged += OnMessages;
        chat.Attachments.CollectionChanged += OnAttachments;
    }
    private void Unhook(Conversation chat)
    {
        chat.Messages.CollectionChanged -= OnMessages;
        chat.Attachments.CollectionChanged -= OnAttachments;
    }
    private void OnMessages(object? sender, NotifyCollectionChangedEventArgs e) { Raise(nameof(IsEmpty)); MarkTail(); }
    private void OnAttachments(object? sender, NotifyCollectionChangedEventArgs e) { draftRevision++; Raise(nameof(HasAttachments)); Raise(nameof(VisionWarning)); }
    private void MarkTail() => MarkTail(Current);
    private static void MarkTail(Conversation chat)
    {
        var tail = chat.Messages.LastOrDefault() is { Role: "assistant", Opening: false, Incomplete: false } last ? last : null;
        foreach (var message in chat.Messages) message.IsTail = message == tail;
    }
}
