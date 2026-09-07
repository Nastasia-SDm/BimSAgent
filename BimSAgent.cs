using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace BimSAgentApp;

public sealed class BimSAgent : IDisposable
{
    public const string Model = "gpt-4.1-nano";
    public sealed record TokenStatistics(int? UserInput, int? HistoryInput, int? TotalInput, int? Output);
    public TokenStatistics? LastTokenStatistics { get; private set; }

    private sealed record Message(
        [property: System.Text.Json.Serialization.JsonPropertyName("role")] string Role,
        [property: System.Text.Json.Serialization.JsonPropertyName("content")] string Content);

    private static readonly JsonSerializerOptions HistoryOptions = new() { WriteIndented = true };
    private readonly string _historyPath = Path.GetFullPath("history.json");
    private List<Message> _history;

    public BimSAgent()
    {
        try
        {
            _history = JsonSerializer.Deserialize<List<Message>>(File.ReadAllText(_historyPath))
                ?? throw new JsonException();
            if (_history.Any(message => message is null ||
                message.Role is not ("user" or "assistant") || string.IsNullOrWhiteSpace(message.Content)))
                throw new JsonException();
        }
        catch (FileNotFoundException)
        {
            _history = [];
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            _httpClient.Dispose();
            throw new InvalidOperationException(
                "Не удалось загрузить history.json. Проверьте формат JSON и доступ к файлу. Файл не изменён.");
        }
    }

    private const string Instructions = """
        Ты BimSAgent — AI-агент с глубокой специализацией на Autodesk Revit, BIM и Revit API.
        Ты понимаешь устройство Revit-моделей, метаданные, элементы, категории, семейства,
        типы и экземпляры, параметры, геометрию, зависимости и связи между элементами.
        Ты глубоко понимаешь внутренние механизмы Revit, зависимости между элементами,
        механизмы их перестроения и возможные причины изменений элементов модели.
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
        LastTokenStatistics = null;
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        var apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("Задайте переменную окружения OPENAI_API_KEY перед отправкой запроса.");

        var userTokens = await TryCountTokensAsync([new Message("user", prompt)], apiKey, cancellationToken);
        int? historyTokens = _history.Count == 0 ? 0 :
            await TryCountTokensAsync(_history.ToArray(), apiKey, cancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/responses");
        // The key is used only for authentication and is never written to disk or logs.
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
        request.Content = JsonContent.Create(new
        {
            model = Model,
            instructions = Instructions,
            input = _history.Append(new Message("user", prompt)).ToArray(),
            store = false,
            truncation = "disabled"
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
        answer = answer.Replace(apiKey.Trim(), "[скрыто]", StringComparison.Ordinal);
        var updatedHistory = _history
            .Append(new Message("user", prompt))
            .Append(new Message("assistant", answer))
            .Select(message => message with
            {
                Content = message.Content.Replace(apiKey.Trim(), "[скрыто]", StringComparison.Ordinal)
            }).ToList();
        SaveHistory(updatedHistory);
        _history = updatedHistory;
        var hasUsage = root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object;
        LastTokenStatistics = new TokenStatistics(userTokens, historyTokens,
            hasUsage && usage.TryGetProperty("input_tokens", out var inputTokens) ? inputTokens.GetInt32() : null,
            hasUsage && usage.TryGetProperty("output_tokens", out var outputTokens) ? outputTokens.GetInt32() : null);
        return answer;
    }

    private async Task<int?> TryCountTokensAsync(Message[] input, string apiKey, CancellationToken cancellationToken)
    {
        try
        {
            return await CountTokensAsync(input, apiKey, null, cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return null; }
        catch (Exception e) when (e is HttpRequestException or JsonException or InvalidOperationException)
        {
            // A counting failure must not prevent a normal answer or invent a token estimate.
            return null;
        }
    }

    private async Task<int> CountTokensAsync(Message[] input, string apiKey, string? instructions,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/responses/input_tokens");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
        request.Content = JsonContent.Create(new { model = Model, input, instructions });
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Подсчёт токенов недоступен: HTTP {(int)response.StatusCode}.");
        using var json = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        if (!json.RootElement.TryGetProperty("input_tokens", out var count) ||
            !count.TryGetInt32(out var tokens) || tokens < 0)
            throw new JsonException();
        return tokens;
    }

    public async Task<string> RunContextLimitTestAsync(CancellationToken cancellationToken = default)
    {
        const int targetTokens = 1_050_000;
        var apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("Задайте переменную окружения OPENAI_API_KEY перед тестом.");

        // Repeated text is just a starting point; the API verifies the actual count,
        // including the unchanged instructions and message framing, before generation.
        var repetitions = targetTokens;
        Message[] input = [];
        var verifiedTokens = 0;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var text = string.Concat(Enumerable.Repeat(" a", repetitions));
            input = [new Message("user", text)];
            verifiedTokens = await CountTokensAsync(input, apiKey, Instructions, cancellationToken);
            if (verifiedTokens == targetTokens)
                break;
            repetitions = checked(repetitions + targetTokens - verifiedTokens);
            if (repetitions <= 0 || repetitions > targetTokens * 2)
                break;
        }
        if (verifiedTokens != targetTokens)
            throw new InvalidOperationException("Не удалось подтвердить контекст ровно в 1 050 000 токенов. Тестовый запрос не отправлен.");

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/responses");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
        request.Content = JsonContent.Create(new
        {
            model = Model, instructions = Instructions, input,
            truncation = "disabled", store = false, max_output_tokens = 16
        });
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var summary = $"Вход: {verifiedTokens:N0} токенов (подтверждено API); лимит модели: 1 047 576.\n";
        if (response.IsSuccessStatusCode)
            return summary + "API неожиданно принял запрос. Ошибка переполнения не подтверждена.";

        using var json = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        if (json.RootElement.TryGetProperty("error", out var error) &&
            error.TryGetProperty("code", out var code) && code.GetString() == "context_length_exceeded")
        {
            var message = error.TryGetProperty("message", out var detail) ? detail.GetString() ?? "" : "";
            message = message.Replace(apiKey.Trim(), "[скрыто]", StringComparison.Ordinal);
            return summary + $"HTTP {(int)response.StatusCode}: context_length_exceeded\n{message}";
        }
        // Other errors may contain authentication details; don't print their raw bodies.
        return summary + $"HTTP {(int)response.StatusCode}: получена другая ошибка API. " +
            "Переполнение контекста не подтверждено; проверьте доступ к модели и лимиты аккаунта.";
    }

    private void SaveHistory(List<Message> history)
    {
        var temporaryPath = _historyPath + ".tmp";
        try
        {
            // Replace only after the entire JSON has been written successfully.
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(history, HistoryOptions));
            File.Move(temporaryPath, _historyPath, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                "Ответ получен, но сохранить history.json не удалось. Проверьте доступ к папке и свободное место. " +
                "Новый обмен сообщениями не добавлен в контекст.");
        }
        finally
        {
            try { File.Delete(temporaryPath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    public void Dispose() => _httpClient.Dispose();
}
