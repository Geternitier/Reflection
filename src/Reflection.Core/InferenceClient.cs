using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace Reflection.Core;

public sealed record GenerationMetrics(long? ContextTokens, int? ContextLimit, double? TokensPerSecond, bool EstimatedSpeed);
public readonly record struct StreamDelta(string Text, string Thinking, GenerationMetrics? Metrics = null);
public readonly record struct StreamChunk(string Text, string Thinking, bool Done)
{
    internal bool FinishedChoice { get; init; }
    internal TokenUsage Usage { get; init; }
}

internal readonly record struct TokenUsage(long? Prompt, long? Output, long? Total, double? Rate)
{
    public TokenUsage Merge(TokenUsage next) => new(next.Prompt ?? Prompt, next.Output ?? Output, next.Total ?? Total, next.Rate ?? Rate);
    public long? ContextTokens => Total ?? (Prompt is { } prompt && Output is { } output ? prompt + output : null);
}

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
        var selected = ImageContext.Select(messages, system, settings.ContextCharacters - (extraUser?.Length ?? 0)).Messages;
        if (selected.Where(x => x.Role == "user").SelectMany(x => x.Images).Any(x => string.IsNullOrEmpty(x.Data)))
            throw new ArgumentException("图片资产需要通过异步请求入口读取。");
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
    public static async Task<string> CreateBodyAsync(ModelOption model, IEnumerable<ChatMessage> messages, string system, AppSettings settings, ModelInfo? info = null, string? extraUser = null, CancellationToken ct = default, IProgress<ImageContextSelection>? progress = null)
    {
        var snapshot = ConversationOperations.Snapshot(new Conversation { Messages = new(messages) }).Messages;
        var selected = await Task.Run(() => ImageContext.Select(snapshot, system, settings.ContextCharacters - (extraUser?.Length ?? 0)), ct).ConfigureAwait(false);
        progress?.Report(selected);
        var hydrated = await ImageContext.MaterializeAsync(selected, ct).ConfigureAwait(false);
        return await Task.Run(() => CreateBody(model, hydrated, system, settings, info, extraUser), ct).ConfigureAwait(false);
    }
    public async IAsyncEnumerable<StreamDelta> StreamAsync(ModelOption model, IEnumerable<ChatMessage> messages, string system, AppSettings settings, [EnumeratorCancellation] CancellationToken ct, ModelInfo? info = null, string? extraUser = null, IProgress<ImageContextSelection>? progress = null, bool collectMetrics = false)
    {
        var ollama = model.Service.Protocol == ApiProtocol.Ollama;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromMinutes(10));
        var endpoint = Endpoint(model.Service, ollama ? "api/chat" : "v1/chat/completions");
        var body = await CreateBodyAsync(model, messages, system, settings, info, extraUser, deadline.Token, progress);
        var clock = Stopwatch.StartNew();
        using var response = await SendChatAsync(endpoint, body, !ollama && collectMetrics, clock, deadline.Token);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"服务返回 HTTP {(int)response.StatusCode}（{response.ReasonPhrase}）。请检查模型是否加载及服务配置。");
        await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
        using var reader = new StreamReader(stream);
        var lines = new BoundedLineReader(reader, 1_048_576);
        var received = 0;
        var eventData = new StringBuilder();
        using var usageTail = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        var usage = default(TokenUsage);
        TimeSpan? finishedAt = null;
        var completed = false;
        while (true)
        {
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(usageTail.Token); idle.CancelAfter(TimeSpan.FromSeconds(90));
            string? line;
            try { line = await lines.ReadAsync(idle.Token); }
            catch (OperationCanceledException) when (finishedAt.HasValue && usageTail.IsCancellationRequested && !deadline.IsCancellationRequested)
            { break; }
            catch (IOException) when (finishedAt.HasValue && !deadline.IsCancellationRequested) { break; }
            if (line is { Length: > 1_048_576 }) throw new IOException("服务返回的数据块过大。");
            StreamChunk chunk;
            if (ollama)
            {
                if (line is null) break;
                if (string.IsNullOrWhiteSpace(line)) continue;
                chunk = ParseChunk(line, true);
            }
            else if (string.IsNullOrEmpty(line))
            {
                if (eventData.Length == 0) { if (line is null) break; continue; }
                try { chunk = ParseChunk(eventData.ToString(), false); }
                catch (Exception ex) when (finishedAt.HasValue && ex is JsonException or IOException) { break; }
                eventData.Clear();
            }
            else
            {
                if (line.StartsWith("data:", StringComparison.Ordinal))
                {
                    if (eventData.Length > 0) eventData.Append('\n');
                    eventData.Append(line.AsSpan(5).TrimStart());
                    if (eventData.Length > 1_048_576) throw new IOException("服务返回的数据块过大。");
                }
                continue;
            }

            received += chunk.Text.Length + chunk.Thinking.Length;
            if (received > 500_000) throw new IOException("回复与思考内容合计已达到 50 万字符上限。");
            usage = usage.Merge(chunk.Usage);
            completed |= chunk.Done;
            if (completed && !finishedAt.HasValue)
            {
                finishedAt = clock.Elapsed;
                // OpenAI sends the optional usage event after finish_reason. Older services may stop there.
                if (chunk.FinishedChoice && collectMetrics) usageTail.CancelAfter(TimeSpan.FromSeconds(1));
            }
            if (chunk.Text.Length > 0 || chunk.Thinking.Length > 0) yield return new(chunk.Text, chunk.Thinking);
            if (line is null || chunk.Done && (!chunk.FinishedChoice || !collectMetrics)
                || completed && usage.ContextTokens.HasValue && usage.Output.HasValue) break;
        }
        if (!completed) throw new IOException("连接提前中断，已保留部分回复，可重新生成。");
        if (!collectMetrics) yield break;
        // Release the chat connection before querying a runner on the same service.
        response.Dispose();
        deadline.Token.ThrowIfCancellationRequested();
        var rate = usage.Rate;
        if (rate is null && usage.Output is > 0 && finishedAt is { TotalSeconds: > 0 } elapsed)
            rate = usage.Output.Value / elapsed.TotalSeconds;
        var tokens = usage.ContextTokens;
        if (tokens.HasValue || rate.HasValue)
        {
            var limit = !tokens.HasValue ? null : ollama
                ? await ReadRunningContextAsync(model, deadline.Token)
                : ReadContextLimit(info ?? model.Info);
            deadline.Token.ThrowIfCancellationRequested();
            yield return new("", "", new(tokens, limit, rate, usage.Rate is null && rate.HasValue));
        }
    }

    private async Task<HttpResponseMessage> SendChatAsync(Uri endpoint, string body, bool includeUsage, Stopwatch clock, CancellationToken ct)
    {
        async Task<HttpResponseMessage> Send(string json)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
            clock.Restart();
            return await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        }

        if (!includeUsage) return await Send(body);
        var response = await Send(string.Concat(body.AsSpan(0, body.Length - 1), ",\"stream_options\":{\"include_usage\":true}}"));
        if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMilliseconds(750));
            var rejectedUsage = false;
            try
            {
                var error = Encoding.UTF8.GetString(await ReadBoundedAsync(response, 8192, timeout.Token));
                rejectedUsage = error.Contains("stream_options", StringComparison.OrdinalIgnoreCase) || error.Contains("include_usage", StringComparison.OrdinalIgnoreCase);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
            catch (IOException) { }
            catch { response.Dispose(); throw; }
            if (rejectedUsage)
            {
                response.Dispose();
                return await Send(body);
            }
        }
        return response;
    }

    private async Task<int?> ReadRunningContextAsync(ModelOption model, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(750));
        try
        {
            using var response = await http.GetAsync(Endpoint(model.Service, "api/ps"), HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await ReadBoundedAsync(response, 65536, timeout.Token));
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("models", out var models)
                || models.ValueKind != JsonValueKind.Array) return null;
            var name = model.Model;
            var tagged = name.LastIndexOf(':') > name.LastIndexOf('/') ? name : name + ":latest";
            foreach (var item in models.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                foreach (var key in new[] { "name", "model" })
                    if (item.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String && (value.GetString() == name || value.GetString() == tagged)
                        && Count(item, "context_length") is > 0 and <= int.MaxValue)
                        return item.GetProperty("context_length").GetInt32();
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException) { }
        return null;
    }

    private static int? ReadContextLimit(ModelInfo info) =>
        int.TryParse(info.ContextLimit, NumberStyles.None, CultureInfo.InvariantCulture, out var limit) && limit > 0 ? limit : null;

    private static async Task<byte[]> ReadBoundedAsync(HttpResponseMessage response, int limit, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var buffer = new byte[limit];
        var size = await stream.ReadAtLeastAsync(buffer, limit, false, ct);
        return size == limit ? buffer : buffer[..size];
    }

    private static long? Count(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)
            && number is >= 0 and <= int.MaxValue ? number : null;

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
            var output = Count(root, "eval_count");
            var rate = output is > 0 && root.TryGetProperty("eval_duration", out var duration) && duration.ValueKind == JsonValueKind.Number
                && duration.TryGetInt64(out var nanos) && nanos > 0 ? output * 1_000_000_000d / nanos : null;
            return new(text, thinking, root.TryGetProperty("done", out var done) && done.ValueKind == JsonValueKind.True)
            { Usage = new(Count(root, "prompt_eval_count"), output, null, rate) };
        }
        var usage = root.TryGetProperty("usage", out var counts) && counts.ValueKind == JsonValueKind.Object
            ? new TokenUsage(Count(counts, "prompt_tokens"), Count(counts, "completion_tokens"), Count(counts, "total_tokens"), null) : default;
        if (root.TryGetProperty("timings", out var timings) && timings.ValueKind == JsonValueKind.Object
            && timings.TryGetProperty("predicted_per_second", out var speed) && speed.ValueKind == JsonValueKind.Number
            && speed.TryGetDouble(out var perSecond) && double.IsFinite(perSecond) && perSecond > 0) usage = usage with { Rate = perSecond };
        if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
            return new("", "", false) { Usage = usage };
        var choice = choices[0];
        var value = ""; var reasonText = "";
        if (choice.TryGetProperty("delta", out var delta))
        {
            if (delta.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String) value = c.GetString() ?? "";
            if (delta.TryGetProperty("reasoning_content", out var reasoning) && reasoning.ValueKind == JsonValueKind.String) reasonText = reasoning.GetString() ?? "";
            else if (delta.TryGetProperty("reasoning", out var alternate) && alternate.ValueKind == JsonValueKind.String) reasonText = alternate.GetString() ?? "";
        }
        var finishedChoice = choice.TryGetProperty("finish_reason", out var reason) && reason.ValueKind == JsonValueKind.String;
        return new(value, reasonText, finishedChoice) { FinishedChoice = finishedChoice, Usage = usage };
    }
    public void Dispose() => http.Dispose();
}
