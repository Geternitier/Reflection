using Markdig.Extensions.Alerts;
using Markdig.Extensions.DefinitionLists;
using Markdig.Extensions.Footnotes;
using Markdig.Extensions.Mathematics;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using System.Net;
using System.Text;
using System.Text.Json;
using Reflection.Core;
using Reflection.Desktop;

int passed = 0;
async Task Test(string name, Func<Task> test) { await test(); Console.WriteLine("PASS " + name); passed++; }
void Check(bool value, string message = "Assertion failed") { if (!value) throw new Exception(message); }
async Task Throws<T>(Func<Task> action) where T : Exception { try { await action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
ServiceConfig OpenAI() => new() { Name = "ninfer", BaseUrl = "http://127.0.0.1:8080/v1", Protocol = ApiProtocol.OpenAI };
async Task<string> Collect(InferenceClient client, ServiceConfig config, CancellationToken token = default)
{
    var result = new StringBuilder();
    await foreach (var delta in client.StreamAsync(new(config, "test"), [new() { Content = "你好" }], "system", new(), token)) result.Append(delta.Text);
    return result.ToString();
}
async Task<List<StreamDelta>> CollectMeasured(InferenceClient client, ModelOption model)
{
    var deltas = new List<StreamDelta>();
    await foreach (var delta in client.StreamAsync(model, [new() { Content = "你好" }], "", new(), default, collectMetrics: true)) deltas.Add(delta);
    return deltas;
}
await Test("Ollama discovery", async () =>
{
    using var client = new InferenceClient(new Stub(async r => { Check(r.RequestUri!.AbsolutePath == "/api/tags"); await Task.Yield(); return Json("{\"models\":[{\"name\":\"qwen\"},{\"name\":\"qwen\"}]}"); }));
    var result = await client.DiscoverAsync(new(), default); Check(result.Online && result.Models.Count == 1);
});
await Test("OpenAI discovery and /v1 normalization", async () =>
{
    using var client = new InferenceClient(new Stub(r => { Check(r.RequestUri!.AbsolutePath == "/v1/models"); return Task.FromResult(Json("{\"data\":[{\"id\":\"local-model\"}]}")); }));
    var result = await client.DiscoverAsync(OpenAI(), default); Check(result.Online && result.Models[0].Model == "local-model");
});
await Test("Unavailable service", async () => { using var c = new InferenceClient(new Stub(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)))); Check(!(await c.DiscoverAsync(new(), default)).Online); });
await Test("Invalid model payload", async () => { using var c = new InferenceClient(new Stub(_ => Task.FromResult(Json("{}")))); Check(!(await c.DiscoverAsync(new(), default)).Online); });
await Test("Loopback-only addresses", async () =>
{
    foreach (var address in new[] { "https://example.com", "file:///tmp", "http://localhost@evil.test", "http://127.0.0.1?key=secret" })
        await Throws<ArgumentException>(() => { InferenceClient.Endpoint(new() { BaseUrl = address }, "api/tags"); return Task.CompletedTask; });
    Check(InferenceClient.Endpoint(new() { BaseUrl = "http://[::1]:11434" }, "api/tags").IsLoopback);
});
await Test("Fragmented UTF-8 SSE, comments, blank usage", async () =>
{
    const string sse = ": ping\r\ndata: {\"choices\":[{\"delta\":{\"content\":\"你好\"}}]}\r\n\r\ndata: {\"choices\":[]}\n\ndata: {\"choices\":[{\"delta\":{\"content\":\"，世界\"}}]}\n\ndata: [DONE]\n\n";
    using var c = new InferenceClient(new Stub(async r => { Check(r.RequestUri!.AbsolutePath == "/v1/chat/completions"); using var body = JsonDocument.Parse(await r.Content!.ReadAsStringAsync()); Check(body.RootElement.GetProperty("stream").GetBoolean()); Check(body.RootElement.GetProperty("messages")[0].GetProperty("role").GetString() == "system"); return Stream(sse); }));
    Check(await Collect(c, OpenAI()) == "你好，世界");
});
await Test("Ollama NDJSON and request options", async () =>
{
    using var c = new InferenceClient(new Stub(async r => { Check(r.RequestUri!.AbsolutePath == "/api/chat"); using var body = JsonDocument.Parse(await r.Content!.ReadAsStringAsync()); Check(body.RootElement.GetProperty("options").GetProperty("num_predict").GetInt32() == 2048); return Stream("{\"message\":{\"content\":\"你好\"},\"done\":false}\n{\"message\":{\"content\":\"！\"},\"done\":true}\n"); }));
    Check(await Collect(c, new()) == "你好！");
});
await Test("Ollama metrics use decode time and the running model's context capacity", async () =>
{
    using var client = new InferenceClient(new Stub(r => Task.FromResult(r.RequestUri!.AbsolutePath switch
    {
        "/api/chat" => Stream("{\"message\":{\"content\":\"ok\"},\"done\":true,\"prompt_eval_count\":1000,\"eval_count\":24,\"eval_duration\":500000000}\n"),
        "/api/ps" => Json("{\"models\":[{\"name\":\"other\",\"context_length\":2048},{\"name\":\"test:latest\",\"context_length\":4096}]}"),
        _ => throw new Exception("Unexpected metrics endpoint")
    })));
    var model = new ModelOption(new(), "test");
    model.Info.ContextLimit = "131072"; // Architecture maximum is unrelated to the loaded runner.
    var deltas = await CollectMeasured(client, model);
    Check(string.Concat(deltas.Select(delta => delta.Text)) == "ok");
    Check(deltas.Last().Metrics == new GenerationMetrics(1024, 4096, 48, false));
});
await Test("OpenAI usage after finish_reason supplies the count and an estimated rate", async () =>
{
    using var client = new InferenceClient(new Stub(async r =>
    {
        using var json = JsonDocument.Parse(await r.Content!.ReadAsStringAsync());
        Check(json.RootElement.GetProperty("stream_options").GetProperty("include_usage").GetBoolean());
        return Stream("data: {\"choices\":[{\"delta\":{\"content\":\"ok\"},\"finish_reason\":\"stop\"}]}\n\n"
            + "data: {\"choices\":[],\"usage\":{\"prompt_tokens\":1000,\"completion_tokens\":24,\"total_tokens\":1024}}\n\n"
            + "data: [DONE]\n\n");
    }));
    var deltas = await CollectMeasured(client, new(OpenAI(), "test"));
    Check(string.Concat(deltas.Select(delta => delta.Text)) == "ok");
    Check(deltas.Last().Metrics is { ContextTokens: 1024, ContextLimit: null, TokensPerSecond: > 0, EstimatedSpeed: true });
});
await Test("A compatible server rejecting usage options still returns the original completion", async () =>
{
    var calls = 0;
    using var client = new InferenceClient(new Stub(async r =>
    {
        using var json = JsonDocument.Parse(await r.Content!.ReadAsStringAsync());
        Check(json.RootElement.TryGetProperty("stream_options", out _) == (++calls == 1));
        return calls == 1 ? new(HttpStatusCode.UnprocessableEntity) { Content = new StringContent("unknown field stream_options") }
            : Stream("data: {\"choices\":[{\"delta\":{\"content\":\"ok\"},\"finish_reason\":\"stop\"}]}\n\n");
    }));
    var deltas = await CollectMeasured(client, new(OpenAI(), "test"));
    Check(calls == 2 && deltas is [{ Text: "ok", Metrics: null }]);
});
await Test("SSE multiline data", async () =>
{
    using var c = new InferenceClient(new Stub(_ => Task.FromResult(Stream("data: {\"choices\":\ndata: [{\"delta\":{\"content\":\"ok\"},\"finish_reason\":\"stop\"}]}\n\n")))); Check(await Collect(c, OpenAI()) == "ok");
});
await Test("Premature EOF is an error", async () => { using var c = new InferenceClient(new Stub(_ => Task.FromResult(Stream("data: {\"choices\":[{\"delta\":{\"content\":\"partial\"}}]}\n\n")))); await Throws<IOException>(() => Collect(c, OpenAI())); });
await Test("Service error propagation", async () => { using var c = new InferenceClient(new Stub(_ => Task.FromResult(Stream("{\"error\":\"out of memory\"}\n")))); await Throws<IOException>(() => Collect(c, new())); });
await Test("HTTP failure propagation", async () => { using var c = new InferenceClient(new Stub(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)))); await Throws<HttpRequestException>(() => Collect(c, new())); });
await Test("Generation cancellation", async () => { using var c = new InferenceClient(new CancelStub()); using var token = new CancellationTokenSource(30); await Throws<OperationCanceledException>(() => Collect(c, new(), token.Token)); });
await Test("Context budgets preserve complete messages", () =>
{
    var messages = new[] { new ChatMessage { Content = new string('x', 80) }, new ChatMessage { Role = "assistant", Content = "old" }, new ChatMessage { Content = "new" } };
    var result = InferenceClient.BuildMessages(messages, "sys", 20); var json = JsonSerializer.Serialize(result);
    Check(!json.Contains("old") && json.Contains("new")); return Task.CompletedTask;
});
await Test("Oversized last message is rejected", () => Throws<ArgumentException>(() => { InferenceClient.BuildMessages([new() { Content = new string('x', 50) }], "sys", 20); return Task.CompletedTask; }));
await Test("Opening is sent as history in both protocols on every retained turn", () =>
{
    foreach (var protocol in new[] { ApiProtocol.Ollama, ApiProtocol.OpenAI })
    foreach (var role in new[] { "assistant", "user" })
    {
        var opening = new ChatMessage { Role = role, Content = "opening history", Opening = true };
        var history = new List<ChatMessage> { opening, new() { Content = "first question" } };
        for (var round = 0; round < 3; round++)
        {
            using var body = JsonDocument.Parse(InferenceClient.CreateBody(new(new() { Protocol = protocol }, "m"), history, "character rules", new()));
            var messages = body.RootElement.GetProperty("messages");
            Check(messages.GetArrayLength() == history.Count + 1);
            Check(messages[0].GetProperty("role").GetString() == "system" && messages[0].GetProperty("content").GetString() == "character rules");
            Check(messages[1].GetProperty("role").GetString() == role && messages[1].GetProperty("content").GetString() == opening.Content);
            Check(messages.EnumerateArray().Count(m => m.GetProperty("content").GetString() == opening.Content) == 1);
            history.Add(new() { Role = "assistant", Content = "reply " + round });
            history.Add(new() { Content = "next question " + round });
        }
    }
    return Task.CompletedTask;
});
await Test("Opening follows history budget without keeping orphaned replies", () =>
{
    var opening = new ChatMessage { Role = "assistant", Opening = true, Content = new string('x', 80) };
    var messages = new ChatMessage[] { opening, new() { Content = new string('y', 40) }, new() { Role = "assistant", Content = "old" }, new() { Content = "new" } };
    var selected = InferenceClient.Trim(messages, "sys", 20);
    Check(selected.Count == 1 && selected[0].Content == "new");
    Check(opening.Opening && opening.Content.Length == 80, "Trimming changed saved history");
    return Task.CompletedTask;
});
await Test("Incomplete assistant excluded", () => { var json = JsonSerializer.Serialize(InferenceClient.BuildMessages([new() { Content = "hello" }, new() { Role = "assistant", Content = "partial", Incomplete = true }], "sys", 100)); Check(!json.Contains("partial")); return Task.CompletedTask; });
await Test("Character V2 round trip", () =>
{
    var card = new CharacterCard { Name = "测试角色", Description = "介绍", SystemPrompt = "你是{{char}}", Greeting = "你好" };
    var result = CharacterExchange.Import(CharacterExchange.Export(card)); Check(result.Name == card.Name && result.SystemPrompt == card.SystemPrompt && result.Greeting == card.Greeting); return Task.CompletedTask;
});
await Test("Character V1 import", () => { var c = CharacterExchange.Import("{\"name\":\"A\",\"description\":\"D\",\"personality\":\"P\",\"first_mes\":\"Hi\"}"); Check(c.Description == "D" && c.SystemPrompt.Contains("P") && c.Greeting == "Hi"); return Task.CompletedTask; });
await Test("Invalid card rejected", () => Throws<ArgumentException>(() => { CharacterExchange.Import("{}"); return Task.CompletedTask; }));
await Test("Avatar, cover and conversation context persist independently", async () =>
{
    var root = Path.Combine(Environment.CurrentDirectory, "artifacts", "avatar-context-" + Guid.NewGuid());
    try
    {
        var store = new LocalStore(root);
        var card = new CharacterCard { Name = "avatar fixture", Image = new() { Data = "YWJj" }, Avatar = new() { Data = "ZGVm" } };
        await store.SaveCharactersAsync([card]);
        var chat = new Conversation { CharacterId = card.Id, CharacterSnapshot = card, ContextWindowOverride = 8192 };
        await store.SaveConversationAsync(chat);
        var saved = (await store.LoadCharactersAsync()).Single();
        var savedChat = await store.LoadConversationAsync(chat.Id);
        Check(saved.Avatar is { AssetId: not null, Store: not null } && saved.Avatar.AssetId != saved.Image?.AssetId);
        Check(savedChat.CharacterSnapshot?.Avatar?.AssetId == saved.Avatar!.AssetId && savedChat.ContextWindowOverride == 8192);
        var exchanged = CharacterExchange.Import(await CharacterExchange.ExportAsync(saved));
        Check(exchanged.Image?.Data == "YWJj" && exchanged.Avatar?.Data == "ZGVm");
        await store.SaveCharactersAsync([]);
        await store.SaveCharactersAsync([]);
        await store.CollectImageAssetsAsync();
        Check((await store.LoadConversationAsync(chat.Id)).CharacterSnapshot?.Avatar?.AssetId == saved.Avatar.AssetId);
        Check(JsonSerializer.Deserialize<Conversation>("{\"title\":\"old chat\"}", LocalStore.Json)?.ContextWindowOverride is null);
    }
    finally { Directory.Delete(root, true); }
});
await Test("An explicit context window reaches both protocols even when equal to model metadata", () =>
{
    var info = new ModelInfo { ContextLimit = "8192" };
    var settings = new AppSettings { ParamDefaults = new() { ["num_ctx"] = "8192" } };
    using var ollama = JsonDocument.Parse(InferenceClient.CreateBody(new(new(), "m"), [new() { Content = "hi" }], "", settings, info));
    using var compatible = JsonDocument.Parse(InferenceClient.CreateBody(new(OpenAI(), "m"), [new() { Content = "hi" }], "", settings, info));
    Check(ollama.RootElement.GetProperty("options").GetProperty("num_ctx").GetInt32() == 8192);
    Check(compatible.RootElement.GetProperty("n_ctx").GetInt32() == 8192);
    Check(!Prompting.RequestOptions(new(), info, false).ContainsKey("num_ctx") && !Prompting.RequestOptions(new(), info, true).ContainsKey("n_ctx"));
    return Task.CompletedTask;
});
await Test("Persistence, index, backup recovery, delete", async () =>
{
    var root = Path.Combine(Environment.CurrentDirectory, "artifacts", "Reflection-tests-" + Guid.NewGuid()); var store = new LocalStore(root);
    try
    {
        var chat = new Conversation { Title = "first" }; chat.Messages.Add(new() { Content = "你好" });
        await store.SaveConversationAsync(chat); chat.Title = "second"; await store.SaveConversationAsync(chat);
        Check((await store.LoadIndexAsync()).Count == 1); Check((await store.LoadConversationAsync(chat.Id)).Title == "second");
        await File.WriteAllTextAsync(Path.Combine(root, "chats", chat.Id + ".json"), "{broken");
        Check((await store.LoadConversationAsync(chat.Id)).Title == "first");
        await store.DeleteConversationAsync(chat.Id); Check((await store.LoadIndexAsync()).Count == 0);
        Check(!File.Exists(Path.Combine(root, "chats", chat.Id + ".json.bak")));
    }
    finally { Directory.Delete(root, true); }
});
await Test("Concurrent conversation saves keep index", async () =>
{
    var root = Path.Combine(Environment.CurrentDirectory, "artifacts", "Reflection-tests-" + Guid.NewGuid()); var store = new LocalStore(root);
    try { await Task.WhenAll(Enumerable.Range(0, 20).Select(i => store.SaveConversationAsync(new() { Title = i.ToString() }))); Check((await store.LoadIndexAsync()).Count == 20); }
    finally { Directory.Delete(root, true); }
});
await Test("Thinking is separate from answer text", async () =>
{
    using var c = new InferenceClient(new Stub(_ => Task.FromResult(Stream("{\"message\":{\"content\":\"答案\",\"thinking\":\"想过\"},\"done\":true}\n"))));
    var text = new StringBuilder(); var thinking = new StringBuilder();
    await foreach (var delta in c.StreamAsync(new(new(), "m"), [new() { Content = "hi" }], "", new(), default)) { text.Append(delta.Text); thinking.Append(delta.Thinking); }
    Check(text.ToString() == "答案" && thinking.ToString() == "想过");
    var openAi = InferenceClient.ParseChunk("{\"choices\":[{\"delta\":{\"content\":\"c\",\"reasoning_content\":\"d\"}}]}", false);
    Check(openAi.Text == "c" && openAi.Thinking == "d");
    var separated = Prompting.SeparateThink("可见<think>内部</think>结尾", "流式");
    Check(separated.Content == "可见结尾" && separated.Thinking.Contains("流式") && separated.Thinking.Contains("内部"));
});
await Test("Sampling sends only overrides", () =>
{
    var info = new ModelInfo { Defaults = new() { ["temperature"] = "0.7", ["num_predict"] = "2048" } };
    var plain = InferenceClient.CreateBody(new(new(), "m"), [new() { Content = "hi" }], "sys", new(), info);
    using (var doc = JsonDocument.Parse(plain)) Check(!doc.RootElement.TryGetProperty("options", out _));
    var settings = new AppSettings { Reason = "high", ParamDefaults = new() { ["top_p"] = "0.5", ["top_k"] = "100" } };
    var ollama = InferenceClient.CreateBody(new(new(), "m"), [new() { Content = "hi" }], "sys", settings, info);
    Check(ollama.Contains("\"think\":\"high\"") && ollama.Contains("\"top_p\":0.5") && ollama.Contains("\"top_k\":100"));
    var openai = InferenceClient.CreateBody(new(OpenAI(), "m"), [new() { Content = "hi" }], "sys", settings, info);
    using var body = JsonDocument.Parse(openai);
    Check(body.RootElement.GetProperty("reasoning_effort").GetString() == "high" && body.RootElement.GetProperty("top_k").GetInt32() == 64 && body.RootElement.GetProperty("top_p").GetDouble() == 0.5);
    return Task.CompletedTask;
});
await Test("Images skip the character budget", () =>
{
    var image = new ChatMessage { Content = "hi", Images = [new() { Mime = "image/jpeg", Data = new string('A', 4000) }] };
    var json = JsonSerializer.Serialize(InferenceClient.BuildMessages([new() { Content = new string('x', 80) }, image], "", 30));
    Check(json.Contains("hi") && json.Contains(new string('A', 4000)) && !json.Contains(new string('x', 80)));
    var ollama = InferenceClient.CreateBody(new(new(), "m"), [new() { Images = [new() { Data = "QQ==" }] }], "", new() { ContextCharacters = 1000 });
    var openai = InferenceClient.CreateBody(new(OpenAI(), "m"), [new() { Images = [new() { Mime = "image/jpeg", Data = "QQ==" }] }], "", new() { ContextCharacters = 1000 });
    Check(ollama.Contains("\"images\"") && ollama.Contains("QQ==") && openai.Contains("image_url") && openai.Contains("data:image/jpeg;base64,QQ=="));
    Check(Prompting.JoinContinuation("Hello", "Hello world") == "Hello world" && Prompting.JoinContinuation("Hello", " world") == "Hello world");
    return Task.CompletedTask;
});
await Test("Legacy files and character scenario round trip", () =>
{
    var settings = JsonSerializer.Deserialize<AppSettings>("{\"temperature\":0.4,\"maxOutputTokens\":128,\"contextCharacters\":4000}", LocalStore.Json)!;
    Check(settings.Temperature == 0.4 && settings.MaxOutputTokens == 128 && settings.Reason == "off" && settings.ParamDefaults.Count == 0);
    var card = JsonSerializer.Deserialize<CharacterCard>("{\"name\":\"旧\",\"description\":\"d\",\"systemPrompt\":\"s\",\"greeting\":\"g\"}", LocalStore.Json)!;
    Check(card.Setting == "" && card.OpeningRole == "assistant" && card.Image == null);
    var chat = JsonSerializer.Deserialize<Conversation>("{\"title\":\"t\",\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]}", LocalStore.Json)!;
    Check(chat.Draft == "" && chat.Attachments.Count == 0 && chat.Messages[0].Thinking == "" && chat.Messages[0].Images.Count == 0);
    var scene = CharacterExchange.Import("{\"name\":\"A\",\"description\":\"D\",\"personality\":\"P\",\"scenario\":\"S\",\"first_mes\":\"Hi\"}");
    Check(scene.SystemPrompt == "P" && scene.Setting == "S" && scene.Greeting == "Hi");
    var picture = new CharacterCard { Name = "图", Description = "介绍", Setting = "雨夜", SystemPrompt = "安静", Greeting = "来了", OpeningRole = "user", Image = new() { Mime = "image/jpeg", Data = "YQ==" } };
    var again = CharacterExchange.Import(CharacterExchange.Export(picture));
    Check(again.Setting == "雨夜" && again.OpeningRole == "user" && again.Greeting == "来了" && again.Image!.Data == "YQ==" && again.SystemPrompt == "安静");
    return Task.CompletedTask;
});
await Test("Stopped and failed replies never enter context", () =>
{
    var messages = new ChatMessage[] { new() { Content = "question" }, new() { Role = "assistant", Content = "stopped-secret", Stopped = true }, new() { Role = "assistant", Content = "failed-secret", Error = "offline" } };
    var json = JsonSerializer.Serialize(InferenceClient.BuildMessages(messages, "", 1000));
    Check(!json.Contains("secret") && json.Contains("question")); return Task.CompletedTask;
});
await Test("Continuation preserves target despite UI state mutation", () =>
{
    var answer = new ChatMessage { Role = "assistant", Content = "existing answer", Stopped = true };
    var snapshot = ConversationOperations.Continuation([new() { Content = "question" }, answer], answer);
    answer.Incomplete = true; answer.Content = "changed";
    var json = InferenceClient.CreateBody(new(new(), "m"), snapshot, "", new(), extraUser: Prompting.ContinueCue);
    Check(json.Contains("existing answer") && !json.Contains("changed")); return Task.CompletedTask;
});
await Test("Image-only edit preserves images and truncates following turns", () =>
{
    var original = new ChatMessage { Images = [new() { Data = "QQ==" }] };
    var chat = new Conversation { Messages = [original, new() { Role = "assistant", Content = "old reply" }] };
    ConversationOperations.ReplaceUserMessage(chat, original, "");
    Check(chat.Messages.Count == 1 && chat.Messages[0].Images.Single().Data == "QQ==" && InferenceClient.HasPayload(chat.Messages[0]));
    return Task.CompletedTask;
});
await Test("Character snapshot survives card changes and serialization", () =>
{
    var card = new CharacterCard { Name = "original", SystemPrompt = "original system", Image = new() { Data = "QQ==" } };
    var chat = new Conversation { CharacterId = card.Id, CharacterSnapshot = ConversationOperations.Snapshot(card) };
    card.SystemPrompt = "changed"; card.Image.Data = "changed";
    var loaded = JsonSerializer.Deserialize<Conversation>(JsonSerializer.Serialize(chat, LocalStore.Json), LocalStore.Json)!;
    Check(loaded.CharacterSnapshot!.SystemPrompt == "original system" && loaded.CharacterSnapshot.Image!.Data == "QQ=="); return Task.CompletedTask;
});
await Test("Card imports compose system personality examples and scenario", () =>
{
    var card = CharacterExchange.Import("""{"name":"A","description":"description","system_prompt":"system","personality":"personality","mes_example":"example","scenario":"scenario"}""");
    var prompt = Prompting.ResolveSystem(card, "", "", true);
    Check(new[] { "system", "personality", "example", "scenario", "description" }.All(prompt.Contains)); return Task.CompletedTask;
});
await Test("Character refresh updates settings and prompt while preserving opening history", () =>
{
    var card = new CharacterCard { Name = "old", Description = "old description", Setting = "old setting", SystemPrompt = "old rules", Greeting = "original opening", OpeningRole = "assistant", Image = new() { Data = "QQ==" } };
    var opening = new ChatMessage { Role = "assistant", Opening = true, Content = card.Greeting };
    var chat = new Conversation { CharacterId = card.Id, CharacterSnapshot = ConversationOperations.Snapshot(card), Messages = [opening, new() { Content = "question" }] };
    var updatedAt = chat.UpdatedAt;
    card.Name = "new"; card.Description = "new description"; card.Setting = "new setting"; card.SystemPrompt = "new rules";
    card.Greeting = "new opening"; card.OpeningRole = "user"; card.Image.Data = "Qg==";
    Check(ConversationOperations.RefreshCharacter(chat, card));
    var snapshot = chat.CharacterSnapshot!;
    Check(snapshot.Name == card.Name && snapshot.Description == card.Description && snapshot.Setting == card.Setting && snapshot.SystemPrompt == card.SystemPrompt && snapshot.Image!.Data == "Qg==");
    Check(snapshot.Greeting == "original opening" && snapshot.OpeningRole == "assistant" && ReferenceEquals(chat.Messages[0], opening) && opening.Content == "original opening" && chat.UpdatedAt == updatedAt);
    using var body = JsonDocument.Parse(InferenceClient.CreateBody(new(new(), "m"), chat.Messages, Prompting.ResolveSystem(snapshot, "", "", true), new()));
    var sent = body.RootElement.GetProperty("messages");
    Check(sent[0].GetProperty("content").GetString()!.Contains("new rules") && sent[0].GetProperty("content").GetString()!.Contains("new setting"));
    Check(sent[1].GetProperty("content").GetString() == "original opening");
    var unrelated = new Conversation { CharacterId = Guid.NewGuid(), CharacterSnapshot = new() { Name = "unrelated" } };
    Check(!ConversationOperations.RefreshCharacter(unrelated, card) && unrelated.CharacterSnapshot.Name == "unrelated");
    card.Image.Data = "changed again"; Check(snapshot.Image!.Data == "Qg==");
    return Task.CompletedTask;
});
await Test("Recovery retains valid backup on next save and deletion scrubs index", async () =>
{
    var root = Path.Combine(Environment.CurrentDirectory, "artifacts", "Reflection-tests-" + Guid.NewGuid()); var store = new LocalStore(root);
    try
    {
        var chat = new Conversation { Title = "private-title", Messages = [new() { Content = "private-preview" }] };
        await store.SaveConversationAsync(chat); await store.SaveConversationAsync(chat);
        var path = Path.Combine(root, "chats", chat.Id + ".json");
        await File.WriteAllTextAsync(path, "{broken");
        var restored = await store.LoadConversationAsync(chat.Id); await store.SaveConversationAsync(restored);
        using (JsonDocument.Parse(await File.ReadAllTextAsync(path + ".bak"))) { }
        await store.DeleteConversationAsync(chat.Id);
        Check(!(await File.ReadAllTextAsync(Path.Combine(root, "index.json.bak"))).Contains("private-"));
        await File.WriteAllTextAsync(Path.Combine(root, "index.json"), "{broken");
        Check((await store.LoadIndexAsync()).Count == 0);
    }
    finally { Directory.Delete(root, true); }
});
await Test("Application tray preference defaults on and retains a valid opt-out backup", async () =>
{
    var root = Path.Combine(Environment.CurrentDirectory, "artifacts", "Reflection-tests-" + Guid.NewGuid());
    var store = new LocalStore(root);
    try
    {
        Check((await store.LoadApplicationPreferencesAsync()).CloseToTray);
        var path = Path.Combine(root, "application.json");
        await File.WriteAllTextAsync(path, "{}");
        Check((await store.LoadApplicationPreferencesAsync()).CloseToTray);
        await store.SaveApplicationPreferencesAsync(new(CloseToTray: false));
        await store.SaveApplicationPreferencesAsync(new(CloseToTray: true));
        await File.WriteAllTextAsync(path, "{broken");
        Check(!(await store.LoadApplicationPreferencesAsync()).CloseToTray && store.RecoveryNotice is not null);
        await store.SaveApplicationPreferencesAsync(new(CloseToTray: true));
        File.Delete(path);
        Check(!(await store.LoadApplicationPreferencesAsync()).CloseToTray);
    }
    finally { Directory.Delete(root, true); }
});
await Test("Missing index is reconstructed from conversations", async () =>
{
    var root = Path.Combine(Environment.CurrentDirectory, "artifacts", "Reflection-tests-" + Guid.NewGuid()); var store = new LocalStore(root);
    try
    {
        var chat = new Conversation { Title = "recover me" }; await store.SaveConversationAsync(chat);
        File.Delete(Path.Combine(root, "index.json"));
        Check((await store.LoadIndexAsync()).Single().Id == chat.Id);
    }
    finally { Directory.Delete(root, true); }
});
await Test("Thinking stream has a total output bound", async () =>
{
    var payload = JsonSerializer.Serialize(new { message = new { thinking = new string('x', 500001) }, done = true }) + "\n";
    using var c = new InferenceClient(new Stub(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(payload) })));
    await Throws<IOException>(() => Collect(c, new()));
});
await Test("Oversized unbroken protocol line is rejected", async () =>
{
    using var c = new InferenceClient(new Stub(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(new string('x', 1_048_577)) })));
    await Throws<IOException>(() => Collect(c, new()));
});
await Test("Picture payload and additional instruction budgets are bounded", async () =>
{
    await Throws<ArgumentException>(() => { InferenceClient.CreateBody(new(new(), "m"), [new() { Images = new(Enumerable.Range(0, 17).Select(_ => new ChatImage { Data = "QQ==" })) }], "", new()); return Task.CompletedTask; });
    await Throws<ArgumentException>(() => { InferenceClient.CreateBody(new(new(), "m"), [new() { Content = "hi" }], "", new() { ContextCharacters = 10 }, extraUser: new string('x', 20)); return Task.CompletedTask; });
});
await Test("Math tokenization preserves code, currency and exact source", () =>
{
    const string text = "价格 $5 与 $10。行内 \\(x^2\\)。\n```tex\n$$not math$$\n```\n`\\(code\\)`\n$$\\frac{1}{2}\n+1$$";
    var tokens = MessageMarkupTokenizer.Tokenize(text);
    Check(string.Concat(tokens.Select(t => t.Raw)) == text);
    Check(tokens.Count(t => t.Kind == MarkupKind.InlineMath) == 1);
    Check(tokens.Count(t => t.Kind == MarkupKind.DisplayMath) == 1);
    Check(tokens.Count(t => t.Kind == MarkupKind.CodeBlock) == 1);
    Check(tokens.Count(t => t.Kind == MarkupKind.InlineCode) == 1);
    return Task.CompletedTask;
});
await Test("Math boundaries preserve escaped and incomplete content", () =>
{
    foreach (var text in new[] { @"未结束 \(x + 1", @"转义 \\(x\\)", "\\(x\ny\\)", "```\n$$open" })
    {
        var tokens = MessageMarkupTokenizer.Tokenize(text);
        Check(string.Concat(tokens.Select(t => t.Raw)) == text);
        Check(!tokens.Any(t => t.Kind is MarkupKind.InlineMath or MarkupKind.DisplayMath));
    }
    var block = MessageMarkupTokenizer.Tokenize("前文\\[x^2\n+y^2\\]后文");
    Check(block.Count(t => t.Kind == MarkupKind.DisplayMath) == 1);
    return Task.CompletedTask;
});
await Test("Dollar math handles model output without consuming currency or escapes", () =>
{
    const string text = @"分析$\oint_L P\mathrm dx$，其中 $\mathbf{F} = (P,Q,R)$。价格 $5 与 $10。转义 \$x\$。$$\oint_L \mathbf{F} \cdot d\mathbf{r}$$";
    var tokens = MessageMarkupTokenizer.Tokenize(text);
    Check(string.Concat(tokens.Select(t => t.Raw)) == text);
    Check(tokens.Count(t => t.Kind == MarkupKind.InlineMath) == 2);
    Check(tokens.Count(t => t.Kind == MarkupKind.DisplayMath) == 1);
    Check(MessageMarkupTokenizer.Tokenize("$2x+1$ and $5$").Count(t => t.Kind == MarkupKind.InlineMath) == 2);
    return Task.CompletedTask;
});
await Test("Code fences and multi-backtick spans take precedence over math", () =>
{
    foreach (var text in new[] { "~~~tex\n\\(x\\)\n~~~", "   ```tex\n$$x$$\n   ```", "``code ` $$x$$``" })
    {
        var tokens = MessageMarkupTokenizer.Tokenize(text);
        Check(string.Concat(tokens.Select(t => t.Raw)) == text);
        Check(!tokens.Any(t => t.Kind is MarkupKind.InlineMath or MarkupKind.DisplayMath));
    }
    return Task.CompletedTask;
});
await Test("Markdown table preserves math, code pipes, escapes and source", () =>
{
    const string text = "前文\n| 项 | 表达式 | 对应方向 |\n|---|:---:|---:|\n| 第一项 | $\\frac{\\partial R}{\\partial y}$ | $x$ 方向 |\n| 绝对值 | $|x|$ | `a|b` 与 a\\|b |\n后文";
    var tokens = MessageMarkupTokenizer.Tokenize(text);
    Check(string.Concat(tokens.Select(t => t.Raw)) == text);
    var token = tokens.Single(t => t.Kind == MarkupKind.Table);
    Check(MarkdownTableParser.TryRead(token.Raw, 0, out var table, out _));
    Check(table!.Headers.Length == 3 && table.Rows.Count == 2);
    Check(table.Alignments.SequenceEqual(new[] { MarkdownColumnAlignment.Left, MarkdownColumnAlignment.Center, MarkdownColumnAlignment.Right }));
    Check(table.Rows[1][1] == "$|x|$" && table.Rows[1][2] == "`a|b` 与 a|b");
    return Task.CompletedTask;
});
await Test("Markdown table accepts missing outer pipes and normalizes row widths", () =>
{
    Check(MarkdownTableParser.TryRead("A | B\n--- | ---\n1 | 2 | 3\n4 |\n", 0, out var table, out _));
    Check(table!.Rows[0].SequenceEqual(new[] { "1", "2" }));
    Check(table.Rows[1].SequenceEqual(new[] { "4", "" }));
    return Task.CompletedTask;
});
await Test("Table recognition excludes fenced code and invalid separator rows", () =>
{
    foreach (var text in new[] { "```md\n| A | B |\n|---|---|\n```", "| A | B |\n|--|---|", "| A | B |\n|---|---|---|" })
        Check(!MessageMarkupTokenizer.Tokenize(text).Any(t => t.Kind == MarkupKind.Table));
    Check(MessageMarkupTokenizer.Tokenize("| A | B |\n|---|---|\n").Count(t => t.Kind == MarkupKind.Table) == 1);
    return Task.CompletedTask;
});
await Test("Theme preference is backward compatible and persisted", async () =>
{
    Check(JsonSerializer.Deserialize<AppSettings>("{}")!.Theme == "light");
    var dir = Path.Combine(Environment.CurrentDirectory, "artifacts", "test-theme-" + Guid.NewGuid().ToString("N"));
    try
    {
        var store = new LocalStore(dir);
        await store.SaveSettingsAsync(new AppSettings { Theme = "dark" });
        Check((await store.LoadSettingsAsync()).Theme == "dark");
    }
    finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
});
await Test("Character history uses snapshot name while retaining topic titles", async () =>
{
    var dir = Path.Combine(Environment.CurrentDirectory, "artifacts", "test-character-title-" + Guid.NewGuid().ToString("N"));
    try
    {
        var store = new LocalStore(dir);
        var chat = new Conversation { Title = "公式讨论", CharacterSnapshot = new CharacterCard { Name = "数学老师" } };
        await store.SaveConversationAsync(chat);
        var summary = (await store.LoadIndexAsync()).Single();
        Check(summary.DisplayTitle == "数学老师" && summary.Title == "公式讨论");
        // A library edit has no bearing on the captured conversation identity.
        Check((await store.LoadConversationAsync(chat.Id)).CharacterSnapshot!.Name == summary.CharacterName);
        var indexFile = Path.Combine(dir, "index.json");
        await File.WriteAllTextAsync(indexFile, JsonSerializer.Serialize(new[] { new { id = chat.Id, title = "公式讨论", updatedAt = chat.UpdatedAt, preview = "" } }, LocalStore.Json));
        var legacy = (await store.LoadIndexAsync()).Single();
        Check(legacy.DisplayTitle == "数学老师");
        var plain = new Conversation { Title = "普通对话" }; await store.SaveConversationAsync(plain);
        Check((await store.LoadIndexAsync()).Single(x => x.Id == plain.Id).DisplayTitle == "普通对话");
    }
    finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
});
await Test("History collapse preference survives settings round trip", () =>
{
    Check(JsonSerializer.Deserialize<AppSettings>("{}")!.HistoryExpanded);
    Check(!JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(new AppSettings { HistoryExpanded = false }))!.HistoryExpanded);
    return Task.CompletedTask;
});
await Test("Reader parses native Markdown structure, outline, math and safe image placeholders", () =>
{
    var doc = ReaderDocuments.Parse("sample.md", "# 阅读\n\n**加粗**、*斜体*与 \\(a^2+b^2=c^2\\)。\n\n| 左 | 右 |\n|:---|---:|\n| 文本 | $x^2$ |\n\n```txt\n# 非标题\n\\(不解析\\)\n```\n\n## 阅读\n");
    Check(doc.Outline.Count == 2 && doc.Outline[0].Title == "阅读" && doc.Outline[1].Anchor == "阅读-1");
    var blocks = doc.Pieces.SelectMany(p => p.Blocks).ToArray();
    Check(blocks.OfType<Markdig.Extensions.Tables.Table>().Any());
    Check(blocks.OfType<Markdig.Syntax.FencedCodeBlock>().Single().Lines.ToString().Contains("\\(不解析\\)"));
    var math = blocks.OfType<Markdig.Syntax.ParagraphBlock>().SelectMany(p => p.Inline ?? new Markdig.Syntax.Inlines.ContainerInline()).OfType<Markdig.Extensions.Mathematics.MathInline>().ToArray();
    Check(math.Any(m => m.Content.ToString().Contains("a^2")), "Slash formulas were not parsed");
    var literal = ReaderDocuments.Parse("literal.md", "金额 $5 和 $10；孤立 \\) 保持原文，~普通文字~ 后的 \\(x^2\\) 仍是公式。");
    var literalBlock = literal.Pieces.SelectMany(p => p.Blocks).OfType<Markdig.Syntax.ParagraphBlock>().Single();
    Check(literalBlock.Inline!.OfType<Markdig.Extensions.Mathematics.MathInline>().Count() == 1);
    Check(ReaderDocuments.InlineText(literalBlock.Inline).Contains("$5"));
    return Task.CompletedTask;
});
await Test("Reader bounds long TXT/paragraph/code/list/table pieces and preserves text", () =>
{
    var text = "第一章 开始\n" + string.Concat(Enumerable.Repeat("这是很长的正文。😀", 40000)) + "\n第二章 后续\n结束";
    var doc = ReaderDocuments.Parse("novel.txt", text);
    Check(doc.Outline.Count == 2 && doc.Pieces.Count > 100);
    Check(doc.Pieces.All(p => (p.Text?.Length ?? 0) <= ReaderDocuments.PieceCharacters + 1));
    Check(string.Concat(doc.Pieces.Select(p => p.Text)).Contains("第二章 后续"));
    var code = ReaderDocuments.Parse("code.md", "```txt\n" + new string('x', 40000) + "\n```");
    Check(code.Pieces.Count > 10 && code.Pieces.All(p => p.Code && p.Text!.Length <= ReaderDocuments.PieceCharacters));
    var table = ReaderDocuments.Parse("table.md", "| A | B |\n|---|---|\n" + string.Concat(Enumerable.Range(0, 500).Select(i => $"| {i} | data |\n")));
    Check(table.Pieces.Count > 20 && table.Pieces.All(p => p.Count <= 24));
    var list = ReaderDocuments.Parse("list.md", string.Concat(Enumerable.Range(0, 500).Select(i => $"- item {i}\n")));
    Check(list.Pieces.Count > 20 && list.Pieces.All(p => p.Count <= 24));
    var paragraph = ReaderDocuments.Parse("long.md", new string('字', 30000));
    Check(paragraph.Pieces.Count > 10 && paragraph.Pieces.All(p => p.Length <= ReaderDocuments.PieceCharacters));
    return Task.CompletedTask;
});
await Test("Reader cancellation and Markdown TXT isolation", async () =>
{
    using var cancel = new CancellationTokenSource(); cancel.Cancel();
    await Throws<OperationCanceledException>(() => Task.FromResult(ReaderDocuments.Parse("x.txt", "text", token: cancel.Token)));
    var doc = ReaderDocuments.Parse("safe.txt", "<script>alert(1)</script>\n# literal\n$x$");
    Check(doc.Pieces.All(p => p.Blocks.Count == 0) && doc.Outline.Count == 0);
});
await Test("Reader encoding fallback, bounded file reads and unsupported file rejection", async () =>
{
    var dir = Path.Combine(Path.GetTempPath(), "reader-core-" + Guid.NewGuid()); Directory.CreateDirectory(dir);
    try
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var path = Path.Combine(dir, "gb.txt"); await File.WriteAllBytesAsync(path, Encoding.GetEncoding("GB18030").GetBytes("第一章 测试\n中文阅读"));
        var doc = await ReaderDocuments.OpenAsync(path, default); Check(doc.Encoding == "GB18030" && doc.Text.Contains("中文阅读"));
        await File.WriteAllTextAsync(path, "第二章 UTF-16\n正文", Encoding.Unicode);
        var unicode = await ReaderDocuments.OpenAsync(path, default); Check(unicode.Encoding == "UTF-16 LE" && unicode.Text.Contains("正文"));
        var huge = Path.Combine(dir, "huge.md"); using (var stream = File.Create(huge)) stream.SetLength(ReaderDocuments.MaxBytes + 1L);
        await Throws<InvalidDataException>(() => ReaderDocuments.OpenAsync(huge, default));
        await Throws<InvalidDataException>(() => ReaderDocuments.OpenAsync(Path.Combine(dir, "image.png"), default));
    }
    finally { Directory.Delete(dir, true); }
});
await Test("Reader state/snapshot persistence, backup recovery and snapshot clearing", async () =>
{
    var dir = Path.Combine(Environment.CurrentDirectory, ".build", "reader-store-" + Guid.NewGuid());
    try
    {
        var store = new ReaderStore(dir); var state = new ReaderState { LastPath = "book.md", Sources = ["book.md"], FontSize = 22 };
        state.Positions["book.md"] = new(2300, .42); await store.SaveAsync(state); await store.SaveAsync(state);
        await File.WriteAllTextAsync(Path.Combine(dir, "reader", "state.json"), "broken");
        var loaded = await store.LoadAsync(); Check(loaded.LastPath == "book.md" && loaded.Positions["BOOK.md"].Offset == 2300 && loaded.FontSize == 22);
        await store.SaveSnapshotAsync(ReaderDocuments.Parse("book.md", "# 正文\n\n保留内容"));
        Check((await store.LoadSnapshotAsync())?.Text.Contains("保留内容") == true);
        await store.ClearSnapshotAsync(); Check(await store.LoadSnapshotAsync() is null);
    }
    finally { Directory.Delete(dir, true); }
});
await Test("Reader image paths resolve local references and reject remote sources", () =>
{
    var directory = Path.GetFullPath("reader-path-fixture");
    var document = Path.Combine(directory, "chapter.md");
    var image = Path.Combine(directory, "figures", "图 1.png");
    Check(ReaderDocuments.ResolveImagePath(document, "figures/%E5%9B%BE%201.png") == image);
    Check(ReaderDocuments.ResolveImagePath(document, new Uri(image).AbsoluteUri) == image);
    foreach (var reference in new[] { "https://example.invalid/x.png", "//server/share/x.png",
        "file://server/share/x.png", "%5c%5cserver/share/x.png", "data:image/png;base64,AAAA", "figure.png:alternate.png", "figure.svg" })
        Check(ReaderDocuments.ResolveImagePath(document, reference) is null, "Unsafe image reference: " + reference);
    Check(ReaderDocuments.Parse(document, "![图](figures/x.png)").Pieces.Single().SourcePath == document);
    return Task.CompletedTask;
});
await Test("Shared Markdown extensions preserve source and virtualized footnote positions", () =>
{
    const string source = "# Topic\n\nH~2~O and x^2^.[^ref] [[Sibling#Section|Open section]]\n\nTerm\n: definition\n\n> [!TIP]\n> Keep reading\n\n[^ref]: Footnote body\n\n## Last";
    var ast = ReaderDocuments.ParseMarkdown(source);
    Check(ast.Descendants<EmphasisInline>().Select(i => i.DelimiterChar).SequenceEqual(['~', '^']));
    Check(ast.Descendants<LinkInline>().Any(link => link.Url == "Sibling.md#Section" && ReaderDocuments.InlineText(link) == "Open section"));
    Check(ast.OfType<DefinitionList>().Count() == 1 && ast.OfType<AlertBlock>().Single().Kind.ToString().Equals("tip", StringComparison.OrdinalIgnoreCase));
    var reading = ReaderDocuments.Parse("sample.md", source);
    var note = reading.Pieces.SelectMany(piece => piece.Blocks).OfType<Footnote>().Single();
    Check(note.Order == 1 && note.Links.Any(link => !link.IsBackLink && link.Span.Start == source.IndexOf("[^ref]", StringComparison.Ordinal)));
    Check(reading.Pieces.Any(piece => piece.Blocks.Contains(note) && piece.Offset == note.Span.Start) && note.Span.Start > source.IndexOf("[^ref]:", StringComparison.Ordinal));
    Check(reading.Pieces.Select(piece => piece.Offset).SequenceEqual(reading.Pieces.Select(piece => piece.Offset).Order()));
    Check(reading.Text == source && reading.Outline.Count == 2);
    var rows = ReaderDocuments.Parse("code.md", "```cs\n" + string.Join('\n', Enumerable.Repeat("int n = 1;", 185)) + "\n```").Pieces;
    Check(rows.Select(piece => piece.CodeLine).SequenceEqual([1, 81, 161]) && rows.All(piece => piece.CodeLanguage == "cs"));
    return Task.CompletedTask;
});
await Test("Shared Markdown keeps formula and code pipes inside cells and leaves partial TeX visible", () =>
{
    const string source = "| A | B |\n|---|---|\n| $a|b$ | `x|y` |\n\n金额 $5 和 $10。\n\n未闭合 \\(x";
    var ast = ReaderDocuments.ParseMarkdown(source);
    var table = ast.OfType<Markdig.Extensions.Tables.Table>().Single();
    Check(table.ColumnDefinitions.Count == 2 && table.Descendants<MathInline>().Single().Content.ToString() == "a|b");
    Check(table.Descendants<CodeInline>().Single().Content == "x|y" && ast.Descendants<MathInline>().Count() == 1);
    Check(ReaderDocuments.InlineText(ast.OfType<ParagraphBlock>().Last().Inline).EndsWith("\\(x", StringComparison.Ordinal));
    return Task.CompletedTask;
});
await Test("Native diagram subset rejects includes, unsupported directives and oversized input", () =>
{
    Check(MarkdownDiagrams.Parse("flowchart TD\nA[开始] --> B{判断} -->|通过| C(结束)", "mermaid")
        is FlowDiagram { Nodes.Count: 3, Edges.Count: 2 } flow && flow.Nodes[1].Shape == "diamond" && flow.Edges[1].Label == "通过");
    Check(MarkdownDiagrams.Parse("sequenceDiagram\nparticipant A as 用户\nA->>B: 读取\nB-->>A: 返回", "mermaid")
        is SequenceDiagram { Participants.Count: 2, Messages.Count: 2 });
    Check(MarkdownDiagrams.Parse("@startuml\nparticipant \"Service\" as S\nA -> S: ping\nS --> A: pong\n@enduml", "plantuml")
        is SequenceDiagram sequence && sequence.Participants[0].Label == "Service" && sequence.Messages[1].From == "S");
    Check(MarkdownDiagrams.Parse("@startuml\n!include https://example.invalid/test\nA -> B\n@enduml", "plantuml") is null);
    Check(MarkdownDiagrams.Parse("flowchart TD\nA --> B\nclick A \"https://example.invalid\"", "mermaid") is null);
    Check(MarkdownDiagrams.Parse("flowchart TD\nA --> B\n" + new string('x', 6000), "mermaid") is null);
    return Task.CompletedTask;
});
await Test("Reader shares explicit math boundaries and keeps cross-piece formulas intact", () =>
{
    const string explicitMath = @"$2π$ $2α$ $x$2 \( x+y \) \[ x+y \] $$ x+y $$";
    Check(ReaderDocuments.ParseMarkdown(explicitMath).Descendants<MathInline>().Count() == 6);
    Check(MessageMarkupTokenizer.Tokenize(explicitMath).Count(token =>
        token.Kind is MarkupKind.InlineMath or MarkupKind.DisplayMath) == 6);
    const string chineseUnit = @"$2\text{米}$，另有价格 $5 和 $10。";
    Check(ReaderDocuments.ParseMarkdown(chineseUnit).Descendants<MathInline>().Single().Content.ToString() == @"2\text{米}");
    Check(MessageMarkupTokenizer.Tokenize(chineseUnit).Single(token => token.Kind == MarkupKind.InlineMath).Text == @"2\text{米}");
    const string currency = "价格 $5 和 $10；$10.20 与 $20.50。";
    Check(!ReaderDocuments.ParseMarkdown(currency).Descendants<MathInline>().Any());
    Check(!MessageMarkupTokenizer.Tokenize(currency).Any(token =>
        token.Kind is MarkupKind.InlineMath or MarkupKind.DisplayMath));

    var source = new string('a', ReaderDocuments.PieceCharacters - 2) + " $x+1$ " +
        new string('b', ReaderDocuments.PieceCharacters + 10);
    var md = ReaderDocuments.Parse("boundary.md", source);
    var txt = ReaderDocuments.Parse("boundary.txt", source);
    Check(md.Pieces.SelectMany(piece => piece.Blocks).OfType<ParagraphBlock>().SelectMany(block => block.Inline?.Descendants<MathInline>() ?? [])
        .Single().Content.ToString() == "x+1");
    Check(txt.Pieces.SelectMany(piece => MessageMarkupTokenizer.Tokenize(piece.Text!, recognizeTables: false))
        .Single(token => token.Kind == MarkupKind.InlineMath).Text == "x+1");
    Check(string.Concat(txt.Pieces.Select(piece => piece.Text)).TrimEnd('\n') == source);

    var document = ReaderDocuments.Parse("numbers.md", @"前向 \eqref{eq:a}

\begin{align}
a&=b\label{eq:a}\\
c&=d
\end{align}

```tex
\begin{equation}x=1\label{ignored}\end{equation}
```");
    var formula = document.Pieces.First().Formulas!.Entries.Single();
    Check(formula.Source.Contains(@"\label{eq:a}") && formula.Display &&
        document.Text.AsSpan(formula.Offset).StartsWith(@"\begin{align}"));
    return Task.CompletedTask;
});
await Test("Reader math accepts Chinese adjacency and nested list formulas", () =>
{
    var doc = ReaderDocuments.Parse("limits.md", "1. 唯一性：$f$在点$p$的极限存在。\n2. 对任意正数$\\varepsilon$，总有点$p$的邻域$\\mathring N_\\varepsilon(q)$。\n   - 设$\\lim f(x)=A,\\lim g(x)=B$，其中$k,l$为常数。\n   - $\\frac{f(x)}{g(x)}$为商。\n\n金额 $5 和 $10；转义 \\$x\\$；`$code$`。\n");
    IEnumerable<Markdig.Extensions.Mathematics.MathInline> InlineMath(Markdig.Syntax.Inlines.ContainerInline? parent)
    {
        if (parent is null) yield break;
        foreach (var node in parent)
        {
            if (node is Markdig.Extensions.Mathematics.MathInline math) yield return math;
            if (node is Markdig.Syntax.Inlines.ContainerInline child) foreach (var m in InlineMath(child)) yield return m;
        }
    }
    IEnumerable<Markdig.Extensions.Mathematics.MathInline> BlockMath(Markdig.Syntax.Block node)
    {
        if (node is Markdig.Syntax.LeafBlock leaf) foreach (var m in InlineMath(leaf.Inline)) yield return m;
        if (node is Markdig.Syntax.ContainerBlock parent) foreach (var child in parent) foreach (var m in BlockMath(child)) yield return m;
    }
    var maths = doc.Pieces.SelectMany(p => p.Blocks).SelectMany(BlockMath).ToArray();
    Check(maths.Length == 8, "CJK math was lost or currency/code interpreted as math: " + string.Join(" | ", maths.Select(m => m.Content.ToString())));
    Check(maths.Any(m => m.Content.ToString() == "f") && maths.Any(m => m.Content.ToString().Contains("mathring")));
    return Task.CompletedTask;
});
await Test("Daily and private profiles keep independent chat, role and reading stores", async () =>
{
    var root = Path.Combine(Environment.CurrentDirectory, ".build", "profile-test-" + Guid.NewGuid());
    try
    {
        var daily = new WorkspaceProfile(root); var privacy = daily.Other;
        Check(daily.Root != privacy.Root && privacy.Other.Root == daily.Root);
        var a = new LocalStore(daily.Root); var b = new LocalStore(privacy.Root);
        await a.SaveCharactersAsync([new CharacterCard { Name = "daily" }]); await b.SaveCharactersAsync([new CharacterCard { Name = "private" }]);
        await a.SaveConversationAsync(new Conversation { Title = "daily chat" }); await b.SaveConversationAsync(new Conversation { Title = "private chat" });
        var ar = new ReaderStore(daily.Root); var br = new ReaderStore(privacy.Root);
        await ar.SaveAsync(new ReaderState { LastPath = "daily.md", Sources = ["daily.md"] }); await br.SaveAsync(new ReaderState { LastPath = "private.md", Sources = ["private.md"] });
        Check((await a.LoadCharactersAsync()).Single().Name == "daily" && (await b.LoadCharactersAsync()).Single().Name == "private");
        Check((await a.LoadIndexAsync()).Single().Title == "daily chat" && (await b.LoadIndexAsync()).Single().Title == "private chat");
        Check((await ar.LoadAsync()).LastPath == "daily.md" && (await br.LoadAsync()).LastPath == "private.md");
    }
    finally { Directory.Delete(root, true); }
});
await Test("Scroll motion preserves velocity, reverses immediately and converges without overshoot", () =>
{
    var motion = new ScrollDynamics(); motion.Reset(100); motion.Add(66, 1000);
    var last = motion.Position;
    for (var i = 0; i < 5; i++) { var next = motion.Step(1d / 60); Check(next >= last && next <= motion.Target); last = next; }
    var velocity = motion.Velocity; motion.Add(66, 1000);
    Check(motion.Velocity == velocity, "Wheel input restarted the existing velocity");
    motion.Add(-66, 1000); Check(motion.Step(1d / 60) < last, "Direction reversal retained stale motion");
    for (var i = 0; i < 100; i++) motion.Step(1d / 120);
    Check(motion.Settled && Math.Abs(motion.Position - motion.Target) < .01);
    motion.Add(-10000, 1000); for (var i = 0; i < 100; i++) Check(motion.Step(1d / 60) >= 0);
    return Task.CompletedTask;
});
await Test("Scroll trajectory is independent of callback frame rate", () =>
{
    double At(int rate) { var m = new ScrollDynamics(); m.Reset(0); m.Add(200, 1000); for (var i = 0; i < rate / 4; i++) m.Step(1d / rate); return m.Position; }
    Check(Math.Abs(At(60) - At(120)) < .01 && Math.Abs(At(60) - At(144)) < .01);
    return Task.CompletedTask;
});
await ImageRegressions.RunAsync(Test);
Console.WriteLine($"\n{passed} tests passed.");
static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK) { Content = new StringContent(value, Encoding.UTF8, "application/json") };
static HttpResponseMessage Stream(string value) => new(HttpStatusCode.OK) { Content = new StreamContent(new FragmentStream(Encoding.UTF8.GetBytes(value))) };
sealed class Stub(Func<HttpRequestMessage, Task<HttpResponseMessage>> callback) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => callback(request);
}
sealed class CancelStub : HttpMessageHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) { await Task.Delay(10000, ct); return new(HttpStatusCode.OK); }
}
sealed class FragmentStream(byte[] bytes) : MemoryStream(bytes)
{
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => base.ReadAsync(buffer[..Math.Min(buffer.Length, 3)], ct);
}
