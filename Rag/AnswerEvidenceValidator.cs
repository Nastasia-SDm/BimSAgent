using System.Text.Json;
using System.Text.RegularExpressions;

namespace BimSAgentApp.Rag;

public static class AnswerEvidenceValidator
{
    public static readonly JsonElement Schema = JsonDocument.Parse("""
{"type":"object","additionalProperties":false,"required":["answers"],"properties":{"answers":{"type":"array","items":{
  "type":"object","additionalProperties":false,"required":["number","text","status","evidence"],"properties":{
    "number":{"type":"integer"},"text":{"type":"string"},"status":{"type":"string","enum":["supported","uncertain","unknown"]},
    "evidence":{"type":"array","items":{"type":"object","additionalProperties":false,"required":["quote"],
      "properties":{"quote":{"type":"string"}}}}
  }}}}}
""").RootElement.Clone();

    // Referential/quote validation is deterministic; it is not semantic entailment verification.
    public static string ValidateAndRender(string payload, RagAnswerRequest request)
    {
        var contexts = request.Questions ?? [new RagQuestionContext(request.Question, request.Context)];
        string Render(IEnumerable<string> texts) =>
        string.Join(Environment.NewLine, texts.Select((s, i) => $"⬪ {i + 1}. {s}"));
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
                if (status == "unknown") { rendered.Add(text); continue; }
                var evidence = item.GetProperty("evidence").EnumerateArray().ToArray();
                var valid = evidence.Length > 0;
                var verified = new List<(string ChunkId, string Quote)>();

                foreach (var citation in evidence)
                {
                    var rawQuote = citation.GetProperty("quote").GetString()?.Trim() ?? "";
                    var quote = Normalize(rawQuote);

                    if (quote.Length < 8) { valid = false; break; }

                    var hit = contexts[i].Context.FirstOrDefault(h =>
                    {
                        var sources = new[] { h.Chunk.Text, h.Chunk.FamilyPurpose }
                            .Concat(h.Chunk.InheritedContext.Select(b => b.Text))
                            .Concat(h.Chunk.Tables.Select(t => string.Join(" | ", t.Headers)));

                        return sources.Any(s => Normalize(s).Contains(quote, StringComparison.Ordinal));
                    });

                    if (hit == null) { valid = false; break; }
                    if (status == "supported" && hit.Chunk.Warnings.Contains("machine_extracted_unverified"))
                    {
                        valid = false;
                        break;
                    }

                    verified.Add((hit.Chunk.ChunkId, rawQuote));
                }

            var names = contexts[i].Context.SelectMany(h => h.Chunk.FamilyNames).Distinct().ToArray();
                var prefixes = names.Select(n => n.Split('_')[0] + "_").Distinct().ToArray();
                foreach (Match match in Regex.Matches(text, @"[\p{L}\p{N}][\p{L}\p{N}_№.\-]*_[\p{L}\p{N}_№.\-]+"))
                {
                    var name = match.Value.TrimEnd('.');
                    if (prefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase))
                        && !names.Any(n => n == name || n.StartsWith(name + " ", StringComparison.Ordinal))) valid = false;
                }
                if (!valid)
                {
                    rendered.Add(RagDefaults.Unknown);
                    continue;
                }

                var chunkIds = verified
                    .Select(v => v.ChunkId)
                    .Distinct()
                    .Select(id => $"chunk_id: {id}");

                var quotes = verified
                    .Select(v => $"\"{v.Quote}\"");

                rendered.Add(
                    text +
                    Environment.NewLine + "▫️ Источники:" +
                    Environment.NewLine + string.Join(Environment.NewLine, chunkIds) +
                    Environment.NewLine + "▫️ Цитаты:" +
                    Environment.NewLine + string.Join(Environment.NewLine, quotes));
            }
            return Render(rendered);
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        { return Render(contexts.Select(_ => RagDefaults.Unknown)); }
    }
    private static string Normalize(string text) => Regex.Replace(text.Normalize(), @"\s+", " ").Trim();
}
