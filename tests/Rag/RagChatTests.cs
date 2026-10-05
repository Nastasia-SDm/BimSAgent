using System.Net;
using System.Text.Json;
using BimSAgentApp.Rag;

static class RagChatTests
{
    public static async Task Run(Action<bool, string> check, IIndexStore indexes, string root)
    {
        foreach (var scenario in new[] { "Лестницы", "Отверстия" })
        {
            var directory = Path.Combine(root, "chat-" + scenario);
            var chats = new RagChatStore(directory);
            using var lease = chats.Lock();
            var session = chats.Open(true, null, 7);
            var initialId = session.Id;
            var embedding = new Embeddings(); var retrieval = new Retriever(); var generation = new Generator(check);
            var planner = new Planner(scenario, check);
            var reads = 0;
            JsonElement TaskState(int id) { check(id == 7, "chat reuses bound task ID"); return JsonSerializer.SerializeToElement(new { id, state = "EXECUTION", currentStep = ++reads }); }
            var engine = new RagChatEngine(new(embedding, generation, indexes, retrieval, new()), planner, TaskState);
            var messages = new[] {
                "Цель: " + scenario,
                "Ограничение: только бетон",
                "Термин: ЛП означает лестничная площадка",
                "Выбираю семейство Family_" + scenario,
                "Уточнение: параметр Ширина_Проема",
                "Решение: сравнивать типоразмеры",
                "Для чего это семейство?",
                "Как изменить тот параметр?",
                "Отменяю только бетон; новое ограничение: только железобетон",
                "Докажи это старой цитатой",
                "1. Какое это семейство? 2. Какой тот параметр? 3. Какие ограничения?",
                "Продолжи с учётом задачи",
                "Что решили раньше?",
                "Сохрани исходную цель и выбранный вариант"
            };
            for (var i = 0; i < messages.Length; i++)
            {
                var before = embedding.Inputs.Count;
                var result = await engine.TurnAsync(session, messages[i], "structural", 2, default);
                chats.Save(result.Session); session = result.Session;
                check(embedding.Inputs.Count - before == QuestionParser.Split(messages[i]).Count
                    && retrieval.Calls == embedding.Inputs.Count && generation.Calls == i + 1, "chat fresh retrieval per question, one answer call per turn");
                check(session.State.Goal?.Value == scenario && session.TaskId == 7 && reads == i + 1, "chat goal and current task survive long dialogue");
                if (i >= 2) check(session.State.Terms.Single().Value.Contains("ЛП"), "chat terminology retained");
                if (i >= 3) check(session.State.Selections.Single().Value == "Family_" + scenario, "chat explicit family selection retained");
                if (i >= 8) check(session.State.Constraints.Single().Value == "только железобетон", "chat explicit correction replaces old constraint");
                if (i == 6) check(embedding.Inputs[^1].Contains("Family_" + scenario), "family reference resolved before embedding");
                if (i == 7) check(embedding.Inputs[^1].Contains("Ширина_Проема"), "parameter reference resolved before embedding");
                check(!session.Turns[^1].HistoryAnswer.Contains("chunk_id:") && !session.Turns[^1].HistoryAnswer.Contains("▫️ Цитаты:"), "old citations excluded from next history input");
                if (i == 9) check(System.Text.RegularExpressions.Regex.IsMatch(result.Answer.Text, @"^[\p{S}\p{M}]+\s+1\. Не знаю\. Для корректного ответа укажите дополнительно - ")
                    && !result.Answer.Text.Contains("chunk_id:"), "stale quote rejected with specific unknown");
                else check(result.Answer.Sources.All(h => result.Answer.Text.Contains("chunk_id: " + h.Chunk.ChunkId)), "only current retrieved sources used");
                if (i == 5)
                {
                    session = new RagChatStore(directory).Open(false, null, null);
                    check(session.Id == initialId && session.Turns.Length == 6 && session.State.Decisions.Length == 1, "restart restores separate history and structured state");
                }
            }
            check(generation.Calls == 14 && reads == 14 && planner.Calls == 14, "14-message scenario complete: " + scenario);
            var originalBytes = File.ReadAllBytes(Path.Combine(directory, initialId + ".json"));
            var fresh = chats.Open(true, null, null);
            check(fresh.Id != initialId && fresh.Turns.Length == 0 && fresh.State.Goal == null
                && File.ReadAllBytes(Path.Combine(directory, initialId + ".json")).SequenceEqual(originalBytes), "new chat preserves archived chat");
            check(chats.Open(false, initialId, null).State.Goal?.Value == scenario, "explicit resume restores selected chat");
        }

        var cliInput = new StringReader("/state\n/new\n/exit\nparent-command\n");
        var cliOutput = new StringWriter(); var cliError = new StringWriter();
        var code = await RagChatCommands.RunAsync(["rag", "chat", "--new", "--baseline", "--top-k", "2"], default,
            cliInput, cliOutput, cliError, Path.Combine(root, "cli-chat"));
        check(code == 0 && cliInput.ReadLine() == "parent-command" && cliError.ToString() == "", "chat exit returns control without consuming parent input");
        check(await RagChatCommands.RunAsync(["rag", "chat", "--baseline", "--improved"], default, new StringReader(""), cliOutput, cliError,
            Path.Combine(root, "cli-chat")) == 1, "chat rejects conflicting retrieval flags");
        var failedDirectory = Path.Combine(root, "failed-chat");
        var failedEngine = new RagChatEngine(new(new Embeddings(), new Generator(check), indexes, new Retriever(), new()),
            new FailedPlanner(), _ => throw new Exception("No task bound"));
        check(await RagChatCommands.RunAsync(["rag", "chat", "--new"], default, new StringReader("Вопрос\n/state\n/exit\n"),
            cliOutput, cliError, failedDirectory, engine: failedEngine) == 0
            && new RagChatStore(Path.Combine(failedDirectory, "chats")).Open(false, null, null).Turns.Length == 0,
            "invalid planner output leaves chat intact and CLI available");

        using var handler = new PlannerHandler(); using var http = new HttpClient(handler);
        var client = new OpenAiRagClient(http, () => "fixture-no-network");
        var ctx = new RagChatContext(new(), [], null);
        var plan = await client.PlanChatAsync(["Какое семейство?"], "Какое семейство?", ctx, default);
        check(plan.Queries.Single() == "семейство площадки" && handler.Calls == 1, "chat planner parses structured API response");
        check(!handler.Payload.TryGetProperty("temperature", out _)
            && handler.Payload.GetProperty("reasoning").GetProperty("effort").GetString() == "low"
            && handler.Payload.GetProperty("model").GetString() == RagDefaults.AnswerModel,
            "planner uses current reasoning model without unsupported temperature");
        using var rejectedHttp = new HttpClient(new RejectedPlannerHandler());
        try
        {
            await new OpenAiRagClient(rejectedHttp, () => "secret-test-key").PlanChatAsync(["Вопрос"], "Вопрос", ctx, default);
            check(false, "400 reports incompatible parameter");
        }
        catch (InvalidOperationException e)
        {
            check(e.Message.Contains("HTTP 400") && e.Message.Contains("param=temperature")
                && !e.Message.Contains("secret-test-key") && !e.Message.Contains("private-document"),
                "400 reports parameter without echoing server message or credentials");
        }
        try { RagChatEngine.Apply(new(), [new("constraints", "add", "выдумка", "нет в сообщении")], "исходное сообщение", 1); check(false, "unproven state rejected"); }
        catch (InvalidDataException) { check(true, "unproven state rejected"); }
        var request = new RagAnswerRequest("Какой параметр?", [], false, new()) { Chat = ctx };
        check(AnswerEvidenceValidator.ValidateAndRender("{}", request).Contains("точное имя параметра"), "invalid chat JSON requests concrete parameter clarification");
        var explicitMessage = "Решение: ничего не менять в Revit. Под тем параметром дальше понимаем Диаметр.";
        var explicitState = RagChatEngine.Apply(new(), RagChatEngine.ExplicitChanges(explicitMessage), explicitMessage, 1);
        check(explicitState.Decisions.Single().Value == "ничего не менять в Revit" && explicitState.Terms.Single().Value.Contains("Диаметр"),
            "explicit decisions and reference definitions survive an omitted model delta");
        var repeated = RagChatEngine.Apply(explicitState, [new("decisions", "add", "ничего не менять в Revit", "Что дальше?")], "Что дальше?", 2);
        check(repeated.Decisions.Single().Turn == 1 && repeated.Decisions.Single().Evidence.Contains("Решение:"), "repeated facts preserve their original evidence");
        var referenceState = explicitState with { Selections = [new("Family_Круглое", "Выбираю Family_Круглое", 1)] };
        var resolved = RagChatEngine.ResolveQuery("Как изменить тот параметр у этого семейства?", "Как изменить тот параметр у этого семейства?", referenceState);
        check(resolved.Contains("Family_Круглое") && resolved.Contains("Диаметр"), "unresolved model query gets explicit family and parameter anchors");
        check(RagChatEngine.ResolveQuery("Как смоделировать балку?", "балка", referenceState) == "балка",
            "independent question does not inherit unrelated selected family");
    }

    sealed class Embeddings : IEmbeddingClient
    {
        public List<string> Inputs { get; } = [];
        public Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken)
        { if (texts.Count != 1) throw new Exception("Combined embedding"); Inputs.Add(texts[0]); var v = new float[1536]; v[0] = 1; return Task.FromResult(new[] { v }); }
    }
    sealed class Retriever : IRetriever
    {
        public int Calls;
        public IReadOnlyList<RetrievalHit> Search(RagIndex index, float[] query, int topK)
        { Calls++; return [new(index.Chunks[0] with { ChunkId = "current-" + Calls, Text = "Актуальное описание номер " + Calls + ".", FamilyPurpose = "", Tables = [], InheritedContext = [], Warnings = [] }, 0.9)]; }
    }
    sealed class Generator(Action<bool, string> check) : IRagAnswerGenerator
    {
        public int Calls;
        public Task<string> GenerateAsync(RagAnswerRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            check(request.Chat?.Task?.GetProperty("currentStep").GetInt32() == Calls, "final answer sees freshly read task state");
            var answers = request.Questions!.Select((q, i) => new { number = i + 1, text = "Короткий ответ.", status = "supported",
                evidence = new[] { new { quote = Calls == 10 ? "Актуальное описание номер 1." : q.Context.Single().Chunk.Text } } });
            return Task.FromResult(AnswerEvidenceValidator.ValidateAndRender(JsonSerializer.Serialize(new { answers }), request));
        }
    }
    sealed class Planner(string scenario, Action<bool, string> check) : IRagChatPlanner
    {
        public int Calls;
        public Task<RagChatPlan> PlanChatAsync(IReadOnlyList<string> questions, string message, RagChatContext context, CancellationToken token)
        {
            var step = Calls++;
            check(context.History.All(h => !h.Assistant.Contains("chunk_id:")) && context.History.Count <= 12, "planner sees bounded history without old evidence");
            RagChatChange[] changes = step switch {
                0 => [new("goal", "set", scenario, message)],
                1 => [new("constraints", "add", "только бетон", message)],
                2 => [new("terms", "add", "ЛП означает лестничная площадка", message)],
                3 => [new("selections", "add", "Family_" + scenario, message)],
                4 => [new("clarifications", "add", "параметр Ширина_Проема", message)],
                5 => [new("decisions", "add", "сравнивать типоразмеры", message)],
                8 => [new("constraints", "remove", "только бетон", message), new("constraints", "add", "только железобетон", message)],
                _ => [] };
            var queries = questions.Select(q => q.Replace("это семейство", context.State.Selections.FirstOrDefault()?.Value ?? "")
                .Replace("тот параметр", context.State.Clarifications.FirstOrDefault()?.Value ?? "")).ToArray();
            return Task.FromResult(new RagChatPlan(queries, changes));
        }
    }
    sealed class PlannerHandler : HttpMessageHandler
    {
        public int Calls;
        public JsonElement Payload;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken)); Payload = json.RootElement.Clone();
          Calls++; return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new {
            status = "completed", output = new[] { new { content = new[] { new { type = "output_text", text = "{\"queries\":[\"семейство площадки\"],\"changes\":[]}" } } } }
        })) }; }
    }
    sealed class RejectedPlannerHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent(
                """{"error":{"code":"unsupported_parameter","param":"temperature","message":"secret-test-key private-document"}}""") });
    }
    sealed class FailedPlanner : IRagChatPlanner
    {
        public Task<RagChatPlan> PlanChatAsync(IReadOnlyList<string> questions, string message, RagChatContext context, CancellationToken token)
            => Task.FromResult(new RagChatPlan([], []));
    }
}
