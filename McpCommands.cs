using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BimSAgentApp;

internal static class McpCommands
{
    private sealed class ServerState(string directory, string assemblyName)
    {
        public string Directory { get; } = directory;
        public string AssemblyName { get; } = assemblyName;
        public (long[] ElementIds, string DocumentSession)? LastElements;
        public string? LastParametersFile;
    }
    private static readonly ServerState Server1 = new(@"D:\BIM-S-MCP-1\BIM-S_MCP-Server", "BIM-S_MCP-Server");
    private static readonly ServerState Server2 = new(@"D:\BIM-S-MCP-2\BIM-S_MCP-Server-2", "BIM-S_MCP-Server-2");
    private static readonly ServerState Server3 = new(@"D:\BIM-S-MCP-3\BIM-S_MCP-Server-3", "BIM-S_MCP-Server-3");
    public sealed record ToolInfo(
    string Server,
    string Name,
    string Description,
    string InputSchema);

    public sealed record ToolResult(
        string Server,
        string Tool,
        bool IsError,
        string Text,
        string? StructuredContent);

    private static ServerState GetServer(string server) => server.ToLowerInvariant() switch
    {
        "mcp1" => Server1,
        "mcp2" => Server2,
        "mcp3" => Server3,
        _ => throw new ArgumentException("Неизвестный MCP-сервер.", nameof(server))
    };

    private static StdioClientTransport CreateTransport(ServerState server)
    {
        var environment = new Dictionary<string, string?>();

        foreach (var name in new[] { "PATH", "SystemRoot", "TEMP", "TMP", "DOTNET_ROOT", "ProgramFiles" })
            environment[name] = Environment.GetEnvironmentVariable(name);

        return new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = server.AssemblyName,
            Command = "dotnet",
            Arguments =
            [
                Path.Combine(
                server.Directory,
                "bin",
                "Debug",
                "net10.0",
                server.AssemblyName + ".dll")
            ],
            WorkingDirectory = server.Directory,
            InheritEnvironmentVariables = false,
            EnvironmentVariables = environment,
            StandardErrorLines = _ => { }
        });
    }
    public static async Task<string> GetToolsCatalogAsync(
    CancellationToken cancellationToken)
    {
        var catalog = new List<object>();

        foreach (var item in new[]
        {
        (Name: "mcp1", Server: Server1),
        (Name: "mcp2", Server: Server2),
        (Name: "mcp3", Server: Server3)
    })
        {
            using var timeout =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            timeout.CancelAfter(TimeSpan.FromSeconds(30));

            await using var client = await McpClient.CreateAsync(
                CreateTransport(item.Server),
                cancellationToken: timeout.Token);

            var tools = await client.ListToolsAsync(
                cancellationToken: timeout.Token);

            foreach (var tool in tools)
            {
                catalog.Add(new
                {
                    server = item.Name,
                    tool = tool.Name,
                    description = tool.Description ?? "Описание отсутствует",
                    inputSchema = tool.JsonSchema.ToString()
                });
            }
        }

        return JsonSerializer.Serialize(catalog);
    }
    public static async Task<ToolResult> ExecuteToolAsync(
    string serverName,
    string toolName,
    Dictionary<string, object?> arguments,
    CancellationToken cancellationToken)
    {
        var server = GetServer(serverName);

        using var timeout =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        timeout.CancelAfter(TimeSpan.FromMinutes(15));

        await using var client = await McpClient.CreateAsync(
            CreateTransport(server),
            cancellationToken: timeout.Token);

        var result = await client.CallToolAsync(
            toolName,
            arguments,
            cancellationToken: timeout.Token);

        var text = string.Join(
            "\n",
            result.Content
                .OfType<TextContentBlock>()
                .Select(x => x.Text));

        return new ToolResult(
            serverName,
            toolName,
            result.IsError == true,
            text,
            result.StructuredContent?.ToString());
    }
    public static async Task HandleAsync(string command, CancellationToken cancellationToken)
    {
        var parts = command.Split((char[]?)null, 3, StringSplitOptions.RemoveEmptyEntries);
        var prefix = parts.Length > 0 && parts[0].StartsWith("mcp3-", StringComparison.OrdinalIgnoreCase)
    ? "mcp3"
    : parts.Length > 0 && parts[0].StartsWith("mcp2-", StringComparison.OrdinalIgnoreCase)
        ? "mcp2"
        : "mcp1";

        var server = prefix == "mcp3" ? Server3 : prefix == "mcp2" ? Server2 : Server1;
        var isCall = parts.Length > 0 && parts[0].Equals(prefix + "-call", StringComparison.OrdinalIgnoreCase);
        if (isCall && parts.Length < 2)
        {
            Console.WriteLine($"Использование: {prefix}-call <tool-name> [JSON-объект аргументов]");
            return;
        }
        if (!isCall && !command.Equals(prefix + "-tools", StringComparison.OrdinalIgnoreCase))
        {
           Console.WriteLine("Неизвестная MCP-команда. Доступны: mcp1-tools; mcp1-call <tool-name>; mcp2-tools; mcp2-call <tool-name>; mcp3-tools; mcp3-call <tool-name>.");
            return;
        }
        try
        {
            if (isCall && parts[1] == "get-model-elements") server.LastElements = null;
            if (isCall && (parts[1] == "get-model-elements" || parts[1] == "get-model-elements-parameters"))
                server.LastParametersFile = null;
            var arguments = new Dictionary<string, object?>();
            if (isCall && parts.Length == 3)
            {
                try
                {
                    using var json = JsonDocument.Parse(parts[2]);
                    if (json.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException();
                    foreach (var property in json.RootElement.EnumerateObject())
                        if (!arguments.TryAdd(property.Name, property.Value.Clone())) throw new JsonException();
                }
                catch (JsonException)
                {
                    Console.WriteLine("MCP: аргументы должны быть JSON-объектом без повторяющихся свойств.");
                    return;
                }
            }
            if (isCall && parts[1] == "get-model-elements-parameters" && parts.Length == 2)
            {
                if (server.LastElements is not { } previous)
                {
                    Console.WriteLine($"Сначала выполните {prefix}-call get-model-elements в этом сеансе BimSAgent.");
                    return;
                }
                if (previous.ElementIds.Length == 0)
                {
                    Console.WriteLine("Предыдущий get-model-elements не вернул элементов для выгрузки.");
                    return;
                }
                arguments["elementIds"] = previous.ElementIds;
                arguments["documentSession"] = previous.DocumentSession;
            }
            if (isCall && parts[1] == "create-model-elements-report" && parts.Length == 2)
            {
                if (server.LastParametersFile == null)
                {
                    Console.WriteLine($"Сначала выполните {prefix}-call get-model-elements-parameters в этом сеансе BimSAgent.");
                    return;
                }
                arguments["filePath"] = server.LastParametersFile;
            }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
           timeout.CancelAfter(isCall && (prefix == "mcp3" || parts[1] == "get-model-elements-parameters" || parts[1] == "get-model" || (prefix == "mcp2" && (parts[1] == "get-documentation-elements" || parts[1] == "get-documentation"))) ? TimeSpan.FromMinutes(15) : TimeSpan.FromSeconds(30));
            // Do not inherit credentials, attach a logging provider or echo server stderr.
            var environment = new Dictionary<string, string?>();
            foreach (var name in new[] { "PATH", "SystemRoot", "TEMP", "TMP", "DOTNET_ROOT", "ProgramFiles" })
                environment[name] = Environment.GetEnvironmentVariable(name);
            var transport = new StdioClientTransport(new StdioClientTransportOptions
            {
                Name = server.AssemblyName,
                Command = "dotnet",
                Arguments = [Path.Combine(server.Directory, "bin", "Debug", "net10.0", server.AssemblyName + ".dll")],
                WorkingDirectory = server.Directory,
                InheritEnvironmentVariables = false,
                EnvironmentVariables = environment,
                StandardErrorLines = _ => { }
            });
            await using var client = await McpClient.CreateAsync(transport, cancellationToken: timeout.Token);
            if (isCall)
            {
                var result = await client.CallToolAsync(parts[1], arguments,
                    cancellationToken: timeout.Token);
                if (result.IsError != true && parts[1] == "get-model-elements-parameters" &&
                    result.StructuredContent is { ValueKind: JsonValueKind.Object } exported &&
                    exported.TryGetProperty("filePath", out var savedPath) && savedPath.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(savedPath.GetString()))
                    server.LastParametersFile = savedPath.GetString();
                if (result.IsError == true)
                    Console.WriteLine("MCP: инструмент вернул ошибку.");
                if (result.IsError != true && parts[1] == "get-model-elements")
                {
                    RememberElements(server, result.StructuredContent);
                    if (result.StructuredContent is { } data && TryPrintElements(data.ToString())) return;
                    foreach (var block in result.Content.OfType<TextContentBlock>())
                        if (TryPrintElements(block.Text)) return;
                }
                foreach (var content in result.Content)
                    Console.WriteLine(SafeText(content is TextContentBlock text
                        ? text.Text
                        : JsonSerializer.Serialize(content)));
                if (result.StructuredContent is { } structured)
                    Console.WriteLine(SafeText(structured.ToString()));
                if (result.Content.Count == 0 && result.StructuredContent is null)
                    Console.WriteLine("MCP: инструмент вернул пустой результат.");
                return;
            }
            var tools = await client.ListToolsAsync(cancellationToken: timeout.Token);
            if (tools.Count == 0)
                Console.WriteLine("MCP подключён, tools пока нет.");
            foreach (var tool in tools)
            {
                var description = SafeText(tool.Description ?? "Описание отсутствует");
                Console.WriteLine($"{SafeText(tool.Name)} — {string.Join(" ", description.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(5))}");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { Console.WriteLine("MCP: время ожидания ответа истекло."); }
        catch (Exception)
        {
            // Protocol/process exception messages can contain server output or secrets.
            Console.WriteLine(isCall
                ? "MCP: не удалось вызвать инструмент. Проверьте имя инструмента, сервер и его сборку Debug/net10.0."
                : "MCP: не удалось получить tools. Проверьте сервер и его сборку Debug/net10.0.");
        }
    }

    private static void RememberElements(ServerState server, JsonElement? content)
    {
        if (content is not { ValueKind: JsonValueKind.Object } root ||
            !root.TryGetProperty("documentSession", out var session) || session.ValueKind != JsonValueKind.String ||
            !Guid.TryParseExact(session.GetString(), "N", out _) ||
            !root.TryGetProperty("elements", out var elements) || elements.ValueKind != JsonValueKind.Array)
            return;
        var ids = new List<long>();
        foreach (var element in elements.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object ||
                !element.TryGetProperty("ElementId", out var id) || id.ValueKind != JsonValueKind.Number ||
                !id.TryGetInt64(out var value) || value <= 0) return;
            ids.Add(value);
        }
        server.LastElements = (ids.Distinct().ToArray(), session.GetString()!);
    }

    private static bool TryPrintElements(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("result", out var wrapped)) root = wrapped;
            if (root.ValueKind == JsonValueKind.String) return TryPrintElements(root.GetString() ?? "");
            if (root.ValueKind != JsonValueKind.Array || root.EnumerateArray().Any(e => e.ValueKind != JsonValueKind.Object)) return false;
            if (root.GetArrayLength() == 0) Console.WriteLine("Элементы не найдены.");
            foreach (var element in root.EnumerateArray())
            {
                string Value(string name)
                {
                    if (!element.TryGetProperty(name, out var value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                        return "не определено";
                    var text = SafeText(value.ToString());
                    return string.IsNullOrWhiteSpace(text) ? "не определено" : text;
                }
                Console.WriteLine($"Категория: {Value("Category")}");
                Console.WriteLine($"🔽 Имя семейства: {Value("FamilyName")}");
                Console.WriteLine($"⏬ Имя типа: {Value("TypeName")}");
                Console.WriteLine($"🆔 Element-ID: {Value("ElementId")}");
                if (element.TryGetProperty("SystemProperties", out var properties) && properties.ValueKind == JsonValueKind.Array)
                {
                    foreach (var property in properties.EnumerateArray())
                    {
                        if (property.ValueKind != JsonValueKind.Object) continue;
                        string Text(string name) => property.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                            ? SafeText(v.GetString() ?? "") : "";
                        var status = Text("status");
                        var display = status switch
                        {
                            "unsupported" => "нет универсального системного способа",
                            "notApplicable" => "не применимо",
                            "error" => "ошибка чтения",
                            _ => "не определено"
                        };
                        if (status == "ok" && property.TryGetProperty("value", out var propertyValue))
                        {
                            display = propertyValue.ValueKind == JsonValueKind.Number && propertyValue.TryGetDouble(out var number)
                                ? number.ToString("0.###", System.Globalization.CultureInfo.GetCultureInfo("ru-RU"))
                                : propertyValue.ValueKind == JsonValueKind.String ? SafeText(propertyValue.GetString() ?? "") : "не определено";
                            if (Text("unit") is { Length: > 0 } unit) display += " " + unit;
                        }
                        if (Text("source") == "type") display += " (данные типа)";
                        if (Text("note") is { Length: > 0 } note) display += ". " + note;
                        Console.WriteLine($"⬪ {Text("label")}: {display}");
                    }
                }
                Console.WriteLine();
            }
            return true;
        }
        catch (JsonException) { return false; }
    }

    private static string SafeText(string text)
    {
        text = Regex.Replace(text, @"(?i)\b(?:sk-[\w-]+|Bearer\s+\S+|(?:api[_ -]?key|token|password|secret|пароль|токен)\s*[:=]\s*\S+)", "[скрыто]");
        text = Regex.Replace(text, @"[\p{Cc}\p{Cf}]", " ");
        return Regex.Replace(text, @"\s+", " ").Trim();
    }
}
