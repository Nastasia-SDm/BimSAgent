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
try
{
    await agent.InitializeAsync(shutdown.Token);
}
catch (Exception e) when (e is InvalidOperationException or HttpRequestException or
    OperationCanceledException or System.Text.Json.JsonException)
{
    Console.Error.WriteLine(e is InvalidOperationException ? e.Message :
        "Не удалось сжать историю при запуске. Исходная история сохранена; повторите запуск.");
    Environment.ExitCode = 1;
    return;
}
Console.WriteLine("BimSAgent — помощник по Revit, BIM и Revit API.");
Console.WriteLine("Введите запрос. Для выхода: /exit или Ctrl+C.");
Console.WriteLine($"Модель: {BimSAgent.Model}. Тест лимита контекста: context-limit-test.");
Console.WriteLine("Создать технический prompt по задаче: generate-prompt.");

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
        if (input.Trim().Equals("context-limit-test", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("Подготовка и проверка контекста 1 050 000 токенов через API...");
            Console.WriteLine(await agent.RunContextLimitTestAsync(shutdown.Token));
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
            int tokenLimit;
            while (true)
            {
                Console.WriteLine("Сколько токенов может потратить LLM на создание prompt?");
                Console.Write("Лимит токенов (16–32768): ");
                var limitInput = await Console.In.ReadLineAsync(shutdown.Token);
                if (limitInput is null || limitInput.Trim().Equals("/exit", StringComparison.OrdinalIgnoreCase))
                    return;
                if (int.TryParse(limitInput, out tokenLimit) && tokenLimit is >= 16 and <= 32768)
                    break;
                Console.WriteLine("Введите целое число от 16 до 32768.");
            }
            Console.WriteLine(await agent.GeneratePromptAsync(task, tokenLimit, shutdown.Token));
        }
        else
        {
            Console.WriteLine($"\nАгент: {await agent.AskAsync(input, shutdown.Token)}");
        }
        if (agent.LastTokenStatistics is { } tokens)
        {
            static string Format(int? count) => count?.ToString("N0") ?? "недоступно (API не вернул счётчик)";
            Console.WriteLine($"Токены текущего запроса (со служебным оформлением): {Format(tokens.UserInput)}");
            Console.WriteLine($"Токены предыдущей истории (summary + последние сообщения, без нового запроса и роли): {Format(tokens.HistoryInput)}");
            Console.WriteLine($"Весь вход, включая роль, историю и запрос (usage.input_tokens): {Format(tokens.TotalInput)}");
            Console.WriteLine($"Токены ответа (usage.output_tokens): {Format(tokens.Output)}");
        }
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
}
