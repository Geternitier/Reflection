using System.Text.Json;

namespace Reflection.Core;

// Request snapshots must never share mutable generation state with the displayed message.
public static class ConversationOperations
{
    public static CharacterCard? Snapshot(CharacterCard? card) => card is null ? null : new()
    {
        Id = card.Id, Name = card.Name, Description = card.Description, SystemPrompt = card.SystemPrompt,
        Setting = card.Setting, Greeting = card.Greeting, OpeningRole = card.OpeningRole,
        Image = card.Image is null ? null : new() { Mime = card.Image.Mime, Data = card.Image.Data }
    };

    public static bool RefreshCharacter(Conversation chat, CharacterCard card)
    {
        if (chat.CharacterId != card.Id) return false;
        var updated = Snapshot(card)!;
        if (chat.CharacterSnapshot is { } previous)
        {
            updated.Greeting = previous.Greeting;
            updated.OpeningRole = previous.OpeningRole;
        }
        chat.CharacterSnapshot = updated;
        return true;
    }

    public static Conversation Snapshot(Conversation chat) => new()
    {
        Id = chat.Id, Title = chat.Title, CharacterId = chat.CharacterId, CharacterSnapshot = Snapshot(chat.CharacterSnapshot),
        ServiceId = chat.ServiceId, Model = chat.Model, UpdatedAt = chat.UpdatedAt, Draft = chat.Draft,
        Attachments = new(chat.Attachments.Select(x => new ChatImage { Mime = x.Mime, Data = x.Data })),
        Messages = new(chat.Messages.Select(x => new ChatMessage
        {
            Role = x.Role, Content = x.Content, Thinking = x.Thinking, CreatedAt = x.CreatedAt,
            Incomplete = x.Incomplete, Opening = x.Opening, Error = x.Error, Stopped = x.Stopped,
            Images = new(x.Images.Select(image => new ChatImage { Mime = image.Mime, Data = image.Data }))
        }))
    };

    public static ChatMessage[] Continuation(IEnumerable<ChatMessage> messages, ChatMessage target) =>
        messages.Select(x => ReferenceEquals(x, target) ? new ChatMessage
        {
            Role = x.Role, Content = x.Content, CreatedAt = x.CreatedAt,
            Images = new(x.Images), Opening = x.Opening
        } : x).ToArray();

    public static void ReplaceUserMessage(Conversation chat, ChatMessage message, string text)
    {
        var index = chat.Messages.IndexOf(message);
        if (index < 0) throw new ArgumentException("消息已不在当前对话中。");
        var replacement = new ChatMessage { Role = "user", Content = text.Trim(), Images = new(message.Images) };
        if (!InferenceClient.HasPayload(replacement)) throw new ArgumentException("消息不能为空。");
        while (chat.Messages.Count > index) chat.Messages.RemoveAt(chat.Messages.Count - 1);
        chat.Messages.Add(replacement);
    }
}

internal sealed class BoundedLineReader(StreamReader reader, int limit)
{
    private readonly char[] buffer = new char[4096];
    private int offset, count;
    public async Task<string?> ReadAsync(CancellationToken ct)
    {
        var line = new System.Text.StringBuilder();
        while (true)
        {
            if (offset == count) { count = await reader.ReadAsync(buffer.AsMemory(), ct); offset = 0; }
            if (count == 0) return line.Length == 0 ? null : line.ToString().TrimEnd('\r');
            var end = Array.IndexOf(buffer, '\n', offset, count - offset);
            var length = (end < 0 ? count : end) - offset;
            if (line.Length + length > limit) throw new IOException("服务返回的数据行过大。");
            line.Append(buffer, offset, length); offset += length;
            if (end >= 0) { offset++; return line.ToString().TrimEnd('\r'); }
        }
    }
}
