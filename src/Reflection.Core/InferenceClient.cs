using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace Reflection.Core;

public readonly record struct StreamDelta(string Text, string Thinking);
public readonly record struct StreamChunk(string Text, string Thinking, bool Done);

public sealed class InferenceClient : IDisposable
{
    private readonly HttpClient http;
    public InferenceClient(HttpMessageHandler? handler = null)
    {
        http = handler is null ? new(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(3), PooledConnectionLifetime = TimeSpan.FromMinutes(5), MaxConnectionsPerServer = 4, AllowAutoRedirect = false, UseProxy = false }) : new(handler);
        http.Timeout = Timeout.InfiniteTimeSpan;
    }
    public static Uri Endpoint(ServiceConfig config, string path)
    {
        if (!Uri.TryCreate(config.BaseUrl.Trim().TrimEnd('/') + "/", UriKind.Absolute, out var uri)
            || (uri.Scheme != "http" && uri.Scheme != "https") || !uri.IsLoopback || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
            throw new ArgumentException("请输入本机 HTTP API 地址，例如 http://127.0.0.1:8080；不支持远程地址或内嵌凭据。");
        if (uri.AbsolutePath.TrimEnd('/').EndsWith("/v1", StringComparison.OrdinalIgnoreCase) && path.StartsWith("v1/")) path = path[3..];
        return new Uri(uri, path);
    }
    public async Task<ServiceStatus> DiscoverAsync(ServiceConfig service, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(4));
        try
        {
            var path = service.Protocol == ApiProtocol.Ollama ? "api/tags" : "v1/models";
            using var response = await http.GetAsync(Endpoint(service, path), HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: timeout.Token);
            var list = doc.RootElement.GetProperty(service.Protocol == ApiProtocol.Ollama ? "models" : "data");
            var models = list.EnumerateArray().Select(x => x.GetProperty(service.Protocol == ApiProtocol.Ollama ? "name" : "id").GetString())
                .Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().Select(x => new ModelOption(service, x!)).ToList();
            return new(service, models, "已连接");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new(service, [], "连接超时"); }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or ArgumentException or InvalidOperationException or KeyNotFoundException)
        { return new(service, [], ex is HttpRequestException ? "服务未启动或接口不可用" : "地址或模型列表格式不匹配"); }
    }
    public async Task<ModelInfo> InspectAsync(ModelOption model, CancellationToken ct)
    {
        var info = new ModelInfo();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(4));
        try
        {
            var ollama = model.Service.Protocol == ApiProtocol.Ollama;
            using var response = ollama
                ? await http.PostAsJsonAsync(Endpoint(model.Service, "api/show"), new { model = model.Model }, timeout.Token)
                : await http.GetAsync(new Uri(Endpoint(model.Service, "props").GetLeftPart(UriPartial.Authority) + "/props"), HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode) return info;
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: timeout.Token);
            if (ollama) Prompting.ReadShow(doc.RootElement, info); else Prompting.ReadProps(doc.RootElement, info);
            info.Inspected = true;
            return info;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or ArgumentException or InvalidOperationException or OperationCanceledException or KeyNotFoundException)
        { return info; }
    }
    public static int MessageWeight(ChatMessage message)
    {
        if (!string.IsNullOrWhiteSpace(message.Content)) return message.Content.Length;
        return message.Images.Count > 0 ? Prompting.ImagePrompt.Length : 0;
    }
    public static bool HasPayload(ChatMessage message) => MessageWeight(message) > 0;
    public static string OutboundText(ChatMessage message) => string.IsNullOrWhiteSpace(message.Content) && message.Images.Count > 0 ? Prompting.ImagePrompt : message.Content;
    public static List<ChatMessage> Trim(IEnumerable<ChatMessage> messages, string system, int characterBudget)
    {
        if (system.Length >= characterBudget) throw new ArgumentException("角色提示词超过上下文预算，请缩短提示词或调高预算。");
        var remaining = characterBudget - system.Length;
        var history = messages.Where(x => !x.Incomplete && !x.Stopped && !x.HasError && HasPayload(x)).ToList();
        var selected = new List<ChatMessage>();
        for (int i = history.Count - 1; i >= 0; i--)
        {
            var weight = MessageWeight(history[i]);
            if (weight > remaining)
            {
                if (selected.Count == 0) throw new ArgumentException("最新消息超过上下文预算，请缩短消息。");
                break;
            }
            selected.Add(history[i]); remaining -= weight;
        }
        selected.Reverse();
        // A character's opening is intentional history, unlike an orphaned reply after trimming.
        while (selected.Count > 0 && selected[0].Role != "user" && !(selected[0].Opening && selected[0].Role == "assistant")) selected.RemoveAt(0);
        return selected;
    }
    public static List<object> BuildMessages(IEnumerable<ChatMessage> messages, string system, int characterBudget)
    {
        var result = new List<object>();
        if (!string.IsNullOrWhiteSpace(system)) result.Add(new { role = "system", content = system });
        result.AddRange(Trim(messages, system, characterBudget).Select(x => (object)ApiMessage(x, false)));
        return result;
    }
    public static object ApiMessage(ChatMessage message, bool openAi)
    {
        var content = OutboundText(message);
        if (message.Images.Count == 0 || message.Role != "user") return new { role = message.Role, content };
        if (!openAi) return new { role = message.Role, content, images = message.Images.Select(x => x.Data).ToArray() };
        var parts = new List<object> { new { type = "text", text = content } };
        parts.AddRange(message.Images.Select(x => (object)new { type = "image_url", image_url = new { url = $"data:{x.Mime};base64,{x.Data}" } }));
        return new { role = message.Role, content = parts };
    }
    public static string CreateBody(ModelOption model, IEnumerable<ChatMessage> messages, string system, AppSettings settings, ModelInfo? info = null, string? extraUser = null)
    {
        var ollama = model.Service.Protocol == ApiProtocol.Ollama;
        var list = new List<object>();
        if (!string.IsNullOrWhiteSpace(system)) list.Add(new { role = "system", content = system });
        var selected = Trim(messages, system, settings.ContextCharacters - (extraUser?.Length ?? 0));
        var images = selected.SelectMany(x => x.Images).ToArray();
        if (images.Length > 16 || images.Sum(x => (long)x.Data.Length) > 12_000_000)
            throw new ArgumentException("本轮图片上下文过大，请新建对话或减少图片（最多 16 张、12 MB 编码数据）。");
        list.AddRange(selected.Select(x => ApiMessage(x, !ollama)));
        if (!string.IsNullOrWhiteSpace(extraUser)) list.Add(new { role = "user", content = extraUser });
        var payload = new Dictionary<string, object?> { ["model"] = model.Model, ["messages"] = list, ["stream"] = true };
        var options = Prompting.RequestOptions(settings, info, !ollama);
        var reason = Prompting.NormalizeReason(settings.Reason);
        if (ollama)
        {
            if (options.Count > 0) payload["options"] = options;
            payload["keep_alive"] = "5m";
            payload["think"] = reason == "off" ? false : reason;
        }
        else
        {
            foreach (var pair in options) payload[pair.Key] = pair.Value;
            if (reason != "off") payload["reasoning_effort"] = reason;
        }
        return JsonSerializer.Serialize(payload);
    }
    public async IAsyncEnumerable<StreamDelta> StreamAsync(ModelOption model, IEnumerable<ChatMessage> messages, string system, AppSettings settings, [EnumeratorCancellation] CancellationToken ct, ModelInfo? info = null, string? extraUser = null)
    {
        var ollama = model.Service.Protocol == ApiProtocol.Ollama;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromMinutes(10));
        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint(model.Service, ollama ? "api/chat" : "v1/chat/completions"))
        { Content = new StringContent(CreateBody(model, messages, system, settings, info, extraUser), Encoding.UTF8, "application/json") };
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"服务返回 HTTP {(int)response.StatusCode}（{response.ReasonPhrase}）。请检查模型是否加载及服务配置。");
        await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
        using var reader = new StreamReader(stream);
        var lines = new BoundedLineReader(reader, 1_048_576);
        var received = 0;
        void CheckLimit(StreamChunk chunk) { received += chunk.Text.Length + chunk.Thinking.Length; if (received > 500_000) throw new IOException("回复与思考内容合计已达到 50 万字符上限。"); }
        var eventData = new StringBuilder();
        var completed = false;
        while (true)
        {
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token); idle.CancelAfter(TimeSpan.FromSeconds(90));
            var line = await lines.ReadAsync(idle.Token);
            if (line is null)
            {
                if (eventData.Length > 0)
                {
                    var last = ParseChunk(eventData.ToString(), false); completed |= last.Done; CheckLimit(last);
                    if (last.Text.Length > 0 || last.Thinking.Length > 0) yield return new(last.Text, last.Thinking);
                }
                break;
            }
            if (line.Length > 1_048_576) throw new IOException("服务返回的数据块过大。");
            if (ollama)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var chunk = ParseChunk(line, true); completed |= chunk.Done; CheckLimit(chunk);
                if (chunk.Text.Length > 0 || chunk.Thinking.Length > 0) yield return new(chunk.Text, chunk.Thinking);
                if (completed) break;
            }
            else if (line.Length == 0 && eventData.Length > 0)
            {
                var chunk = ParseChunk(eventData.ToString(), false); eventData.Clear(); completed |= chunk.Done; CheckLimit(chunk);
                if (chunk.Text.Length > 0 || chunk.Thinking.Length > 0) yield return new(chunk.Text, chunk.Thinking);
                if (completed) break;
            }
            else if (line.StartsWith("data:"))
            {
                if (eventData.Length > 0) eventData.Append('\n');
                eventData.Append(line[5..].TrimStart());
                if (eventData.Length > 1_048_576) throw new IOException("服务返回的数据块过大。");
            }
        }
        if (!completed) throw new IOException("连接提前中断，已保留部分回复，可重新生成。");
    }
    public static StreamChunk ParseChunk(string data, bool ollama)
    {
        if (data.Trim() == "[DONE]") return new("", "", true);
        using var doc = JsonDocument.Parse(data); var root = doc.RootElement;
        if (root.TryGetProperty("error", out var error)) throw new IOException("推理服务错误：" + error.ToString());
        if (ollama)
        {
            var text = ""; var thinking = "";
            if (root.TryGetProperty("message", out var msg))
            {
                if (msg.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String) text = content.GetString() ?? "";
                if (msg.TryGetProperty("thinking", out var thought) && thought.ValueKind == JsonValueKind.String) thinking = thought.GetString() ?? "";
            }
            return new(text, thinking, root.TryGetProperty("done", out var done) && done.ValueKind == JsonValueKind.True);
        }
        if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0) return new("", "", false);
        var choice = choices[0];
        var value = ""; var reasonText = "";
        if (choice.TryGetProperty("delta", out var delta))
        {
            if (delta.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String) value = c.GetString() ?? "";
            if (delta.TryGetProperty("reasoning_content", out var reasoning) && reasoning.ValueKind == JsonValueKind.String) reasonText = reasoning.GetString() ?? "";
            else if (delta.TryGetProperty("reasoning", out var alternate) && alternate.ValueKind == JsonValueKind.String) reasonText = alternate.GetString() ?? "";
        }
        return new(value, reasonText, choice.TryGetProperty("finish_reason", out var reason) && reason.ValueKind == JsonValueKind.String);
    }
    public void Dispose() => http.Dispose();
}
