using System.Text.Json;
using System.Text.RegularExpressions;

namespace BimSAgentApp.Rag;

public sealed record RagChatFact(string Value, string Evidence, int Turn);
public sealed record RagChatState
{
    public RagChatFact? Goal { get; init; }
    public RagChatFact[] Clarifications { get; init; } = [];
    public RagChatFact[] Constraints { get; init; } = [];
    public RagChatFact[] Terms { get; init; } = [];
    public RagChatFact[] Selections { get; init; } = [];
    public RagChatFact[] Decisions { get; init; } = [];
}
public sealed record RagChatTurn(int Number, string User, string Answer, string HistoryAnswer, DateTimeOffset At)
{
    public string[] RetrievalQueries { get; init; } = [];
}
public sealed record RagChatSession(string Id, int? TaskId, RagChatState State, RagChatTurn[] Turns)
{
    public int Version { get; init; } = 1;
}
public sealed record RagChatHistory(string User, string Assistant);
public sealed record RagChatContext(RagChatState State, IReadOnlyList<RagChatHistory> History, JsonElement? Task);
public sealed record RagChatChange(string Category, string Action, string Value, string Evidence);
public sealed record RagChatPlan(string[] Queries, RagChatChange[] Changes);
public interface IRagChatPlanner
{
    Task<RagChatPlan> PlanChatAsync(IReadOnlyList<string> questions, string message, RagChatContext context, CancellationToken token);
}

public sealed class RagChatStore(string directory)
{
    private string PathFor(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new ArgumentException("ID RAG-чата должен быть GUID из /state.");
        return Path.Combine(directory, id + ".json");
    }
    public IDisposable Lock()
    {
        Directory.CreateDirectory(directory);
        try { return new FileStream(Path.Combine(directory, "chat.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { throw new InvalidOperationException("RAG-чат уже открыт в другом процессе."); }
    }
    public RagChatSession Open(bool startNew, string? id, int? taskId)
    {
        Directory.CreateDirectory(directory);
        var active = Path.Combine(directory, "active.json");
        if (!startNew && id == null && File.Exists(active))
            id = JsonSerializer.Deserialize<string>(File.ReadAllText(active));
        if (!startNew && id != null)
        {
            var session = JsonSerializer.Deserialize<RagChatSession>(File.ReadAllText(PathFor(id)), JsonIndexStore.JsonOptions);
            if (session == null || session.Version != 1 || session.Id != id || session.State == null || !Valid(session.State) || session.Turns == null
                || session.Turns.Where((t, i) => t == null || t.Number != i + 1 || t.User == null || t.Answer == null || t.HistoryAnswer == null).Any())
                throw new InvalidDataException("Повреждён RAG-чат. Исходный файл не изменён; начните --new или укажите --resume <id>.");
            var result = session with { TaskId = taskId ?? session.TaskId };
            Save(result);
            return result;
        }
        var created = new RagChatSession(Guid.NewGuid().ToString("N"), taskId, new(), []);
        Save(created);
        return created;
    }
    public void Save(RagChatSession session)
    {
        Directory.CreateDirectory(directory);
        Write(PathFor(session.Id), session);
        Write(Path.Combine(directory, "active.json"), session.Id);
    }
    private static bool Valid(RagChatState state) => new[] { state.Clarifications, state.Constraints, state.Terms, state.Selections, state.Decisions }
        .All(items => items != null && items.Length <= 32 && items.All(f => f != null && !string.IsNullOrWhiteSpace(f.Value)
            && f.Value.Length <= 600 && !string.IsNullOrWhiteSpace(f.Evidence) && f.Turn > 0))
        && (state.Goal == null || !string.IsNullOrWhiteSpace(state.Goal.Value));
    private static void Write<T>(string path, T value)
    {
        var temp = path + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, value, JsonIndexStore.JsonOptions);
            stream.Flush(true);
        }
        File.Move(temp, path, true);
    }
}

public sealed class RagChatEngine(RagService rag, IRagChatPlanner planner, Func<int, JsonElement> readTask)
{
    public async Task<(RagChatSession Session, RagAnswer Answer, string[] Queries)> TurnAsync(
        RagChatSession session, string message, string strategy, int? topK, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(message) || new RagTokenizer().Count(message) > 6000)
            throw new ArgumentException("Введите вопрос не длиннее 6000 токенов.");
        // Task state has a single owner: the existing task subsystem. Read it afresh, never save a copy as a new task.
        var task = session.TaskId is { } id ? readTask(id) : (JsonElement?)null;
        var history = session.Turns.TakeLast(12).Select(t => new RagChatHistory(t.User, t.HistoryAnswer)).ToArray();
        var context = new RagChatContext(session.State, history, task);
        var questions = QuestionParser.Split(message);
        var tokenizer = new RagTokenizer();
        while (context.History.Count > 1 && tokenizer.Count(JsonSerializer.Serialize(new { questions, message, context }, JsonIndexStore.JsonOptions)) > 22000)
            context = context with { History = context.History.Skip(1).ToArray() };
        var plan = await planner.PlanChatAsync(questions, message, context, token);
        if (plan.Queries == null || plan.Queries.Length != questions.Count || plan.Queries.Any(string.IsNullOrWhiteSpace) || plan.Changes == null)
            throw new InvalidDataException($"Некорректное уточнение запросов: ожидалось {questions.Count}, получено {plan.Queries?.Length.ToString() ?? "null"}; изменений {plan.Changes?.Length.ToString() ?? "null"}. Чат не изменён.");
        var state = Apply(session.State, plan.Changes.Concat(ExplicitChanges(message)), message, session.Turns.Length + 1);
        if (tokenizer.Count(JsonSerializer.Serialize(state, JsonIndexStore.JsonOptions)) > 8000)
            throw new InvalidOperationException("Состояние превысило 8000 токенов. Укажите, какие устаревшие уточнения или решения отменить; ход не сохранён.");
        var queries = plan.Queries.Select((q, i) => ResolveQuery(questions[i], q, state)).ToArray();
        var answer = await rag.AskChatAsync(message, queries, context with { State = state }, strategy, topK, token);
        var turn = new RagChatTurn(session.Turns.Length + 1, message, answer.Text, HistoryText(answer.Text), DateTimeOffset.UtcNow)
        { RetrievalQueries = queries };
        return (session with { State = state, Turns = [.. session.Turns, turn] }, answer, queries);
    }

    public static string ResolveQuery(string original, string query, RagChatState state)
    {
        var familyReference = Regex.IsMatch(original, @"(?:эт\p{L}*|выбранн\p{L}*)\s+семейств", RegexOptions.IgnoreCase);
        var parameterReference = Regex.IsMatch(original, @"(?:тот|того|тому|тем|том|эт\p{L}*)\s+параметр", RegexOptions.IgnoreCase);
        if (!familyReference && !parameterReference) return query;
        var anchors = new List<string>();
        if (familyReference || parameterReference) anchors.AddRange(state.Selections.Select(f => f.Value));
        if (parameterReference) anchors.AddRange(state.Terms.Concat(state.Clarifications)
            .Where(f => f.Value.Contains("параметр", StringComparison.OrdinalIgnoreCase)).Select(f => f.Value));
        // Explicit anchors are only search hints, never evidence. Preserve alternatives;
        // the answer model must clarify an ambiguous reference instead of guessing.
        var missing = anchors.Distinct(StringComparer.OrdinalIgnoreCase).Where(v => !query.Contains(v, StringComparison.OrdinalIgnoreCase)).ToArray();
        return missing.Length == 0 ? query : query + "\nУточнения из состояния чата: " + string.Join("; ", missing);
    }

    public static RagChatState Apply(RagChatState state, IEnumerable<RagChatChange> changes, string message, int turn)
    {
        var result = state;
        foreach (var change in changes)
        {
            if (change == null || string.IsNullOrWhiteSpace(change.Value) || change.Value.Length > 600)
                throw new InvalidDataException("Некорректное значение изменения состояния. Чат не изменён.");
            if (change.Action is not ("add" or "remove" or "set"))
                throw new InvalidDataException("Некорректная операция изменения состояния. Чат не изменён.");
            if (string.IsNullOrWhiteSpace(change.Evidence) || change.Evidence.Length > 600 || !message.Contains(change.Evidence, StringComparison.Ordinal))
                throw new InvalidDataException($"Подтверждение поля {change.Category} не совпадает с текущим сообщением. Чат не изменён.");
            var fact = new RagChatFact(change.Value.Trim(), change.Evidence, turn);
            RagChatFact[] Update(RagChatFact[] old)
            {
                if (change.Action == "set") throw new InvalidDataException("Списки состояния изменяются только add/remove.");
                if (change.Action == "add" && old.Any(f => f.Value.Equals(fact.Value, StringComparison.OrdinalIgnoreCase))) return old;
                var items = old.Where(f => !f.Value.Equals(fact.Value, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (change.Action == "add") items = [.. items, fact];
                if (items.Length > 32) throw new InvalidOperationException("В разделе состояния уже 32 записи. Уточните, какие ограничения или решения отменить.");
                return items;
            }
            result = change.Category switch
            {
                "goal" when change.Action == "set" => result.Goal?.Value.Equals(fact.Value, StringComparison.OrdinalIgnoreCase) == true
                    ? result : result with { Goal = fact },
                "clarifications" => result with { Clarifications = Update(result.Clarifications) },
                "constraints" => result with { Constraints = Update(result.Constraints) },
                "terms" => result with { Terms = Update(result.Terms) },
                "selections" => result with { Selections = Update(result.Selections) },
                "decisions" => result with { Decisions = Update(result.Decisions) },
                _ => throw new InvalidDataException("Неизвестное поле состояния чата.")
            };
        }
        return result;
    }

    public static IEnumerable<RagChatChange> ExplicitChanges(string message)
    {
        // Clear user declarations do not depend on the model remembering to emit a delta.
        var categories = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        { ["Цель"] = "goal", ["Ограничение"] = "constraints", ["Уточнение"] = "clarifications", ["Термин"] = "terms", ["Решение"] = "decisions" };
        foreach (Match match in Regex.Matches(message, @"(?:^|(?<=[.!?])\s+)(Цель|Ограничение|Уточнение|Термин|Решение):\s*(.+?)(?=[.!?](?:\s|$)|$)", RegexOptions.IgnoreCase))
        {
            var category = categories[match.Groups[1].Value];
            var value = match.Groups[2].Value.Trim();
            if (value.Length is > 0 and <= 600 && match.Value.Length <= 600)
                yield return new(category, category == "goal" ? "set" : "add", value, match.Value);
        }
        var definition = Regex.Match(message, @"Под (.+?) (?:дальше )?понимаем (.+?)(?=[.!?](?:\s|$)|$)", RegexOptions.IgnoreCase);
        if (definition.Success && definition.Value.Length <= 600)
            yield return new("terms", "add", definition.Groups[1].Value + " = " + definition.Groups[2].Value, definition.Value);
    }

    public static string HistoryText(string answer)
    {
        var lines = new List<string>();
        var citation = false;
        foreach (var line in answer.Split('\n'))
        {
            if (Regex.IsMatch(line, @"^[\p{S}\p{M}]+\s+[1-9]\d*\.\s")) citation = false;
            if (line.StartsWith("▫️ Источники:", StringComparison.Ordinal)
                || line.StartsWith("▫️ Цитаты:", StringComparison.Ordinal)) citation = true;
            if (!citation) lines.Add(line.TrimEnd('\r'));
        }
        return string.Join('\n', lines);
    }
}
