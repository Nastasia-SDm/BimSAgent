using System.Text.Json;
using BimSAgentApp.Rag;

static class ContextReplayTests
{
    // Replay saved real model scores to isolate context assembly from API/model variability.
    public static void Run(Action<bool, string> check, string reportPath)
    {
        var index = new JsonIndexStore(RagDefaults.DataDirectory).Load("structural")!;
        var chunks = index.Chunks.ToDictionary(c => c.ChunkId);
        using var cases = JsonDocument.Parse(File.ReadAllText("tests/Rag/quality-cases.json"));
        using var report = JsonDocument.Parse(File.ReadAllText(reportPath).TrimStart('\uFEFF'));
        foreach (var result in report.RootElement.EnumerateArray())
        {
            var question = result.GetProperty("question").GetString();
            var specification = cases.RootElement.EnumerateArray().Single(c => c.GetProperty("question").GetString() == question);
            var candidates = result.GetProperty("diagnostics").GetProperty("questions")[0].GetProperty("candidates")
                .EnumerateArray().Where(c => chunks.ContainsKey(c.GetProperty("chunk_id").GetString()!)).Select(c =>
                    new RetrievalHit(chunks[c.GetProperty("chunk_id").GetString()!], c.GetProperty("similarity_score").GetDouble())
                    {
                        Selected = c.GetProperty("selected").GetBoolean() || c.GetProperty("selection_reason").GetString() == "token_budget"
                    }).ToArray();
            var context = new ContextAssembler(new()).Assemble(new(candidates, index.Chunks.Length), 6000, index);
            var names = context.Candidates.Where(c => c.Selected).SelectMany(c => c.Chunk.FamilyNames).ToHashSet();
            check(specification.GetProperty("expected").EnumerateArray().All(n => names.Contains(n.GetString()!)),
                "real-score context replay preserves requested families: " + question);
            if (specification.TryGetProperty("expect_empty", out var empty) && empty.GetBoolean())
                check(!context.Candidates.Any(c => c.Selected), "real-score unknown query has no context");
        }
    }
}
