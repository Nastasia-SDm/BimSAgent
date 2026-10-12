using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;

namespace BimSAgentApp;

public sealed record LocalServiceOptions
{
    public string Host { get; init; } = "127.0.0.1";
    public int Port { get; init; } = 5080;
    public int MaxBodyBytes { get; init; } = 16384;
    public int MaxQuestionChars { get; init; } = 2000;
    public int ContextTokens { get; init; } = 2048;
    public int MaxOutputTokens { get; init; } = 128;
    public int RequestsPerMinute { get; init; } = 6;
    public int TimeoutSeconds { get; init; } = 180;

    public static LocalServiceOptions Parse(string[] args)
    {
        var o = new LocalServiceOptions();
        for (var i = 0; i < args.Length; i += 2)
        {
            if (i + 1 == args.Length) throw new ArgumentException("Missing option value.");
            var value = args[i + 1];
            o = args[i] switch
            {
                "--host" => o with { Host = value },
                "--port" => o with { Port = int.Parse(value) },
                "--max-body-bytes" => o with { MaxBodyBytes = int.Parse(value) },
                "--max-question-chars" => o with { MaxQuestionChars = int.Parse(value) },
                "--context-tokens" => o with { ContextTokens = int.Parse(value) },
                "--max-output-tokens" => o with { MaxOutputTokens = int.Parse(value) },
                "--requests-per-minute" => o with { RequestsPerMinute = int.Parse(value) },
                "--timeout-seconds" => o with { TimeoutSeconds = int.Parse(value) },
                _ => throw new ArgumentException("Unknown service option.")
            };
        }
        o.Validate();
        return o;
    }

    public void Validate()
    {
        if (!IPAddress.TryParse(Host, out _) || Port is < 0 or > 65535 ||
            MaxBodyBytes is < 128 or > 1048576 || MaxQuestionChars is < 1 or > 100000 ||
            ContextTokens is < 512 or > 4096 || MaxOutputTokens is < 1 or > 768 ||
            RequestsPerMinute is < 1 or > 1000 || TimeoutSeconds is < 1 or > 600 ||
            Encoding.UTF8.GetByteCount(LocalHttpService.Instructions) + 64 + MaxOutputTokens >= ContextTokens)
            throw new ArgumentException("Invalid limits: context must fit the complete system prompt, output reserve and message overhead (64). Context maximum: 4096.");
    }
}

public static class LocalHttpService
{
    // Stateless service has no profiles, memory, Revit connection or document sources.
    // Keep its rules here: changing these must not change console, cloud or RAG prompts.
    internal const string Instructions = """
        You are BimSAgent for Revit/BIM, Revit API 2024, C# and Dynamo.
        Reply in the user's language in 1–3 short sentences or a short action list.
        Answer directly: no introductions, repetition or unsolicited alternatives.
        Use UI steps by default; API, code and details only if explicitly requested.
        Never invent facts, APIs or sources. Keep exact names and versions; flag errors and uncertainty.
        You cannot access Revit or documents: do not claim checks or execution. Briefly ask for missing data.
        For requested API code respect Revit 2024, transactions, units and threads; respect Dynamo versions.
        """;
    private static long _requestId;
    public static WebApplication Create(LocalServiceOptions options, HttpClient ollama)
    {
        options.Validate();
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [] });
        builder.Logging.ClearProviders(); // Request content and exception details never enter default logs.
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.Listen(IPAddress.Parse(options.Host), options.Port);
            k.Limits.MaxRequestBodySize = options.MaxBodyBytes;
        });
        var app = builder.Build();
        var gate = new SemaphoreSlim(1, 1);
        var rates = new Dictionary<IPAddress, (long Window, int Count)>();
        var rateLock = new object();
        app.MapPost("/chat", async (HttpContext ctx) =>
        {
            var elapsed = Stopwatch.StartNew();
            var id = Interlocked.Increment(ref _requestId);
            var calls = 0;
            void Stage(string stage, string details = "") => Console.Error.WriteLine($"[local-http] request={id} stage={stage} elapsed_ms={elapsed.ElapsedMilliseconds} {details}");
            Stage("received", $"timeout_s={options.TimeoutSeconds}");
            ctx.Response.OnCompleted(() =>
            {
                Stage("http_completed", $"status={ctx.Response.StatusCode} model_calls={calls}");
                return Task.CompletedTask;
            });
            var acquired = false;
            string? errorCategory = null;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted, app.Lifetime.ApplicationStopping);
            deadline.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));
            async Task Error(int status, string error)
            {
                errorCategory = error;
                ctx.Response.StatusCode = status;
                await ctx.Response.WriteAsJsonAsync(new { error }, ctx.RequestAborted);
            }
            try
            {
                var ip = ctx.Connection.RemoteIpAddress ?? IPAddress.None;
                var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                var allowed = false;
                lock (rateLock)
                {
                    foreach (var key in rates.Where(p => now - p.Value.Window >= 60).Select(p => p.Key).ToArray()) rates.Remove(key);
                    if (!rates.TryGetValue(ip, out var entry)) entry = (now, 0);
                    // Bound tracking as well as generation; do not trust forwarded IP headers.
                    if (rates.Count < 4096 || rates.ContainsKey(ip))
                    {
                        allowed = entry.Count < options.RequestsPerMinute;
                        rates[ip] = (entry.Window, entry.Count + (allowed ? 1 : 0));
                    }
                    if (!allowed) ctx.Response.Headers.RetryAfter = Math.Max(1, 60 - (now - entry.Window)).ToString();
                }
                if (!allowed) { await Error(429, "Rate limit exceeded. Retry later."); return; }
                if (!ctx.Request.HasJsonContentType()) { await Error(415, "Content-Type must be application/json."); return; }
                if (ctx.Request.ContentLength > options.MaxBodyBytes) { await Error(413, "Request body is too large."); return; }
                using var body = new MemoryStream();
                var buffer = new byte[4096];
                int read;
                while ((read = await ctx.Request.Body.ReadAsync(buffer, deadline.Token)) > 0)
                {
                    if (body.Length + read > options.MaxBodyBytes) { await Error(413, "Request body is too large."); return; }
                    body.Write(buffer, 0, read);
                }
                using var json = JsonDocument.Parse(body.ToArray(), new JsonDocumentOptions { MaxDepth = 8 });
                if (json.RootElement.ValueKind != JsonValueKind.Object ||
                    !json.RootElement.TryGetProperty("question", out var q) || q.ValueKind != JsonValueKind.String ||
                    string.IsNullOrWhiteSpace(q.GetString())) { await Error(400, "A nonempty string question is required."); return; }
                var question = q.GetString()!.Trim();
                if (question.Length > options.MaxQuestionChars) { await Error(413, "Question is too long."); return; }
                // UTF-8 bytes are a conservative token upper bound, independent of Qwen tokenizer.
                // Reject rather than silently truncate the instructions or user input.
                if (Encoding.UTF8.GetByteCount(Instructions) + Encoding.UTF8.GetByteCount(question) + 64 + options.MaxOutputTokens > options.ContextTokens)
                { await Error(413, "Full input exceeds the context budget. Shorten the question."); return; }
                acquired = await gate.WaitAsync(0, deadline.Token);
                if (!acquired) { ctx.Response.Headers.RetryAfter = "5"; await Error(429, "Generation is busy. Retry after the current answer."); return; }
                using var request = new HttpRequestMessage(HttpMethod.Post, "http://127.0.0.1:11434/api/chat")
                {
                    Content = JsonContent.Create(new
                    {
                        model = "qwen3:8b", stream = false, think = false, keep_alive = "5m",
                        messages = new[] { new { role = "system", content = Instructions }, new { role = "user", content = question } },
                        options = new { temperature = 0.0, num_ctx = options.ContextTokens, num_predict = options.MaxOutputTokens, num_batch = 128, num_thread = Math.Min(Environment.ProcessorCount, 4) }
                    })
                };
                calls++;
                var modelElapsed = Stopwatch.StartNew();
                Stage("ollama_sent", $"model_calls={calls} system_chars={Instructions.Length} system_utf8_bytes={Encoding.UTF8.GetByteCount(Instructions)} question_chars={question.Length} input_utf8_bytes={Encoding.UTF8.GetByteCount(Instructions) + Encoding.UTF8.GetByteCount(question)} num_ctx={options.ContextTokens} num_predict={options.MaxOutputTokens} think=false keep_alive=5m");
                using var response = await ollama.SendAsync(request, deadline.Token);
                Stage("ollama_received", $"status={(int)response.StatusCode} ollama_elapsed_ms={modelElapsed.ElapsedMilliseconds}");
                if (!response.IsSuccessStatusCode) { await Error(502, "Ollama returned an error."); return; }
                using var result = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(deadline.Token), cancellationToken: deadline.Token);
                var root = result.RootElement;
                long Metric(string name) => root.TryGetProperty(name, out var value) && value.TryGetInt64(out var number) ? number : 0;
                Stage("ollama_metrics", $"load_ms={Metric("load_duration") / 1000000} prompt_ms={Metric("prompt_eval_duration") / 1000000} decode_ms={Metric("eval_duration") / 1000000} input_tokens={Metric("prompt_eval_count")} output_tokens={Metric("eval_count")}");
                if (!root.TryGetProperty("done", out var done) || done.ValueKind != JsonValueKind.True ||
                    !root.TryGetProperty("message", out var message) || !message.TryGetProperty("content", out var content) ||
                    content.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(content.GetString()))
                { await Error(502, "Ollama returned an invalid answer."); return; }
                await ctx.Response.WriteAsJsonAsync(new { answer = content.GetString() }, ctx.RequestAborted);
                Stage("http_written", $"status={ctx.Response.StatusCode}");
            }
            catch (BadHttpRequestException ex) { await Error(ex.StatusCode, "Invalid or oversized HTTP request."); }
            catch (JsonException) { await Error(acquired ? 502 : 400, acquired ? "Ollama returned invalid JSON." : "Invalid JSON request."); }
            catch (HttpRequestException) { await Error(503, "Ollama is unavailable."); }
            catch (OperationCanceledException)
            {
                if (ctx.RequestAborted.IsCancellationRequested) { ctx.Response.StatusCode = 499; errorCategory = "Client cancelled."; }
                else if (app.Lifetime.ApplicationStopping.IsCancellationRequested) await Error(503, "Service is stopping.");
                else await Error(504, "Ollama request timed out.");
            }
            catch (Exception) { if (!ctx.Response.HasStarted) await Error(500, "Service could not complete the request."); }
            finally
            {
                if (acquired) gate.Release();
                Stage("finished", $"status={ctx.Response.StatusCode} model_calls={calls} error={errorCategory ?? "none"}");
            }
        });
        return app;
    }

    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        try
        {
            var options = LocalServiceOptions.Parse(args);
            using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
            await using var app = Create(options, client);
            Console.WriteLine($"Listening: http://{options.Host}:{options.Port}; LLM: Ollama / qwen3:8b");
            Console.WriteLine($"Service timeout: {options.TimeoutSeconds}s; context: {options.ContextTokens}; output: {options.MaxOutputTokens}; system UTF-8 bytes: {Encoding.UTF8.GetByteCount(Instructions)}");
            foreach (var ip in NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses).Select(a => a.Address)
                .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a)))
                Console.WriteLine($"LAN example (use a reachable laptop IPv4 and bind --host 0.0.0.0): http://{ip}:{options.Port}/chat");
            await ((IHost)app).RunAsync(cancellationToken);
            return 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return 0; }
        catch (Exception) { Console.Error.WriteLine("Service failed to start. Check arguments and port availability."); return 1; }
    }
}
