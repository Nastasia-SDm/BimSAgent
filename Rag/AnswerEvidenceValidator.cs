using System.Text.Json;
using System.Text.RegularExpressions;

namespace BimSAgentApp.Rag;

public static class AnswerEvidenceValidator
{
    public static readonly JsonElement Schema = JsonDocument.Parse("""
    {"type":"object","additionalProperties":false,"required":["answers"],"properties":{"answers":{"type":"array","items":{
      "type":"object","additionalProperties":false,"required":["number","text","status","evidence"],"properties":{
        "number":{"type":"integer"},"text":{"type":"string"},"status":{"type":"string","enum":["supported","uncertain","unknown"]},
        "evidence":{"type":"array","items":{"type":"object","additionalProperties":false,"required":["chunk_id","quote"],
          "properties":{"chunk_id":{"type":"string"},"quote":{"type":"string"}}}}
      }}}}}
    """).RootElement.Clone();

    // Referential/quote validation is deterministic; it is not semantic entailment verification.
    public static string ValidateAndRender(string payload, RagAnswerRequest request)
    {
        var contexts = request.Questions ?? [new RagQuestionContext(request.Question, request.Context)];
        string Render(IEnumerable<string> texts) => string.Join(Environment.NewLine, texts.Select((s, i) => contexts.Count > 1 ? $"⬪ {i + 1}. {s}" : s));
        try
        {
            using var json = JsonDocument.Parse(payload);
            var answers = json.RootElement.GetProperty("answers").EnumerateArray().ToArray();
            if (answers.Length != contexts.Count) return Render(contexts.Select(_ => RagDefaults.Unknown));
            var rendered = new List<string>();
            for (var i = 0; i < answers.Length; i++)
            {
                var item = answers[i];
                if (item.GetProperty("number").GetInt32() != i + 1) { rendered.Add(RagDefaults.Unknown); continue; }
                var text = item.GetProperty("text").GetString()?.Trim() ?? "";
                var status = item.GetProperty("status").GetString();
                if (text.Length == 0 || status is not ("supported" or "uncertain" or "unknown"))
                { rendered.Add(RagDefaults.Unknown); continue; }
                if (request.NoRag) { rendered.Add(text); continue; }
                if (status == "unknown") { rendered.Add(RagDefaults.Unknown); continue; }
                var evidence = item.GetProperty("evidence").EnumerateArray().ToArray();
                var valid = evidence.Length > 0;
                foreach (var citation in evidence)
                {
                    var id = citation.GetProperty("chunk_id").GetString();
                    var quote = Normalize(citation.GetProperty("quote").GetString() ?? "");
                    var hit = contexts[i].Context.FirstOrDefault(h => h.Chunk.ChunkId == id);
                    if (hit == null || quote.Length < 8) { valid = false; break; }
                    var sources = new[] { hit.Chunk.Text, hit.Chunk.FamilyPurpose }.Concat(hit.Chunk.InheritedContext.Select(b => b.Text))
                        .Concat(hit.Chunk.Tables.Select(t => string.Join(" | ", t.Headers)));
                    if (!sources.Any(s => Normalize(s).Contains(quote, StringComparison.Ordinal))) { valid = false; break; }
                    if (status == "supported" && hit.Chunk.Warnings.Contains("machine_extracted_unverified")) { valid = false; break; }
                }
                var names = contexts[i].Context.SelectMany(h => h.Chunk.FamilyNames).Distinct().ToArray();
                var prefixes = names.Select(n => n.Split('_')[0] + "_").Distinct().ToArray();
                foreach (Match match in Regex.Matches(text, @"[\p{L}\p{N}][\p{L}\p{N}_№.\-]*_[\p{L}\p{N}_№.\-]+"))
                {
                    var name = match.Value.TrimEnd('.');
                    if (prefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase))
                        && !names.Any(n => n == name || n.StartsWith(name + " ", StringComparison.Ordinal))) valid = false;
                }
                rendered.Add(valid ? (status == "uncertain" ? "Требуется проверка по источнику. " : "") + text : RagDefaults.Unknown);
            }
            return Render(rendered);
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        { return Render(contexts.Select(_ => RagDefaults.Unknown)); }
    }
    private static string Normalize(string text) => Regex.Replace(text.Normalize(), @"\s+", " ").Trim();
}
