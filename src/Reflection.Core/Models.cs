using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace Reflection.Core;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value; Raise(name); return true;
    }
    public void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}

public enum ApiProtocol { Ollama, OpenAI }
public sealed class ServiceConfig
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Ollama";
    public string BaseUrl { get; set; } = "http://127.0.0.1:11434";
    public ApiProtocol Protocol { get; set; }
    public bool Enabled { get; set; } = true;
    [JsonIgnore] public string EnabledLabel => Enabled ? "自动发现已启用" : "已停用";
    public override string ToString() => Name;
}
public sealed class ModelInfo
{
    public string SystemPrompt { get; set; } = "";
    public Dictionary<string, string> Defaults { get; set; } = [];
    public List<string> Capabilities { get; set; } = [];
    public string ContextLimit { get; set; } = "";
    public bool Inspected { get; set; }
    public bool CapsChecked { get; set; }
    public void CopyFrom(ModelInfo other)
    {
        SystemPrompt = other.SystemPrompt; Defaults = new(other.Defaults); Capabilities = [.. other.Capabilities];
        ContextLimit = other.ContextLimit; Inspected = other.Inspected; CapsChecked = other.CapsChecked;
    }
}
public sealed record ModelOption(ServiceConfig Service, string Model)
{
    public string Label => $"{Model}  ·  {Service.Name}";
    public ModelInfo Info { get; } = new();
}
public sealed record ServiceStatus(ServiceConfig Config, IReadOnlyList<ModelOption> Models, string Detail)
{
    public bool Online => Detail == "已连接";
}
public sealed class ChatImage
{
    public string Mime { get; set; } = "image/jpeg";
    public string Data { get; set; } = "";
}
public sealed class ChatMessage : Observable
{
    public string Role { get; set; } = "user";
    private string content = "";
    public string Content { get => content; set => Set(ref content, value); }
    private string thinking = "";
    public string Thinking { get => thinking; set { if (Set(ref thinking, value)) Raise(nameof(HasThinking)); } }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    private bool isThinking;
    [JsonIgnore] public bool IsThinking { get => isThinking; set => Set(ref isThinking, value); }
    private bool incomplete;
    public bool Incomplete { get => incomplete; set => Set(ref incomplete, value); }
    public bool Opening { get; set; }
    private string error = "";
    public string Error { get => error; set { if (Set(ref error, value)) Raise(nameof(HasError)); } }
    private bool stopped;
    public bool Stopped { get => stopped; set => Set(ref stopped, value); }
    public ObservableCollection<ChatImage> Images { get; set; } = [];
    [JsonIgnore] public string Author => Role == "user" ? "你" : "REFLECTION";
    [JsonIgnore] public bool HasThinking => !string.IsNullOrWhiteSpace(Thinking);
    [JsonIgnore] public bool HasError => !string.IsNullOrWhiteSpace(Error);
    [JsonIgnore] public bool HasImages => Images.Count > 0;
    private bool isTail;
    [JsonIgnore] public bool IsTail { get => isTail; set => Set(ref isTail, value); }
}
public sealed class Conversation : Observable
{
    public Guid Id { get; set; } = Guid.NewGuid();
    private string title = "新的对话";
    public string Title { get => title; set => Set(ref title, value); }
    public Guid? CharacterId { get; set; }
    public CharacterCard? CharacterSnapshot { get; set; }
    public Guid? ServiceId { get; set; }
    public string? Model { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
    public string Draft { get; set; } = "";
    public ObservableCollection<ChatMessage> Messages { get; set; } = [];
    public ObservableCollection<ChatImage> Attachments { get; set; } = [];
    public static string PreviewOf(Conversation chat)
    {
        var last = chat.Messages.LastOrDefault(x => !string.IsNullOrWhiteSpace(x.Content) || x.Images.Count > 0);
        if (last is null) return "还没有消息";
        if (string.IsNullOrWhiteSpace(last.Content)) return "图片";
        var text = string.Join(' ', last.Content.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return text.Length <= 36 ? text : text[..36];
    }
}
public sealed class ConversationSummary : Observable
{
    public ConversationSummary() { }
    public ConversationSummary(Guid id, string title, DateTimeOffset updatedAt, string preview = "", string? characterName = null)
    {
        Id = id; Title = title; UpdatedAt = updatedAt; Preview = preview; CharacterName = characterName;
    }
    public Guid Id { get; set; }
    private string title = "";
    public string Title { get => title; set { if (Set(ref title, value)) Raise(nameof(DisplayTitle)); } }
    private string? characterName;
    public string? CharacterName { get => characterName; set { if (Set(ref characterName, value)) Raise(nameof(DisplayTitle)); } }
    [JsonIgnore] public string DisplayTitle => string.IsNullOrWhiteSpace(CharacterName) ? Title : CharacterName;
    private DateTimeOffset updatedAt;
    public DateTimeOffset UpdatedAt { get => updatedAt; set { if (Set(ref updatedAt, value)) Raise(nameof(DayKey)); } }
    private string preview = "";
    public string Preview { get => preview; set => Set(ref preview, value); }
    private bool renaming;
    [JsonIgnore] public bool Renaming { get => renaming; set => Set(ref renaming, value); }
    [JsonIgnore]
    public string DayKey
    {
        get
        {
            var date = UpdatedAt.LocalDateTime.Date;
            if (date == DateTime.Today) return "0";
            if (date == DateTime.Today.AddDays(-1)) return "1";
            return "2";
        }
    }
}
public sealed class CharacterCard
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "新角色";
    public string Description { get; set; } = "";
    public string SystemPrompt { get; set; } = "";
    public string Setting { get; set; } = "";
    public string Greeting { get; set; } = "";
    public string OpeningRole { get; set; } = "assistant";
    public ChatImage? Image { get; set; }
    [JsonIgnore] public string OpeningLabel => OpeningRole == "user" ? "我" : "对方";
    public override string ToString() => Name;
}
public sealed class AppSettings
{
    public string Theme { get; set; } = "light";
    public bool HistoryExpanded { get; set; } = true;
    public List<ServiceConfig> Services { get; set; } = [new()];
    public double Temperature { get; set; } = 0.7;
    public int MaxOutputTokens { get; set; } = 2048;
    public int ContextCharacters { get; set; } = 24000;
    public bool AutoDiscover { get; set; } = true;
    public string Reason { get; set; } = "off";
    public string SystemPrompt { get; set; } = "";
    public Dictionary<string, string> ParamDefaults { get; set; } = [];
}
