using System.Text.Json;

namespace Reflection.Core;

public static class CharacterExchange
{
    public static CharacterCard Import(string json)
    {
        if (json.Length > 8_000_000) throw new ArgumentException("角色卡不能超过 8 MB。");
        using var document = JsonDocument.Parse(json); var root = document.RootElement;
        var data = root.TryGetProperty("data", out var nested) ? nested : root;
        string Get(string key) => data.TryGetProperty(key, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() ?? "" : "";
        var name = Get("name");
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("角色卡缺少 name 字段。");
        var prompt = Get("systemPrompt");
        if (prompt.Length == 0) prompt = Get("system_prompt");
        prompt = string.Join("\n\n", new[] { prompt, Get("personality"), Get("mes_example") }.Where(x => x.Length > 0));
        var openingRole = "assistant";
        ChatImage? image = null, avatar = null;
        if (data.TryGetProperty("extensions", out var extensions) && extensions.ValueKind == JsonValueKind.Object
            && extensions.TryGetProperty("reflection", out var reflection) && reflection.ValueKind == JsonValueKind.Object)
        {
            if (reflection.TryGetProperty("opening_role", out var role) && role.ValueKind == JsonValueKind.String && role.GetString() == "user") openingRole = "user";
            image = ReadImage(reflection, "image");
            avatar = ReadImage(reflection, "avatar");
        }
        return new()
        {
            Name = name, Description = Get("description"), SystemPrompt = prompt, Setting = Get("scenario"),
            Greeting = Get("first_mes") is { Length: > 0 } greeting ? greeting : Get("greeting"), OpeningRole = openingRole, Image = image, Avatar = avatar
        };
    }
    private static ChatImage? ReadImage(JsonElement extension, string key)
    {
        if (!extension.TryGetProperty(key, out var picture) || picture.ValueKind != JsonValueKind.Object
            || !picture.TryGetProperty("data", out var raw) || raw.ValueKind != JsonValueKind.String
            || raw.GetString() is not { } data || string.IsNullOrWhiteSpace(data)) return null;
        return new() { Mime = picture.TryGetProperty("mime", out var mime) && mime.ValueKind == JsonValueKind.String ? mime.GetString() ?? "image/jpeg" : "image/jpeg", Data = data };
    }
    public static async Task<string> ExportAsync(CharacterCard card, CancellationToken ct = default)
    {
        var portable = ConversationOperations.Snapshot(card)!;
        portable.Image = await MaterializeAsync(portable.Image, ct).ConfigureAwait(false);
        portable.Avatar = await MaterializeAsync(portable.Avatar, ct).ConfigureAwait(false);
        return await Task.Run(() => Export(portable), ct).ConfigureAwait(false);
    }
    private static async Task<ChatImage?> MaterializeAsync(ChatImage? image, CancellationToken ct) =>
        image?.AssetId is { } id ? new()
        {
            Mime = image.Mime,
            Data = await (image.Store ?? throw new IOException("图片未绑定工作区。")).Base64Async(id, ct).ConfigureAwait(false)
        } : image;
    public static string Export(CharacterCard card)
    {
        static object? PortableImage(ChatImage? image) => image switch
        {
            null => null,
            { AssetId: not null, Data.Length: 0 } => throw new ArgumentException("图片资产导出需要使用异步入口。"),
            _ => new { mime = image.Mime, data = image.Data }
        };
        return JsonSerializer.Serialize(new
        {
            spec = "chara_card_v2", spec_version = "2.0", data = new
            {
                name = card.Name, description = card.Description, personality = "", scenario = card.Setting ?? "", first_mes = card.Greeting,
                mes_example = "", system_prompt = card.SystemPrompt, post_history_instructions = "", creator_notes = "",
                alternate_greetings = Array.Empty<string>(), tags = Array.Empty<string>(), creator = "Reflection", character_version = "1.0",
                extensions = new { reflection = new { opening_role = card.OpeningRole == "user" ? "user" : "assistant", image = PortableImage(card.Image), avatar = PortableImage(card.Avatar) } }
            }
        }, LocalStore.Json);
    }
}
