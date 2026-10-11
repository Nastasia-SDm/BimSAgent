using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace BimSAgentApp.Rag;

// The model selects evidence IDs instead of spending CPU time retyping Russian quotations.
// IDs are resolved to literal current-context spans before the existing quote validator runs.
internal sealed class LocalRagEvidence
{
    private readonly Dictionary<int, string> _quotes = [];
    private readonly List<int[]> _questionIds = [];

    public object Input(RagAnswerRequest request)
    {
        var groups = request.Questions ?? [new RagQuestionContext(request.Question, request.Context)];
        var questions = new List<object>();
        foreach (var group in groups)
        {
            var ids = new List<int>();
            var fragments = new List<object>();
            foreach (var hit in group.Context)
            {
                var lines = new StringBuilder();
                foreach (Match match in Regex.Matches(hit.Chunk.Text, @"[^\r\n]+", RegexOptions.Multiline))
                {
                    var span = match.Value.Trim();
                    if (span.Length == 0) continue;
                    // Bound each quote without losing any context: long sentences get consecutive spans.
                    for (var start = 0; start < span.Length;)
                    {
                        var length = Math.Min(220, span.Length - start);
                        if (start + length < span.Length)
                        {
                            var space = span.LastIndexOf(' ', start + length - 1, length);
                            if (space > start + 80) length = space - start + 1;
                        }
                        if (char.IsHighSurrogate(span[start + length - 1])) length--;
                        if (length == 0) throw new InvalidDataException("Некорректный Unicode в RAG-контексте.");
                        var quote = span.Substring(start, length).Trim();
                        var id = _quotes.Count + 1;
                        _quotes.Add(id, quote);
                        ids.Add(id);
                        lines.Append('[').Append(id).Append("] ").AppendLine(quote);
                        start += length;
                    }
                }
                fragments.Add(new { section = hit.Chunk.Section[..Math.Min(240, hit.Chunk.Section.Length)], text = lines.ToString().TrimEnd() });
            }
            _questionIds.Add(ids.ToArray());
            questions.Add(new { number = questions.Count + 1, question = group.Question, fragments });
        }
        var input = new Dictionary<string, object> { ["questions"] = questions };
        if (request.Chat is { } chat)
        {
            input["history"] = chat.History.TakeLast(1).Where(h => h.User.Length + h.Assistant.Length <= 800).ToArray();
            if (chat.Task is { } task) input["task"] = task;
        }
        return input;
    }

    public JsonNode Schema(bool detailed)
    {
        var schema = JsonNode.Parse(AnswerEvidenceValidator.Schema.GetRawText())!;
        var answers = schema["properties"]!["answers"]!;
        answers["minItems"] = _questionIds.Count;
        answers["maxItems"] = _questionIds.Count;
        var properties = answers["items"]!["properties"]!;
        properties["text"]!["maxLength"] = detailed ? 1200 : 420;
        properties["evidence"] = new JsonObject
        {
            ["type"] = "array", ["maxItems"] = detailed ? 4 : 2,
            ["items"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = Math.Max(1, _quotes.Count) }
        };
        return schema;
    }

    public string Resolve(string payload)
    {
        using var json = JsonDocument.Parse(payload);
        var answers = json.RootElement.GetProperty("answers").EnumerateArray().ToArray();
        if (answers.Length != _questionIds.Count) throw new InvalidDataException("Ollama вернул неверное число RAG-ответов.");
        var resolved = answers.Select((answer, i) =>
        {
            var evidence = answer.GetProperty("evidence").EnumerateArray().Select(e =>
            {
                var id = e.GetInt32();
                if (!_questionIds[i].Contains(id) || !_quotes.TryGetValue(id, out var quote))
                    throw new InvalidDataException("Ollama сослался на отсутствующий фрагмент текущего вопроса.");
                return new { quote };
            }).ToArray();
            return new { number = answer.GetProperty("number").GetInt32(), text = answer.GetProperty("text").GetString(),
                status = answer.GetProperty("status").GetString(), evidence };
        }).ToArray();
        return JsonSerializer.Serialize(new { answers = resolved }, JsonIndexStore.JsonOptions);
    }
}
