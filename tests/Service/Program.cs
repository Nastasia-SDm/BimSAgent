using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using BimSAgentApp;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

var checks = 0;
void Check(bool condition, string name) { if (!condition) throw new Exception(name); checks++; Console.WriteLine("PASS " + name); }
var root = Path.Combine(Path.GetTempPath(), "bims-service-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var originalDirectory = Environment.CurrentDirectory;
var originalError = Console.Error;
using var diagnostics = new StringWriter();
Console.SetError(TextWriter.Synchronized(diagnostics));
Environment.CurrentDirectory = root;
Environment.SetEnvironmentVariable("BIMS_LLM_PROVIDER", "openai");
Environment.SetEnvironmentVariable("OPENAI_API_KEY", null);
try
{
    var defaults = LocalServiceOptions.Parse([]);
    Check(defaults.TimeoutSeconds == 180 && defaults.ContextTokens == 2048 && defaults.MaxOutputTokens == 128,
        "service timeout remains 180s independently of agent; lightweight defaults");
    var fake = new FakeOllama();
    using var ollama = new HttpClient(fake);
    await using var app = LocalHttpService.Create(new LocalServiceOptions { Port = 0, RequestsPerMinute = 100, TimeoutSeconds = 1 }, ollama);
    await app.StartAsync();
    using var http = new HttpClient { BaseAddress = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
    Task<HttpResponseMessage> Ask(string q) => http.PostAsJsonAsync("/chat", new { question = q });
    using (var r = await Ask("Как создать стену?"))
    {
        Check(r.StatusCode == HttpStatusCode.OK && (await r.Content.ReadAsStringAsync()).Contains("answer"), "success without key, cloud provider ignored");
        Check(fake.Calls == 1 && fake.ValidPayload, "exactly one local qwen call, short system and bounded options");
    }
    for (var i = 0; i < 100 && !diagnostics.ToString().Contains("stage=http_completed"); i++) await Task.Delay(10);
    var stages = new[] { "received", "ollama_sent", "ollama_received", "ollama_metrics", "http_written", "finished", "http_completed" };
    var lines = diagnostics.ToString().Split('\n').Where(l => l.Contains("request=1 ")).ToArray();
    Check(lines.Length == stages.Length && lines.Select(l => l.Split("stage=")[1].Split(' ')[0]).SequenceEqual(stages),
        "per request timing covers receive, one Ollama call, HTTP completion in order");
    Check(diagnostics.ToString().Contains("input_tokens=160 output_tokens=30") &&
        diagnostics.ToString().Contains("load_ms=10 prompt_ms=20 decode_ms=30"), "Ollama timing and real token counts logged");
    Check(!diagnostics.ToString().Contains("Как создать стену") && !diagnostics.ToString().Contains("Короткий ответ"),
        "diagnostics never log question or answer");
    foreach (var invalid in new[] { "{", "{}", "{\"question\":\" \"}", "{\"question\":1}" })
    {
        using var r = await http.PostAsync("/chat", new StringContent(invalid, Encoding.UTF8, "application/json"));
        Check(r.StatusCode == HttpStatusCode.BadRequest, "invalid JSON/question");
    }
    using (var r = await Ask(new string('a', 2001))) Check((int)r.StatusCode == 413, "question length");
    using (var r = await Ask(new string('я', 1900))) Check((int)r.StatusCode == 413, "complete context including system");
    using (var r = await Ask(new string('a', 17000))) Check((int)r.StatusCode == 413, "body size");
    using (var r = await http.PostAsync("/chat", new StringContent("{}"))) Check((int)r.StatusCode == 415, "content type");
    Check(fake.Calls == 1, "rejected requests never call model");
    fake.Mode = "error";
    using (var r = await Ask("test")) Check((int)r.StatusCode == 502, "Ollama status error");
    fake.Mode = "unavailable";
    using (var r = await Ask("test")) Check((int)r.StatusCode == 503, "Ollama unavailable, no fallback");
    fake.Mode = "json";
    using (var r = await Ask("test")) Check((int)r.StatusCode == 502, "invalid Ollama JSON");
    fake.Mode = "wait";
    fake.Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    var first = Ask("test");
    await fake.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
    using (var r = await Ask("test")) Check((int)r.StatusCode == 429 && r.Headers.RetryAfter != null, "concurrency one, no queue");
    using (var r = await first) Check((int)r.StatusCode == 504 && fake.Cancelled > 0, "timeout cancels upstream");
    fake.Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    using (var cancellation = new CancellationTokenSource())
    {
        var pending = http.PostAsJsonAsync("/chat", new { question = "test" }, cancellation.Token);
        await fake.Entered.Task;
        var before = fake.Cancelled;
        cancellation.Cancel();
        try { await pending; Check(false, "client must cancel"); } catch (OperationCanceledException) { }
        for (var i = 0; i < 100 && fake.Cancelled == before; i++) await Task.Delay(10);
        Check(fake.Cancelled > before, "client cancellation reaches Ollama");
    }
    fake.Mode = "ok";
    for (var i = 0; i < 3; i++) { using var r = await Ask("test"); Check((int)r.StatusCode == 200, "sequential isolated request"); }
    Check(!Directory.EnumerateFileSystemEntries(root).Any(), "no history/memory/profile/task files written");
    await app.StopAsync();

    await using var limited = LocalHttpService.Create(new LocalServiceOptions { Port = 0, RequestsPerMinute = 1 }, ollama);
    await limited.StartAsync();
    using var rateClient = new HttpClient { BaseAddress = new Uri(limited.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
    using (var r = await rateClient.PostAsJsonAsync("/chat", new { question = "test" })) Check((int)r.StatusCode == 200, "first rate window request");
    using (var r = await rateClient.PostAsJsonAsync("/chat", new { question = "test" })) Check((int)r.StatusCode == 429 && r.Headers.RetryAfter != null, "per IP rate limit with Retry-After");
    await limited.StopAsync();
    Console.WriteLine($"Service: {checks} checks passed.");
}
finally { Environment.CurrentDirectory = originalDirectory; Console.SetError(originalError); }

sealed class FakeOllama : HttpMessageHandler
{
    public int Calls, Cancelled;
    public bool ValidPayload;
    public string Mode = "ok";
    public TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Calls++;
        if (request.RequestUri!.ToString() != "http://127.0.0.1:11434/api/chat") throw new Exception("Nonlocal call");
        using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
        var p = json.RootElement;
        ValidPayload = p.GetProperty("model").GetString() == "qwen3:8b" && !p.GetProperty("think").GetBoolean() &&
            !p.GetProperty("stream").GetBoolean() && p.GetProperty("keep_alive").GetString() == "5m" &&
            p.GetProperty("messages").GetArrayLength() == 2 && p.GetProperty("messages")[0].GetProperty("content").GetString()!.Contains("Revit API 2024") &&
            Encoding.UTF8.GetByteCount(p.GetProperty("messages")[0].GetProperty("content").GetString()!) < 1000 &&
            p.GetProperty("options").GetProperty("num_ctx").GetInt32() == 2048 && p.GetProperty("options").GetProperty("num_predict").GetInt32() == 128 &&
            p.GetProperty("options").GetProperty("temperature").GetDouble() == 0;
        if (Mode == "wait")
        {
            Entered.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, cancellationToken); }
            catch (OperationCanceledException) { Interlocked.Increment(ref Cancelled); throw; }
        }
        if (Mode == "unavailable") throw new HttpRequestException("secret internal data");
        return new HttpResponseMessage(Mode == "error" ? HttpStatusCode.InternalServerError : HttpStatusCode.OK)
        { Content = new StringContent(Mode == "json" ? "{" : "{\"done\":true,\"message\":{\"content\":\"Короткий ответ\"},\"load_duration\":10000000,\"prompt_eval_duration\":20000000,\"eval_duration\":30000000,\"prompt_eval_count\":160,\"eval_count\":30}") };
    }
}
