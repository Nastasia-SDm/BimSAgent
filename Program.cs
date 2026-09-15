using System.Text;
using BimSAgentApp;

Console.InputEncoding = Encoding.UTF8;
Console.OutputEncoding = Encoding.UTF8;

using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    shutdown.Cancel();
};

BimSAgent loadedAgent;
try
{
    loadedAgent = new BimSAgent();
}
catch (InvalidOperationException e)
{
    Console.Error.WriteLine(e.Message);
    Environment.ExitCode = 1;
    return;
}
using var agent = loadedAgent;
Console.WriteLine("BimSAgent — помощник по Revit, BIM и Revit API.");
Console.WriteLine("Введите запрос. Для выхода: /exit или Ctrl+C.");
Console.WriteLine($"Модель: {BimSAgent.Model}. Тест лимита контекста: context-limit-test.");
Console.WriteLine("Создать технический prompt по задаче: generate-prompt.");
Console.WriteLine("strategy sliding-window|sticky-facts|branching; checkpoint; branch create <name>; branch switch <name>");
Console.WriteLine(agent.ContextStatus);
Console.WriteLine("memory <id> move from <source> to <target>; memory <id> delete from <source>");
Console.WriteLine("profile create <name>; profile use <name>; profile show; profile list; profile skip");
Console.WriteLine("task create <name>; task open <id>; task pause");

while (!shutdown.IsCancellationRequested)
{
    Console.Write("\nВы: ");
    string? input;
    try
    {
        input = await Console.In.ReadLineAsync(shutdown.Token);
    }
    catch (OperationCanceledException)
    {
        break;
    }

    if (input is null || input.Trim().Equals("/exit", StringComparison.OrdinalIgnoreCase))
        break;
    if (string.IsNullOrWhiteSpace(input))
        continue;

    try
    {
        var profileParts = input.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (profileParts[0].Equals("task", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine(agent.HandleTaskCommand(input));
            if (profileParts.Length == 3 && profileParts[1].Equals("open", StringComparison.OrdinalIgnoreCase) && agent.HasUnfinishedTask)
                if (!await RunTaskWorkflowAsync(agent, agent.TaskResumeInput, shutdown.Token)) break;
            continue;
        }
        if (profileParts[0].Equals("profile", StringComparison.OrdinalIgnoreCase))
        {
            if (profileParts.Length == 3 && profileParts[1].Equals("create", StringComparison.OrdinalIgnoreCase))
            {
                agent.CheckNewProfileName(profileParts[2]);
                Console.WriteLine("Опишите Style (как вы хотите, чтобы агент отвечал вам: кратко/подробно, формально/разговорно, с примерами кода или без):");
                var style = await Console.In.ReadLineAsync(shutdown.Token);
                if (style is null || style.Trim().Equals("/exit", StringComparison.OrdinalIgnoreCase)) break;
                Console.WriteLine("Опишите Constraints (какие правила и ограничения агент должен соблюдать при ответах вам):");
                var constraints = await Console.In.ReadLineAsync(shutdown.Token);
                if (constraints is null || constraints.Trim().Equals("/exit", StringComparison.OrdinalIgnoreCase)) break;
                Console.WriteLine("Опишите Context (кто вы, зачем используете агента, над каким проектом работаете и какой результат хотите получать):");
                var context = await Console.In.ReadLineAsync(shutdown.Token);
                if (context is null || context.Trim().Equals("/exit", StringComparison.OrdinalIgnoreCase)) break;
                agent.CreateProfile(profileParts[2], style, constraints, context);
                Console.WriteLine("Профиль создан. Для активации используйте profile use <name>.");
            }
            else Console.WriteLine(agent.HandleProfileCommand(input));
            continue;
        }
        if (agent.TryHandleContextCommand(input, out var commandResult))
        {
            Console.WriteLine(commandResult);
            continue;
        }
        if (input.Trim().Equals("context-limit-test", StringComparison.OrdinalIgnoreCase))
        {
            var options = await ReadOptionsAsync(shutdown.Token);
            if (options is null) break;
            Console.WriteLine("Подготовка и проверка контекста 1 050 000 токенов через API...");
            Console.WriteLine(await agent.RunContextLimitTestAsync(options.Value.Tokens, options.Value.Temperature, shutdown.Token));
            continue;
        }
        if (input.Trim().Equals("generate-prompt", StringComparison.OrdinalIgnoreCase))
        {
            Console.Write("Задача: ");
            var task = await Console.In.ReadLineAsync(shutdown.Token);
            if (task is null || task.Trim().Equals("/exit", StringComparison.OrdinalIgnoreCase))
                break;
            if (string.IsNullOrWhiteSpace(task))
            {
                Console.WriteLine("Задача не введена. Команда отменена.");
                continue;
            }
            if (!await UpdateFactsIfNeededAsync(agent, task, shutdown.Token)) break;
            var options = await ReadOptionsAsync(shutdown.Token);
            if (options is null) break;
            Console.WriteLine(await agent.GeneratePromptAsync(task, options.Value.Tokens, options.Value.Temperature, shutdown.Token));
        }
        else if (agent.HasUnfinishedTask)
        {
            if (!await RunTaskWorkflowAsync(agent, input, shutdown.Token)) break;
            continue;
        }
        else
        {
            if (!await UpdateFactsIfNeededAsync(agent, input, shutdown.Token)) break;
            var options = await ReadOptionsAsync(shutdown.Token);
            if (options is null) break;
            Console.WriteLine($"\nАгент: {await agent.AskAsync(input, options.Value.Tokens, options.Value.Temperature, shutdown.Token)}");
        }
        if (!await ReportResponseAsync(agent, shutdown.Token)) break;
    }
    catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
    {
        break;
    }
    catch (OperationCanceledException)
    {
        Console.Error.WriteLine("Время ожидания ответа истекло. Повторите запрос.");
    }
    catch (HttpRequestException)
    {
        Console.Error.WriteLine("Не удалось связаться с OpenAI. Проверьте подключение к сети.");
    }
    catch (InvalidOperationException e)
    {
        Console.Error.WriteLine(e.Message);
    }
    catch (System.Text.Json.JsonException)
    {
        Console.Error.WriteLine("OpenAI вернул ответ в неожиданном формате.");
    }
    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
    {
        Console.Error.WriteLine("Не удалось записать память. Проверьте доступ к файлам; перезапуск восстановит незавершённую запись.");
    }
}

static async Task<bool> UpdateFactsIfNeededAsync(BimSAgent agent, string message, CancellationToken cancellationToken)
{
    if (agent.Strategy != "sticky-facts") return true;
    Console.WriteLine("Параметры отдельного запроса для обновления долгосрочных фактов:");
    var options = await ReadOptionsAsync(cancellationToken);
    if (options is null) return false;
    Console.WriteLine(await agent.UpdateFactsAsync(message, options.Value.Tokens, options.Value.Temperature, cancellationToken));
    Console.WriteLine("Параметры основного ответа:");
    return true;
}

static async Task<(int Tokens, double Temperature)?> ReadOptionsAsync(CancellationToken cancellationToken)
{
    int tokens;
    while (true)
    {
        Console.Write("Максимальное количество токенов для ответа (16–32768): ");
        var input = await Console.In.ReadLineAsync(cancellationToken);
        if (input is null || input.Trim().Equals("/exit", StringComparison.OrdinalIgnoreCase))
            return null;
        if (int.TryParse(input, out tokens) && tokens is >= 16 and <= 32768)
            break;
        Console.WriteLine("Введите целое число от 16 до 32768.");
    }
    while (true)
    {
        Console.Write("Temperature (0–2): ");
        var input = await Console.In.ReadLineAsync(cancellationToken);
        if (input is null || input.Trim().Equals("/exit", StringComparison.OrdinalIgnoreCase))
            return null;
        if (double.TryParse(input.Trim().Replace(',', '.'), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var temperature) &&
            double.IsFinite(temperature) && temperature is >= 0 and <= 2)
            return (tokens, temperature);
        Console.WriteLine("Введите число от 0 до 2, например 0.7 или 0,7.");
    }
}

static async Task<bool> RunTaskWorkflowAsync(BimSAgent agent, string input, CancellationToken cancellationToken)
{
    while (agent.HasUnfinishedTask)
    {
        if (agent.TaskAwaitingChoice)
            Console.WriteLine(agent.SavedTaskAnswer);
        else
        {
        if (!await UpdateFactsIfNeededAsync(agent, input, cancellationToken)) return false;
        var options = await ReadOptionsAsync(cancellationToken);
        if (options is null) return false;
        Console.WriteLine(await agent.RunTaskTurnAsync(input, options.Value.Tokens, options.Value.Temperature, cancellationToken));
        // Preserve the existing token reporting and memory update after every answer.
        if (!await ReportResponseAsync(agent, cancellationToken)) return false;
        }
        var stage = agent.ActiveTaskStage;
        int choice;
        while (true)
        {
            Console.WriteLine("Для паузы: task pause");
            Console.WriteLine(BimSAgent.FormatTaskChoices(stage));
            var answer = await Console.In.ReadLineAsync(cancellationToken);
            if (answer is null || answer.Trim().Equals("/exit", StringComparison.OrdinalIgnoreCase)) return false;
            if (answer.Trim().Equals("task pause", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine(agent.HandleTaskCommand(answer));
                return true;
            }
            if (int.TryParse(answer, out choice) && choice >= 1 && choice <= (stage == "PLANNING" ? 3 : 2)) break;
            Console.WriteLine("Введите номер одного из предложенных вариантов.");
        }
        var corrections = "";
        if ((stage == "PLANNING" && choice == 3) || (stage != "PLANNING" && choice == 2))
        {
            while (true)
            {
                Console.WriteLine("Введите корректировки или замечания:");
                var answer = await Console.In.ReadLineAsync(cancellationToken);
                if (answer is null || answer.Trim().Equals("/exit", StringComparison.OrdinalIgnoreCase)) return false;
                if (answer.Trim().Equals("task pause", StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine(agent.HandleTaskCommand(answer));
                    return true;
                }
                if (!string.IsNullOrWhiteSpace(answer)) { corrections = answer; break; }
                Console.WriteLine("Замечания не должны быть пустыми.");
            }
        }
        input = agent.ApplyTaskChoice(choice, corrections);
    }
    Console.WriteLine(BimSAgent.FormatTaskOutput("DONE", "Задача завершена."));
    return true;
}

static async Task<bool> ReportResponseAsync(BimSAgent agent, CancellationToken cancellationToken)
{
        if (agent.LastTokenStatistics is { } tokens)
        {
            static string Format(int? count) => count?.ToString("N0") ?? "недоступно (API не вернул счётчик)";
            Console.WriteLine($"Токены запроса: {Format(tokens.TotalInput)}");
            Console.WriteLine($"Токены ответа: {Format(tokens.Output)}");
        }
        var memoryReport = await agent.UpdateMemoryAsync(cancellationToken);
        // Hide routine summaries, but keep explicit per-entry failures visible.
        foreach (var error in memoryReport.Split(Environment.NewLine).Skip(1))
            Console.Error.WriteLine(error);
        while (agent.PendingMemoryDescription is { } description)
        {
            Console.WriteLine(description);
            Console.WriteLine("Не уверен, куда сохранить эту информацию. Выберите: short / working / long / skip:");
            var choice = await Console.In.ReadLineAsync(cancellationToken);
            if (choice is null || choice.Trim().Equals("/exit", StringComparison.OrdinalIgnoreCase)) return false;
            try
            {
                agent.ResolvePendingMemory(choice);
            }
            catch (InvalidOperationException e)
            {
                Console.Error.WriteLine(e.Message);
            }
            catch (System.Text.Json.JsonException e)
            {
                Console.Error.WriteLine($"Запись не сохранена: {e.Message}");
            }
        }
    return true;
}
