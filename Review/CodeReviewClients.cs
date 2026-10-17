using BimSAgentApp.Review;

namespace BimSAgentApp.Rag;

public sealed partial class OpenAiRagClient
{
    internal static string ReviewModel(string fallback) => string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("BIMS_REVIEW_MODEL")) ? fallback : Environment.GetEnvironmentVariable("BIMS_REVIEW_MODEL")!.Trim();
    public async Task<string> ReviewCodeAsync(string input, CancellationToken token)
    {
        using var json = await PostAsync("responses", new
        {
            model = ReviewModel(RagDefaults.AnswerModel),
            instructions = CodeReviewPrompt.Instructions, input, store = false,
            truncation = "disabled", max_output_tokens = 4000
        }, token);
        var root = json.RootElement;
        if (!root.TryGetProperty("status", out var status) || status.GetString() != "completed")
            throw new InvalidOperationException("OpenAI не завершил review. Сократите PR или повторите запуск.");
        var parts = new List<string>();
        foreach (var output in root.GetProperty("output").EnumerateArray())
            if (output.TryGetProperty("content", out var content))
                foreach (var part in content.EnumerateArray())
                    if (part.GetProperty("type").GetString() == "output_text") parts.Add(part.GetProperty("text").GetString() ?? "");
        return string.Join("\n", parts).Trim();
    }
}

public sealed partial class OllamaRagClient
{
    public async Task<string> ReviewCodeAsync(string input, CancellationToken token)
    {
        using var json = await PostAsync("chat", new
        {
            model = OpenAiRagClient.ReviewModel("qwen3:8b"),
            stream = false, think = false, keep_alive = "2m",
            messages = new[] { new { role = "system", content = CodeReviewPrompt.Instructions }, new { role = "user", content = input } },
            options = new { temperature = 0, num_ctx = 16384, num_predict = 2400 }
        }, token);
        var root = json.RootElement;
        if (!root.GetProperty("done").GetBoolean() || root.TryGetProperty("done_reason", out var reason) && reason.GetString() == "length")
            throw new InvalidOperationException("Ollama не завершил review. Сократите PR или повторите запуск.");
        return root.GetProperty("message").GetProperty("content").GetString()?.Trim() ?? "";
    }
}
