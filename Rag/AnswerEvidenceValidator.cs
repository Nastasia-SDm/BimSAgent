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
        string Unknown(int i) => request.Chat == null ? RagDefaults.Unknown :
            "Не знаю. Для корректного ответа укажите дополнительно - " +
            (Regex.IsMatch(contexts[i].Question, "выгляд|внешн.*вид", RegexOptions.IgnoreCase)
                ? "читаемое изображение общего вида или разреза элемента для вопроса «"
                : Regex.IsMatch(contexts[i].Question, "параметр|размер|толщин", RegexOptions.IgnoreCase)
                ? "точное имя параметра, семейство и типоразмер, к которым относится вопрос «"
                : "категорию элемента, семейство или условия применения для вопроса «") +
            contexts[i].Question[..Math.Min(180, contexts[i].Question.Length)] + "».";
        string Render(IEnumerable<string> texts)
        {
            string Format(string text)
            {
                var lines = text
                    .Split('\n')
                    .Select(x => x.Trim())
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .ToArray();

                var contentLines = lines
                    .TakeWhile(x =>
                        !x.StartsWith("▫️ Цитаты:", StringComparison.Ordinal) &&
                        !x.StartsWith("▫️ Источники:", StringComparison.Ordinal))
                    .ToArray();

                var metadataLines = lines.Skip(contentLines.Length).ToArray();

                var result = new List<string>();

                if (contentLines.Length == 1)
                {
                    result.Add($"➤ {contentLines[0]}");
                }
                else
                {
                    for (var i = 0; i < contentLines.Length; i++)
                        result.Add(i == 0
                            ? $"➤ {i + 1}. {contentLines[i]}"
                            : $"  {i + 1}. {contentLines[i]}");
                }

                result.AddRange(metadataLines);

    return string.Join(Environment.NewLine, result);
}

            return string.Join(Environment.NewLine, texts.Select(Format));
        }
        try
        {
            using var json = JsonDocument.Parse(payload);
            var answers = json.RootElement.GetProperty("answers").EnumerateArray().ToArray();
            if (answers.Length != contexts.Count) return Render(contexts.Select((_, i) => Unknown(i)));
            var rendered = new List<string>();
            for (var i = 0; i < answers.Length; i++)
            {
                var item = answers[i];
                if (item.GetProperty("number").GetInt32() != i + 1) { rendered.Add(Unknown(i)); continue; }
                var text = item.GetProperty("text").GetString()?.Trim() ?? "";
                var status = item.GetProperty("status").GetString();
                if (text.Length == 0 || status is not ("supported" or "uncertain" or "unknown"))
                { rendered.Add(Unknown(i)); continue; }
                if (request.NoRag) { rendered.Add(text); continue; }
                if (status == "unknown")
                {
                    const string prefix = "Не знаю. Для корректного ответа укажите дополнительно - ";
                    var specific = text.StartsWith(prefix, StringComparison.Ordinal) && text.Length > prefix.Length + 12
                        && !Regex.IsMatch(text, "нужн[а-я]* (больше информации|дополнительные данные)", RegexOptions.IgnoreCase);
                    rendered.Add(request.Chat == null || specific ? text : Unknown(i)); continue;
                }
                var evidence = item.GetProperty("evidence").EnumerateArray().ToArray();
                var valid = evidence.Length > 0 && (request.Chat == null || !text.Contains("chunk_id", StringComparison.OrdinalIgnoreCase));
                var verified = new List<(string ChunkId, string Quote)>();
                var visualAnswer = false;

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
                        // A visible observation is usable with an explicit machine-recognition caveat.
                        // Keep the stricter rule for dimensions, parameters and other technical claims.
                        var appearance = ChatVisualContext.IsAppearance(contexts[i].Question);
                        if (!appearance || !hit.Chunk.VisualEvidence.Any(v => Normalize(v).Contains(quote, StringComparison.Ordinal)))
                        { valid = false; break; }
                        visualAnswer = true;
                    }

                    verified.Add((hit.Chunk.ChunkId, rawQuote));
                    visualAnswer |= hit.Chunk.VisualEvidence.Any(v => Normalize(v).Contains(quote, StringComparison.Ordinal));
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
                    rendered.Add(Unknown(i));
                    continue;
                }
                if (visualAnswer && !text.Contains("распознан", StringComparison.OrdinalIgnoreCase))
                    text = "По распознанному изображению: " + text;

                var chunkIds = verified
                    .Select(v => v.ChunkId)
                    .Distinct()
                    .Select(id => $"chunk_id: {id}");

                var quotes = verified
                    .Select(v => $"\"{v.Quote}\"");

                rendered.Add(
     text +
     Environment.NewLine + "▫️ Цитаты:" +
     Environment.NewLine + string.Join(Environment.NewLine, quotes) +
     Environment.NewLine + "▫️ Источники:" +
     Environment.NewLine + string.Join(Environment.NewLine, chunkIds));
            }
            return Render(rendered);
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        { return Render(contexts.Select((_, i) => Unknown(i))); }
    }
    private static string Normalize(string text) => Regex.Replace(text.Normalize(), @"\s+", " ").Trim();
}
