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
            check(await agent.AskWithMcpAsync("hello", 500, 0) == "local answer", "ordinary local response without OpenAI key");
            await agent.UpdateMemoryAsync();
            check(fake.Formats.Contains("mcp_plan") && fake.Formats.Contains("invariant_check") && fake.Formats.Contains("memory_changes"),
                "local planner, invariant reviewer and post-answer memory all use Ollama");
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", "must-not-be-sent-to-ollama");
            check(await agent.AskAsync("key present", 500, 0) == "local answer", "local remains local when cloud key is configured");
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", null);
            agent.TryHandleContextCommand("strategy sticky-facts", out _);
            await agent.UpdateFactsAsync("fixture fact", 500, 0);
            check(fake.JsonMode, "local facts use JSON mode");
            var before = fake.Calls;
            try { await agent.RunContextLimitTestAsync(500, 0); throw new Exception("local cloud experiment accepted"); }
            catch (InvalidOperationException) { check(fake.Calls == before, "local context-limit experiment blocked before HTTP"); }
            fake.Fail = true;
            try { await agent.AskAsync("failure", 500, 0); throw new Exception("failure accepted"); }
            catch (InvalidOperationException) { check(fake.Calls == before + 1, "Ollama failure has no cloud fallback"); }

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
            var answer = "local answer";
            if (body.TryGetProperty("format", out var format))
            {
                if (format.ValueKind == JsonValueKind.String) { JsonMode = true; answer = "{}"; }
                else
                {
                    var properties = format.GetProperty("properties");
                    if (properties.TryGetProperty("useMcp", out _)) { Formats.Add("mcp_plan"); answer = "{\"useMcp\":false,\"steps\":[],\"reason\":\"ordinary\"}"; }
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
