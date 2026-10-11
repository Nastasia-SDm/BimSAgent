using System.Net;
using System.Reflection;
using System.Text.Json;
using BimSAgentApp;

static class LlmProviderTests
{
    public static async Task Run(Action<bool, string> check, string root)
    {
        var provider = Environment.GetEnvironmentVariable("BIMS_LLM_PROVIDER");
        var key = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        var directory = Environment.CurrentDirectory;
        try
        {
            var localRoot = Path.Combine(root, "local");
            Directory.CreateDirectory(localRoot);
            Environment.CurrentDirectory = localRoot;
            Environment.SetEnvironmentVariable("BIMS_LLM_PROVIDER", "ollama");
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", null);
            using var agent = new BimSAgent(Path.Combine(localRoot, "profiles"));
            var fake = new LocalHttp(check);
            var field = typeof(BimSAgent).GetField("_httpClient", BindingFlags.NonPublic | BindingFlags.Instance)!;
            ((HttpClient)field.GetValue(agent)!).Dispose();
            field.SetValue(agent, new HttpClient(fake));
            check(agent.LlmDescription == "Ollama / qwen3:8b", "local startup description");
            McpCommands.Events.Clear();
            check(await agent.AskWithMcpAsync("hello", 500, 0) == "local answer", "ordinary local response without OpenAI key");
            await agent.UpdateMemoryAsync();
            check(fake.Calls == 1 && fake.Formats.Count == 0 && McpCommands.Events.Count == 0,
                "ordinary local turn has one call, no catalog, planner, invariant review or memory classifier");
            check(agent.LocalLlmCalls is [{ Stage: "answer", Succeeded: true }] && agent.LocalLlmCalls[0].Duration >= TimeSpan.Zero,
                "local diagnostics contain stage, count, duration and success");
            check(fake.LastInstructions.Contains("Обязательные инварианты") && fake.LastInstructions.Contains("Revit API 2024"),
                "local instructions and invariants retained in primary call");
            check(File.ReadAllText("history.json").Contains("local answer") && File.ReadAllText("short-term-memory.json").Contains("local answer"),
                "local history and short-term memory preserved");
            foreach (var ordinary in new[] { "Как создать стену через Revit API?", "Объясни BIM", "Как сравнить модели в C#?", "hello" })
                check(!BimSAgent.RequiresMcpPlanning(ordinary), "general question bypasses local planner: " + ordinary);
            foreach (var routed in new[] { "Прочитай текущую модель Revit", "Сравни версии модели", "get-model", "compare-model-versions", "Покажи изменения в моей модели" })
                check(BimSAgent.RequiresMcpPlanning(routed), "live model request retains planner: " + routed);
            for (var server = 1; server <= 6; server++)
            {
                fake.Plan = JsonSerializer.Serialize(new { useMcp = true, steps = new[]
                    { new { server = "mcp" + server, tool = "get-model", argumentsJson = "{}" } }, reason = "fixture" });
                McpCommands.Events.Clear();
                var start = fake.Calls;
                await agent.AskWithMcpAsync("MCP" + server + " fixture", 500, 0);
                check(fake.Calls == start + 2 && agent.LastResponseUsedMcp && McpCommands.Events.SequenceEqual(new[] { "catalog", "get-model" }),
                    "explicit MCP" + server + " keeps planner and execution with one final answer");
                check(agent.LocalLlmCalls.Select(c => c.Stage).SequenceEqual(new[] { "mcp_plan", "answer" }),
                    "MCP diagnostics group both calls in one request");
            }
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", "must-not-be-sent-to-ollama");
            check(await agent.AskAsync("key present", 500, 0) == "local answer", "local remains local when cloud key is configured");
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", null);
            agent.TryHandleContextCommand("strategy sticky-facts", out _);
            var ordinaryStart = fake.Calls;
            await agent.AskWithMcpAsync("ordinary sticky question", 500, 0);
            await agent.UpdateMemoryAsync();
            check(fake.Calls == ordinaryStart + 1, "sticky-facts context needs only one local answer call");
            await agent.UpdateFactsAsync("fixture fact", 500, 0);
            check(fake.JsonMode, "local facts use JSON mode");
            var before = fake.Calls;
            try { await agent.RunContextLimitTestAsync(500, 0); throw new Exception("local cloud experiment accepted"); }
            catch (InvalidOperationException) { check(fake.Calls == before, "local context-limit experiment blocked before HTTP"); }
            fake.Fail = true;
            try { await agent.AskAsync("failure", 500, 0); throw new Exception("failure accepted"); }
            catch (InvalidOperationException) { check(fake.Calls == before + 1, "Ollama failure has no cloud fallback"); }
            check(agent.LocalLlmCalls is [{ Stage: "answer", Succeeded: false }], "failed local call also recorded");

            Environment.SetEnvironmentVariable("BIMS_LLM_PROVIDER", "unknown");
            try { using var invalid = new BimSAgent(); throw new Exception("invalid provider accepted"); }
            catch (InvalidOperationException) { check(true, "unknown provider rejected"); }
            Environment.SetEnvironmentVariable("BIMS_LLM_PROVIDER", "openai");
            using var cloud = new BimSAgent(Path.Combine(localRoot, "profiles"));
            check(cloud.LlmDescription == $"OpenAI / {BimSAgent.Model}", "cloud startup description");
            try { await cloud.AskAsync("missing key", 500, 0); throw new Exception("cloud key not required"); }
            catch (InvalidOperationException e) { check(e.Message.Contains("OPENAI_API_KEY"), "cloud still requires OpenAI key"); }
        }
        finally
        {
            Environment.CurrentDirectory = directory;
            Environment.SetEnvironmentVariable("BIMS_LLM_PROVIDER", provider);
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", key);
        }
    }

    private sealed class LocalHttp(Action<bool, string> check) : HttpMessageHandler
    {
        public int Calls;
        public bool Fail, JsonMode;
        public string Plan = "{\"useMcp\":false,\"steps\":[],\"reason\":\"ordinary\"}";
        public string LastInstructions = "";
        public HashSet<string> Formats = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            check(request.RequestUri!.AbsoluteUri == "http://127.0.0.1:11434/api/chat", "local HTTP stays on loopback Ollama");
            check(request.Headers.Authorization is null, "local HTTP sends no OpenAI credentials");
            if (Fail) return new(HttpStatusCode.ServiceUnavailable);
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var body = json.RootElement;
            check(body.GetProperty("model").GetString() == "qwen3:8b" && !body.GetProperty("stream").GetBoolean()
                && !body.GetProperty("think").GetBoolean(), "native Ollama model and non-streaming generation");
            check(body.GetProperty("messages")[0].GetProperty("role").GetString() == "system", "local preserves instructions");
            LastInstructions = body.GetProperty("messages")[0].GetProperty("content").GetString()!;
            var answer = "local answer";
            if (body.TryGetProperty("format", out var format))
            {
                if (format.ValueKind == JsonValueKind.String) { JsonMode = true; answer = "{}"; }
                else
                {
                    var properties = format.GetProperty("properties");
                    if (properties.TryGetProperty("useMcp", out _)) { Formats.Add("mcp_plan"); answer = Plan; }
                    else if (properties.TryGetProperty("changes", out _)) { Formats.Add("memory_changes"); answer = "{\"changes\":[]}"; }
                    else { Formats.Add("invariant_check"); answer = "{\"violations\":[]}"; }
                }
            }
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new
            {
                done = true, done_reason = "stop", message = new { role = "assistant", content = answer },
                prompt_eval_count = 12, eval_count = 3
            })) };
        }
    }
}
