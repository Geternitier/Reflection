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
        ChatImage? image = null;
        if (data.TryGetProperty("extensions", out var extensions) && extensions.ValueKind == JsonValueKind.Object && extensions.TryGetProperty("reflection", out var reflection))
        {
            if (reflection.TryGetProperty("opening_role", out var role) && role.ValueKind == JsonValueKind.String && role.GetString() == "user") openingRole = "user";
            if (reflection.TryGetProperty("image", out var picture) && picture.ValueKind == JsonValueKind.Object && picture.TryGetProperty("data", out var raw) && raw.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(raw.GetString()))
                image = new() { Mime = picture.TryGetProperty("mime", out var mime) && mime.ValueKind == JsonValueKind.String ? mime.GetString() ?? "image/jpeg" : "image/jpeg", Data = raw.GetString()! };
        }
        return new()
        {
            Name = name, Description = Get("description"), SystemPrompt = prompt, Setting = Get("scenario"),
            Greeting = Get("first_mes") is { Length: > 0 } greeting ? greeting : Get("greeting"), OpeningRole = openingRole, Image = image
        };
    }
    public static string Export(CharacterCard card) => JsonSerializer.Serialize(new
    {
        spec = "chara_card_v2", spec_version = "2.0", data = new
        {
            name = card.Name, description = card.Description, personality = "", scenario = card.Setting ?? "", first_mes = card.Greeting,
            mes_example = "", system_prompt = card.SystemPrompt, post_history_instructions = "", creator_notes = "",
            alternate_greetings = Array.Empty<string>(), tags = Array.Empty<string>(), creator = "Reflection", character_version = "1.0",
            extensions = new { reflection = new { opening_role = card.OpeningRole == "user" ? "user" : "assistant", image = card.Image } }
        }
    }, LocalStore.Json);
}
