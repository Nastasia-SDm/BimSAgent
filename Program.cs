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
        Console.WriteLine($"\nАгент: {await agent.AskAsync(input, shutdown.Token)}");
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
