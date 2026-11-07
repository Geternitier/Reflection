using System.Text.Json;
using System.Collections.Concurrent;

namespace Reflection.Core;

// One file per conversation: only the active conversation is resident in memory.
// Replace + backup keeps the previous valid revision if a write is interrupted.
public sealed class LocalStore
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string root;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly ConcurrentDictionary<string, byte> recovered = new();
    public string Root => root;
    public ImageAssetStore Images { get; }
    public string? RecoveryNotice { get; private set; }
    public static string ResolveRoot() => Environment.GetEnvironmentVariable("REFLECTION_DATA_DIR") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Reflection");
    public bool HasCharactersFile => File.Exists(Path.Combine(root, "characters.json"));
    public LocalStore(string root) { this.root = root; Images = new(root); Directory.CreateDirectory(Path.Combine(root, "chats")); }
    public Task<ApplicationPreferences> LoadApplicationPreferencesAsync() =>
        ReadAsync("application.json", () => new ApplicationPreferences());

    public async Task<AppSettings> LoadSettingsAsync()
    {
        var settings = await ReadAsync("settings.json", () => new AppSettings());
        settings.ParamDefaults ??= [];
        settings.Services ??= [];
        settings.Reason = Prompting.NormalizeReason(settings.Reason);
        return settings;
    }
    public Task<List<CharacterCard>> LoadCharactersAsync() => ReadImagesAsync("characters.json", () => new List<CharacterCard>
    {
        new() { Name = "通用助手", Description = "思考、解答，让日常问题变简单。", SystemPrompt = "你是一位友好、严谨的助手。请用中文回答。" },
        new() { Name = "代码伙伴", Description = "一起拆解问题，写出清晰可靠的代码。", SystemPrompt = "你是一位严谨的软件工程师。用中文解释，提供简洁可靠的代码，指出关键假设。" },
        new() { Name = "灵感作家", Description = "从一个念头，到一个值得讲述的故事。", SystemPrompt = "你是一位富有想象力的中文写作伙伴。帮助用户构思故事、打磨语言，并尊重用户的创作风格。" }
    });
    public async Task<List<ConversationSummary>> LoadIndexAsync()
    {
        List<ConversationSummary> index;
        try { index = await ReadAsync("index.json", () => new List<ConversationSummary>()); }
        catch (Exception ex) when (ex is IOException or JsonException) { index = []; }
        index.RemoveAll(x => File.Exists(Path.Combine(root, $"chats/{x.Id}.deleted")) || (!File.Exists(Path.Combine(root, $"chats/{x.Id}.json")) && !File.Exists(Path.Combine(root, $"chats/{x.Id}.json.bak"))));
        var known = index.Select(x => x.Id).ToHashSet();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "chats"), "*.json"))
        {
            if (!Guid.TryParse(Path.GetFileNameWithoutExtension(file), out var id) || known.Contains(id) || File.Exists(Path.Combine(root, $"chats/{id}.deleted"))) continue;
            var chat = await LoadConversationAsync(id);
            index.Add(new(id, chat.Title, chat.UpdatedAt, Conversation.PreviewOf(chat), chat.CharacterSnapshot?.Name ?? ""));
        }
        foreach (var summary in index.Where(x => x.CharacterName is null))
        {
            var path = Path.Combine(root, $"chats/{summary.Id}.json");
            summary.CharacterName = await Task.Run(() => ReadCharacterHeader(path) ?? ReadCharacterHeader(path + ".bak") ?? "");
        }
        return index.OrderByDescending(x => x.UpdatedAt).ToList();
    }
    // Read only the small metadata prefix, never materialize message/image history for a title.
    private static string? ReadCharacterHeader(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var buffer = new byte[8192]; var retained = 0; var state = new JsonReaderState();
            string? property = null; var snapshotDepth = -1;
            while (true)
            {
                var count = stream.Read(buffer, retained, buffer.Length - retained); var final = count == 0;
                var reader = new Utf8JsonReader(buffer.AsSpan(0, retained + count), final, state);
                while (reader.Read())
                {
                    if (reader.TokenType == JsonTokenType.PropertyName)
                    {
                        property = reader.GetString();
                        if (reader.CurrentDepth == 1 && property == "messages") return "";
                    }
                    else if (reader.TokenType == JsonTokenType.StartObject && property == "characterSnapshot") { snapshotDepth = reader.CurrentDepth + 1; property = null; }
                    else if (reader.TokenType == JsonTokenType.Null && property == "characterSnapshot") return "";
                    else if (reader.TokenType == JsonTokenType.String && property == "name" && reader.CurrentDepth == snapshotDepth) return reader.GetString() ?? "";
                }
                if (final) return "";
                var consumed = (int)reader.BytesConsumed; state = reader.CurrentState;
                retained = retained + count - consumed;
                buffer.AsSpan(consumed, retained).CopyTo(buffer);
                if (retained == buffer.Length)
                {
                    if (buffer.Length >= 262144) return "";
                    Array.Resize(ref buffer, buffer.Length * 2);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }
    public async Task<Conversation> LoadConversationAsync(Guid id)
    {
        var chat = await ReadImagesAsync($"chats/{id}.json", () => new Conversation { Id = id });
        chat.Messages ??= [];
        chat.Attachments ??= [];
        foreach (var message in chat.Messages) message.Images ??= [];
        return chat;
    }
    public async Task SaveApplicationPreferencesAsync(ApplicationPreferences value)
    {
        await gate.WaitAsync();
        try { await WriteCore("application.json", value); }
        finally { gate.Release(); }
    }
    public async Task SaveSettingsAsync(AppSettings value) { await gate.WaitAsync(); try { await WriteCore("settings.json", value); } finally { gate.Release(); } }
    public async Task SaveCharactersAsync(List<CharacterCard> value)
    {
        var snapshot = value.Select(x => ConversationOperations.Snapshot(x)!).ToList();
        await gate.WaitAsync();
        try
        {
            await Task.Run(async () =>
            {
                foreach (var card in snapshot) if (card.Image is not null) card.Image = await Images.ExternalizeAsync(card.Image);
                await WriteCore("characters.json", snapshot);
            });
        }
        finally { gate.Release(); }
    }
    public async Task SaveConversationAsync(Conversation value)
    {
        value = ConversationOperations.Snapshot(value);
        await gate.WaitAsync();
        try
        {
            await Task.Run(async () =>
            {
                if (value.CharacterSnapshot?.Image is { } picture) value.CharacterSnapshot.Image = await Images.ExternalizeAsync(picture);
                for (var i = 0; i < value.Attachments.Count; i++) value.Attachments[i] = await Images.ExternalizeAsync(value.Attachments[i]);
                foreach (var message in value.Messages)
                    for (var i = 0; i < message.Images.Count; i++) message.Images[i] = await Images.ExternalizeAsync(message.Images[i]);
                await WriteCore($"chats/{value.Id}.json", value);
            });
            var index = await LoadIndexAsync();
            index.RemoveAll(x => x.Id == value.Id);
            index.Insert(0, new(value.Id, value.Title, value.UpdatedAt, Conversation.PreviewOf(value), value.CharacterSnapshot?.Name ?? ""));
            await WriteCore("index.json", index);
        }
        finally { gate.Release(); }
    }
    public async Task DeleteConversationAsync(Guid id)
    {
        await gate.WaitAsync();
        try
        {
            // Tombstone prevents a crash or backup recovery from resurrecting a deleted conversation.
            await File.WriteAllTextAsync(Path.Combine(root, $"chats/{id}.deleted"), "");
            var index = await LoadIndexAsync(); index.RemoveAll(x => x.Id == id);
            await WriteCore("index.json", index);
            await WriteCore("index.json", index); // Replace the pre-delete summary backup as well.
            File.Delete(Path.Combine(root, $"chats/{id}.json"));
            File.Delete(Path.Combine(root, $"chats/{id}.json.bak"));
        }
        finally { gate.Release(); }
    }
    private async Task<T> ReadImagesAsync<T>(string relative, Func<T> fallback)
    {
        var value = await ReadAsync(relative, fallback);
        try { await Task.Run(() => BindImages(value)); return value; }
        catch (IOException)
        {
            var path = Path.Combine(root, relative);
            if (!File.Exists(path + ".bak")) throw;
            var backup = await Deserialize<T>(path + ".bak");
            await Task.Run(() => BindImages(backup));
            recovered.TryAdd(path, 0); RecoveryNotice = "图片引用异常，已从上一版备份恢复，请检查最近的修改。";
            return backup;
        }
    }
    private void BindImages<T>(T value)
    {
        if (value is List<CharacterCard> cards)
        {
            foreach (var card in cards) if (card.Image is not null) Images.Attach(card.Image);
        }
        else if (value is Conversation chat)
        {
            chat.Messages ??= []; chat.Attachments ??= [];
            if (chat.CharacterSnapshot?.Image is { } picture) Images.Attach(picture);
            foreach (var image in chat.Attachments) Images.Attach(image);
            foreach (var message in chat.Messages)
            {
                message.Images ??= [];
                foreach (var image in message.Images) Images.Attach(image);
            }
        }
    }
    public async Task<int> CollectImageAssetsAsync(CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try { return await Task.Run(() => Images.CollectUnusedAsync(root, ct), ct); }
        finally { gate.Release(); }
    }
    private async Task<T> ReadAsync<T>(string relative, Func<T> fallback)
    {
        var path = Path.Combine(root, relative);
        if (!File.Exists(path))
        {
            if (!File.Exists(path + ".bak")) return fallback();
            var backup = await Deserialize<T>(path + ".bak"); recovered.TryAdd(path, 0);
            RecoveryNotice = "已从上一版备份恢复数据，请检查最近的修改。"; return backup;
        }
        try { return await Deserialize<T>(path); }
        catch (JsonException)
        {
            if (File.Exists(path + ".bak"))
            {
                var value = await Deserialize<T>(path + ".bak"); recovered.TryAdd(path, 0); RecoveryNotice = "已从上一版备份恢复数据，请检查最近的修改。"; return value;
            }
            throw new IOException($"数据文件损坏，请保留文件并从备份恢复：{path}");
        }
    }
    private static async Task<T> Deserialize<T>(string path)
    {
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<T>(stream, Json) ?? throw new JsonException("Empty data");
    }
    private async Task WriteCore<T>(string relative, T value)
    {
        var path = Path.Combine(root, relative); var temp = path + "." + Guid.NewGuid() + ".tmp";
        try
        {
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
                await JsonSerializer.SerializeAsync(stream, value, Json);
            if (recovered.ContainsKey(path))
            {
                // The primary is corrupt: never rotate it over the valid backup.
                File.Move(temp, path, true); recovered.TryRemove(path, out _);
            }
            else if (File.Exists(path)) File.Replace(temp, path, path + ".bak");
            else File.Move(temp, path);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
