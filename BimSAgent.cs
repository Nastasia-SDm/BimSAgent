using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace BimSAgentApp;

public sealed class BimSAgent : IDisposable
{
    private const string Instructions = """
        Ты BimSAgent — AI-агент с глубокой специализацией на Autodesk Revit, BIM и Revit API.
        Ты понимаешь устройство Revit-моделей, метаданные, элементы, категории, семейства,
        типы и экземпляры, параметры, геометрию, зависимости и связи между элементами.
        Ты помогаешь разрабатывать и отлаживать C#-плагины и Dynamo через Revit API.
        Учитывай контекст выполнения Revit API, транзакции, единицы измерения,
        фильтрацию элементов, общие параметры, связанные модели и ограничения потоков.
        Отвечай на языке пользователя, ясно и по существу. При запросе кода давай
        практические примеры и поясняй предположения и необходимые условия запуска.
        Если решение зависит от версии Revit или Dynamo, уточни версию либо явно
        укажи допущение. Не выдумывай методы API; сообщай о неопределенности.
        Ты не подключен к Revit и не можешь читать или менять модель пользователя.
        Не утверждай, что выполнил код или проверил модель. Анализируй предоставленные данные.
        На запросы вне специализации также отвечай по мере своих знаний.
        """;

    private readonly HttpClient _httpClient = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromMinutes(2)
    };

    public async Task<string> AskAsync(string prompt, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        var apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("Задайте переменную окружения OPENAI_API_KEY перед отправкой запроса.");

        var model = Environment.GetEnvironmentVariable("OPENAI_MODEL");
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/responses");
        // The key is used only for authentication and is never written to disk or logs.
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
        request.Content = JsonContent.Create(new
        {
            model = string.IsNullOrWhiteSpace(model) ? "gpt-4.1" : model.Trim(),
            instructions = Instructions,
            input = prompt,
            store = false
        });

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        // Do not display raw error bodies: authentication errors can include key fragments.
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"OpenAI вернул HTTP {(int)response.StatusCode}. " +
                "Проверьте API-ключ, доступ к модели и лимиты аккаунта.");

        using var json = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        var root = json.RootElement;
        if (root.TryGetProperty("status", out var status) && status.GetString() != "completed")
            throw new InvalidOperationException("OpenAI не завершил ответ. Повторите или уточните запрос.");

        var parts = new List<string>();
        if (root.TryGetProperty("output", out var output))
        {
            foreach (var item in output.EnumerateArray())
            {
                if (!item.TryGetProperty("type", out var type) || type.GetString() != "message" ||
                    !item.TryGetProperty("content", out var content))
                    continue;

                foreach (var part in content.EnumerateArray())
                {
                    var partType = part.GetProperty("type").GetString();
                    if (partType == "output_text")
                        parts.Add(part.GetProperty("text").GetString() ?? "");
                    else if (partType == "refusal")
                        parts.Add(part.GetProperty("refusal").GetString() ?? "");
                }
            }
        }

        var answer = string.Join(Environment.NewLine, parts);
        if (string.IsNullOrWhiteSpace(answer))
            throw new InvalidOperationException("OpenAI не вернул текстовый ответ.");
        return answer.Replace(apiKey.Trim(), "[скрыто]", StringComparison.Ordinal);
    }

    public void Dispose() => _httpClient.Dispose();
}
