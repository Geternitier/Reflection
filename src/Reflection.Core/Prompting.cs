using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Reflection.Core;

public sealed record ParamField(string Key, string Label, string Hint, double Min, double Max, bool Integer, bool Lines, string Fallback);

public static class Prompting
{
    public const string ContinueCue = "请从刚才停下的地方继续写，不要重复已经写过的内容，也不要另起说明。";
    public const string HelpSystem = "你帮助用户起草下一条要发送的消息。只输出可直接发送的正文，不要加引号、标题或解释。使用用户正在使用的语言，语气自然。";
    public const string ImagePrompt = "请结合图片回答。";
    public const int MaxContextWindow = 1_048_576;
    public static readonly ParamField[] Fields =
    [
        new("temperature", "温度", "越高越发散", 0, 2, false, false, "0.8"),
        new("top_p", "Top P", "核采样", 0, 1, false, false, "0.95"),
        new("top_k", "Top K", "候选词数量。兼容接口超过 64 时按 64", 0, 200, true, false, "40"),
        new("min_p", "Min P", "最低概率", 0, 1, false, false, "0.05"),
        new("num_ctx", "上下文长度", "发给模型的上下文。填太大可能占满显存", 1, MaxContextWindow, true, false, ""),
        new("num_predict", "最长回复", "-1 表示不限制", -1, 131072, true, false, "-1"),
        new("repeat_penalty", "重复惩罚", "1 表示关闭", 0, 2, false, false, "1"),
        new("repeat_last_n", "惩罚窗口", "重复惩罚回看多少个 token", 0, 8192, true, false, "64"),
        new("presence_penalty", "存在惩罚", "越低越容易重复话题", -2, 2, false, false, "0"),
        new("frequency_penalty", "频率惩罚", "降低重复用词", -2, 2, false, false, "0"),
        new("seed", "种子", "相同种子更稳定", 0, 2147483647, true, false, ""),
        new("stop", "停止词", "每行一个，遇到就停下", 0, 0, false, true, "")
    ];

    public static string NormalizeReason(string? value) => value is "low" or "medium" or "high" ? value : "off";

    public static string ShownDefault(ParamField field, ModelInfo? info)
    {
        if (info?.Defaults.TryGetValue(field.Key, out var own) == true && !string.IsNullOrWhiteSpace(own))
            return field.Lines ? own : own.Split('\n')[0];
        if (field.Key == "num_ctx" && !string.IsNullOrWhiteSpace(info?.ContextLimit)) return info.ContextLimit;
        return field.Fallback;
    }

    public static Dictionary<string, string> Normalize(IReadOnlyDictionary<string, string>? values)
    {
        var result = new Dictionary<string, string>();
        if (values is null) return result;
        foreach (var field in Fields)
        {
            if (!values.TryGetValue(field.Key, out var raw) || string.IsNullOrWhiteSpace(raw)) continue;
            var text = raw.Trim();
            if (text.Length > (field.Lines ? 800 : 32)) text = text[..(field.Lines ? 800 : 32)];
            if (field.Lines)
            {
                var lines = text.Split('\n').Select(x => x.Trim()).Where(x => x.Length > 0).Take(16).ToArray();
                if (lines.Length > 0) result[field.Key] = string.Join('\n', lines);
                continue;
            }
            if (TryNumber(field, text, out var number)) result[field.Key] = Format(field, number);
        }
        return result;
    }

    public static Dictionary<string, string> Effective(AppSettings settings)
    {
        var map = Normalize(settings.ParamDefaults);
        if (!map.ContainsKey("temperature")) map["temperature"] = settings.Temperature.ToString("0.###", CultureInfo.InvariantCulture);
        if (!map.ContainsKey("num_predict")) map["num_predict"] = settings.MaxOutputTokens.ToString(CultureInfo.InvariantCulture);
        return map;
    }

    public static bool Same(ParamField field, string? left, string? right)
    {
        if (field.Lines) return string.Equals((left ?? "").Trim(), (right ?? "").Trim(), StringComparison.Ordinal);
        if (TryNumber(field, left, out var a) && TryNumber(field, right, out var b)) return a.Equals(b);
        return string.Equals((left ?? "").Trim(), (right ?? "").Trim(), StringComparison.Ordinal);
    }

    public static bool TryNumber(ParamField field, string? raw, out double number)
    {
        number = 0;
        var text = (raw ?? "").Trim();
        if (text.Length == 0) return false;
        if (field.Integer)
        {
            if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer)) return false;
            number = integer;
        }
        else if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out number) || !double.IsFinite(number)) return false;
        return number >= field.Min && number <= field.Max;
    }

    public static string Format(ParamField field, double number) => field.Integer
        ? ((int)number).ToString(CultureInfo.InvariantCulture)
        : number.ToString("0.###", CultureInfo.InvariantCulture);

    public static Dictionary<string, object> RequestOptions(AppSettings settings, ModelInfo? info, bool openAi)
    {
        var effective = Effective(settings);
        var options = new Dictionary<string, object>();
        foreach (var field in Fields)
        {
            if (!effective.TryGetValue(field.Key, out var value)) continue;
            var shown = ShownDefault(field, info);
            if (field.Key != "num_ctx" && shown.Length > 0 && Same(field, value, shown)) continue;
            if (field.Lines)
            {
                var stops = value.Split('\n').Select(x => x.Trim()).Where(x => x.Length > 0).Take(openAi ? 4 : 16).ToArray();
                if (stops.Length > 0) options["stop"] = stops;
                continue;
            }
            if (!TryNumber(field, value, out var number)) continue;
            if (field.Key == "top_k" && openAi && number > 64) number = 64;
            if (openAi && field.Key == "repeat_last_n" && number <= 0) continue;
            if (openAi && field.Key == "num_predict")
            {
                if (number > 0) options["max_tokens"] = (int)number;
                continue;
            }
            if (openAi && field.Key == "num_ctx")
            {
                if (number > 0) options["n_ctx"] = (int)number;
                continue;
            }
            options[field.Key] = field.Integer ? (int)number : number;
        }
        return options;
    }

    public static string ResolveSystem(CharacterCard? card, string? globalPrompt, string? modelPrompt, bool ollama)
    {
        var prompt = (card is not null ? card.SystemPrompt : globalPrompt) ?? "";
        prompt = prompt.Trim();
        var story = (card?.Setting ?? "").Trim();
        var legacy = card is not null ? (card.Description ?? "").Trim() : "";
        var body = prompt;
        if (body.Length == 0 && story.Length > 0 && ollama && !string.IsNullOrWhiteSpace(modelPrompt)) body = modelPrompt.Trim();
        if (story.Length > 0) body = body.Length == 0 ? "【故事设定】\n" + story : body + "\n\n【故事设定】\n" + story;
        if (legacy.Length > 0) body = body.Length == 0 ? legacy : body + "\n\n" + legacy;
        body = body.Replace("{{char}}", card?.Name ?? "助手").Replace("{{user}}", "用户");
        return body.Trim();
    }

    public static (string Content, string Thinking) SeparateThink(string raw, string streamedThinking)
    {
        var visible = new StringBuilder();
        var tagged = new StringBuilder();
        var index = 0;
        while (index < raw.Length)
        {
            var open = raw.IndexOf("<think>", index, StringComparison.OrdinalIgnoreCase);
            if (open < 0) { visible.Append(raw[index..]); break; }
            visible.Append(raw[index..open]);
            var close = raw.IndexOf("</think>", open + 7, StringComparison.OrdinalIgnoreCase);
            if (close < 0) { tagged.Append(raw[(open + 7)..]); break; }
            tagged.Append(raw.AsSpan(open + 7, close - open - 7));
            if (tagged.Length > 0 && tagged[^1] != '\n') tagged.Append('\n');
            index = close + 8;
            while (index < raw.Length && raw[index] is ' ' or '\r' or '\n') index++;
        }
        var thinking = streamedThinking.Trim();
        var extra = tagged.ToString().Trim();
        if (thinking.Length > 0 && extra.Length > 0) thinking = thinking + "\n\n" + extra;
        else if (extra.Length > 0) thinking = extra;
        return (visible.ToString().Trim(), thinking);
    }

    public static string JoinContinuation(string prefix, string extra)
    {
        if (string.IsNullOrEmpty(extra)) return prefix;
        if (prefix.Length > 0 && extra.StartsWith(prefix, StringComparison.Ordinal)) extra = extra[prefix.Length..];
        return prefix + extra;
    }

    public static string SystemFromShow(JsonElement root)
    {
        if (root.TryGetProperty("system", out var system) && system.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(system.GetString()))
            return system.GetString()!.Trim();
        if (!root.TryGetProperty("modelfile", out var file) || file.ValueKind != JsonValueKind.String) return "";
        var text = file.GetString() ?? "";
        var match = Regex.Match(text, "^SYSTEM\\s+\"\"\"([\\s\\S]*?)\"\"\"", RegexOptions.Multiline);
        if (!match.Success) match = Regex.Match(text, "^SYSTEM\\s+\"([\\s\\S]*?)\"", RegexOptions.Multiline);
        if (!match.Success) match = Regex.Match(text, "^SYSTEM\\s+(\\S(?:.*\\S)?)\\s*$", RegexOptions.Multiline);
        return match.Success ? match.Groups[1].Value.Trim() : "";
    }

    public static void ReadShow(JsonElement root, ModelInfo info)
    {
        info.SystemPrompt = SystemFromShow(root);
        info.Defaults = ParseParameters(root);
        if (root.TryGetProperty("model_info", out var modelInfo) && modelInfo.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in modelInfo.EnumerateObject())
            {
                if (!property.Name.EndsWith("context_length", StringComparison.OrdinalIgnoreCase)) continue;
                if (property.Value.TryGetDouble(out var length) && length > 0) { info.ContextLimit = ((int)length).ToString(CultureInfo.InvariantCulture); break; }
            }
        }
        if (!info.Defaults.ContainsKey("num_ctx") && info.ContextLimit.Length > 0) info.Defaults["num_ctx"] = info.ContextLimit;
        if (root.TryGetProperty("capabilities", out var caps) && caps.ValueKind == JsonValueKind.Array)
        {
            info.Capabilities = caps.EnumerateArray().Select(x => x.GetString()).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!).ToList();
            info.CapsChecked = info.Capabilities.Count > 0;
        }
        info.Inspected = true;
    }

    public static void ReadProps(JsonElement root, ModelInfo info)
    {
        if (root.TryGetProperty("default_generation_settings", out var settings) && settings.ValueKind == JsonValueKind.Object)
        {
            if (settings.TryGetProperty("params", out var parameters) && parameters.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in parameters.EnumerateObject())
                {
                    var key = property.Name.ToLowerInvariant();
                    if (Fields.All(x => x.Key != key)) continue;
                    info.Defaults[key] = property.Value.ToString().Trim().Trim('"');
                }
            }
            if (settings.TryGetProperty("n_ctx", out var ctx) && ctx.TryGetDouble(out var length) && length > 0)
                info.ContextLimit = ((int)length).ToString(CultureInfo.InvariantCulture);
        }
        info.Inspected = true;
    }

    private static Dictionary<string, string> ParseParameters(JsonElement root)
    {
        var values = new Dictionary<string, string>();
        var stops = new List<string>();
        void Take(string key, string raw)
        {
            key = key.ToLowerInvariant();
            if (key == "stop") { var stop = Unquote(raw); if (stop.Length > 0) stops.Add(stop); return; }
            if (Fields.All(x => x.Key != key)) return;
            values[key] = Unquote(raw);
        }
        var parameterText = root.TryGetProperty("parameters", out var parameters) && parameters.ValueKind == JsonValueKind.String ? parameters.GetString() ?? "" : "";
        if (parameterText.Trim().Length > 0)
        {
            foreach (var line in parameterText.Split('\n'))
            {
                var match = Regex.Match(line.Trim(), "^([A-Za-z0-9_]+)\\s+(.+)$");
                if (match.Success) Take(match.Groups[1].Value, match.Groups[2].Value);
            }
        }
        else if (root.TryGetProperty("modelfile", out var file) && file.ValueKind == JsonValueKind.String)
        {
            foreach (var line in (file.GetString() ?? "").Split('\n'))
            {
                var match = Regex.Match(line, "^\\s*PARAMETER\\s+(\\S+)\\s+(.+)$", RegexOptions.IgnoreCase);
                if (match.Success) Take(match.Groups[1].Value, match.Groups[2].Value);
            }
        }
        if (stops.Count > 0) values["stop"] = string.Join('\n', stops);
        return values;
    }

    private static string Unquote(string value)
    {
        var text = value.Trim();
        if (text.Length >= 2 && ((text[0] == '"' && text[^1] == '"') || (text[0] == '\'' && text[^1] == '\''))) return text[1..^1];
        return text;
    }
}
