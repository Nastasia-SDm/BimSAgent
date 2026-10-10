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
    private static readonly ServerState Server4 = new(@"D:\BIM-S-MCP-4", "BimS.Mcp4");
    private static readonly ServerState Server5 = new(@"D:\BIM-S-MCP-5", "BimS.Mcp5");
    private static readonly ServerState Server6 = new(@"D:\BIM-S-MCP-6", "BimS.Mcp6");
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
        "mcp4" => Server4,
        "mcp5" => Server5,
        "mcp6" => Server6,
        _ => throw new ArgumentException("Неизвестный MCP-сервер.", nameof(server))
    };

    private static StdioClientTransport CreateTransport(ServerState server)
    {
        var environment = new Dictionary<string, string?>();

        foreach (var name in new[] { "PATH", "SystemRoot", "TEMP", "TMP", "DOTNET_ROOT", "ProgramFiles" })
            environment[name] = Environment.GetEnvironmentVariable(name);
        if (server == Server6)
            foreach (var name in new[] { "BIMS_FAMILY_LLM_ENDPOINT", "BIMS_FAMILY_LLM_KEY", "BIMS_FAMILY_LLM_MODEL", "BIMS_FAMILY_LLM_INPUT_RATE", "BIMS_FAMILY_LLM_OUTPUT_RATE", "BIMS_FAMILY_LLM_VISION" })
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
        (Name: "mcp3", Server: Server3),
        (Name: "mcp4", Server: Server4),
        (Name: "mcp5", Server: Server5),
        (Name: "mcp6", Server: Server6)
    })
        {
            if (item.Name is "mcp4" or "mcp5" or "mcp6" &&
                !File.Exists(Path.Combine(item.Server.Directory, "bin", "Debug", "net10.0", item.Server.AssemblyName + ".dll")))
                continue;
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
                if (item.Name == "mcp1" &&
                    tool.Name is "stop-model-watch"
                        or "get-model-elements"
                        or "get-model-elements-parameters"
                        or "create-model-elements-report")
                    continue;

                catalog.Add(new
                {
                    server = item.Name,
                    tool = tool.Name,
                    description = tool.Description ?? "Описание отсутствует",
                    inputSchema = tool.JsonSchema
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
    private sealed record FamilyJob(
    string RunId,
    string Status,
    string? ResultPath,
    string? HtmlPath,
    string? Error);

    private static string FamilyReportRoot(int stage) => stage switch
    {
        4 => @"D:\BIM-S-MCP-4_Отчеты_Семейства",
        5 => @"D:\BIM-S-MCP-5_Отчеты_Сравнение",
        6 => @"D:\BIM-S-MCP-6_Отчеты_Результат сравнения",
        _ => throw new ArgumentOutOfRangeException(nameof(stage))
    };

    private static string NormalizeFamilyVersion(string value)
    {
        value = value.Trim();

        if (Regex.IsMatch(value, @"^V\d{3,}$", RegexOptions.IgnoreCase))
            return value.ToUpperInvariant();

        if (int.TryParse(value, out var number) && number > 0)
            return $"V{number:000}";

        throw new ArgumentException("Номер JSON должен быть, например, V001 или 1.");
    }

    private static string ResolveFamilyVersion(int stage, string version)
    {
        var name = NormalizeFamilyVersion(version);
        var path = Path.Combine(FamilyReportRoot(stage), name + ".json");

        if (!File.Exists(path))
            throw new FileNotFoundException($"JSON {name} для MCP{stage} не найден.");

        return path;
    }

    private static string NextFamilyVersion(int stage)
    {
        var root = FamilyReportRoot(stage);
        Directory.CreateDirectory(root);

        var max = Directory.EnumerateFiles(root, "V*.json", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileNameWithoutExtension)
            .Select(x => Regex.Match(x ?? "", @"^V(\d+)$", RegexOptions.IgnoreCase))
            .Where(x => x.Success)
            .Select(x => int.Parse(x.Groups[1].Value))
            .DefaultIfEmpty(0)
            .Max();

        return $"V{max + 1:000}";
    }

    private static FamilyJob ParseFamilyJob(ToolResult result)
    {
        if (result.IsError || string.IsNullOrWhiteSpace(result.StructuredContent))
            throw new InvalidOperationException(result.Text);

        using var document = JsonDocument.Parse(result.StructuredContent);
        var root = document.RootElement;

        string? Get(string name) =>
            root.TryGetProperty(name, out var value) &&
            value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

        return new FamilyJob(
            Get("runId") ?? throw new InvalidDataException("Нет runId."),
            Get("status") ?? throw new InvalidDataException("Нет status."),
            Get("resultPath"),
            Get("htmlPath"),
            Get("error"));
    }

    private static async Task<FamilyJob> WaitFamilyJobAsync(
        string server,
        ToolResult started,
        CancellationToken cancellationToken)
    {
        var job = ParseFamilyJob(started);

        var statusTool = server switch
        {
            "mcp4" => "get-extraction-status",
            "mcp5" => "get-comparison-status",
            "mcp6" => "get-unification-status",
            _ => throw new ArgumentException("Неизвестный MCP-сервер.")
        };

        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(30);

        while (job.Status is "queued" or "running")
        {
            if (DateTimeOffset.UtcNow >= deadline)
                throw new TimeoutException("Истекло время ожидания MCP.");

            await Task.Delay(750, cancellationToken);

            var status = await ExecuteToolAsync(
                server,
                statusTool,
                new Dictionary<string, object?> { ["runId"] = job.RunId },
                cancellationToken);

            job = ParseFamilyJob(status);
        }

        if (job.Status is not ("complete" or "partial" or "empty"))
            throw new InvalidOperationException(
                job.Error ?? $"MCP завершился со статусом {job.Status}.");

        if (string.IsNullOrWhiteSpace(job.ResultPath))
            throw new InvalidDataException("MCP не вернул JSON результата.");

        return job;
    }

    private static string PublishFamilyResult(int stage, FamilyJob job)
    {
        var version = NextFamilyVersion(stage);
        var root = FamilyReportRoot(stage);

        File.Copy(
            job.ResultPath!,
            Path.Combine(root, version + ".json"),
            false);

        if (stage == 6 &&
            !string.IsNullOrWhiteSpace(job.HtmlPath) &&
            File.Exists(job.HtmlPath))
        {
            File.Copy(
                job.HtmlPath,
                Path.Combine(root, version + ".html"),
                false);
        }

        return version;
    }
    public static async Task HandleAsync(string command, CancellationToken cancellationToken)
    {
        var parts = command.Split(
    (char[]?)null,
    3,
    StringSplitOptions.RemoveEmptyEntries);

        var familyCommand =
            parts.Length > 0 ? parts[0].ToLowerInvariant() : "";

        if (familyCommand is
            "mcp4-tools" or "mcp5-tools" or "mcp6-tools" or
            "mcp4-call" or "mcp5-call" or "mcp6-call")
        {
            var familyServer = familyCommand[..4];

            if (familyCommand.EndsWith("-tools", StringComparison.Ordinal))
            {
                await using var familyClient =
                    await McpClient.CreateAsync(
                        CreateTransport(GetServer(familyServer)),
                        cancellationToken: cancellationToken);

                foreach (var tool in await familyClient.ListToolsAsync(
                             cancellationToken: cancellationToken))
                    Console.WriteLine($"{tool.Name}: {tool.Description}");

                return;
            }

            if (parts.Length < 2)
            {
                Console.WriteLine($"{familyServer}-call: не указана команда.");
                return;
            }

            var toolName = parts[1];
            var familyArguments = new Dictionary<string, object?>();
            var shortFamilyCommand = false;

            if (familyServer == "mcp4" &&
                toolName.Equals(
                    "extract-family-library",
                    StringComparison.OrdinalIgnoreCase) &&
                parts.Length == 2)
            {
                familyArguments["mode"] = "library";
                shortFamilyCommand = true;
            }
            else if (familyServer == "mcp5" &&
                     toolName.Equals(
                         "compare-family-library",
                         StringComparison.OrdinalIgnoreCase) &&
                     parts.Length == 3 &&
                     !parts[2].TrimStart().StartsWith("{", StringComparison.Ordinal))
            {
                familyArguments["mcp4Path"] =
                    ResolveFamilyVersion(4, parts[2]);

                shortFamilyCommand = true;
            }
            else if (familyServer == "mcp6" &&
                     toolName.Equals("recommend-family-from-passport", StringComparison.OrdinalIgnoreCase) &&
                     parts.Length == 3 &&
                     !parts[2].TrimStart().StartsWith("{", StringComparison.Ordinal))
            {
                PassportCommand.Arguments passportCall;
                try { passportCall = PassportCommand.Parse(parts[2]); }
                catch (ArgumentException error) { Console.WriteLine(error.Message); return; }
                // Verify publication before launching a durable job. The server resolves the version again.
                ResolveFamilyVersion(4, passportCall.Version);
                familyArguments["mcp4Version"] = passportCall.Version;
                familyArguments["passportName"] = passportCall.FileName;
                shortFamilyCommand = true;
            }
            else if (familyServer == "mcp6" &&
                     toolName.Equals(
                         "recommend-family-unification",
                         StringComparison.OrdinalIgnoreCase) &&
                     parts.Length == 3 &&
                     !parts[2].TrimStart().StartsWith("{", StringComparison.Ordinal))
            {
                var mcp5Path = ResolveFamilyVersion(5, parts[2]);

                using var document =
                    JsonDocument.Parse(File.ReadAllText(mcp5Path));

                var mcp4Path = document.RootElement
                    .GetProperty("sourceMcp4")
                    .GetProperty("manifestPath")
                    .GetString()
                    ?? throw new InvalidDataException(
                        "JSON MCP5 не содержит ссылку на MCP4.");

                familyArguments["mcp4Path"] = mcp4Path;
                familyArguments["mcp5Path"] = mcp5Path;
                familyArguments["useLlm"] = true;

                shortFamilyCommand = true;
            }
            else if (parts.Length == 3)
            {
                using var document = JsonDocument.Parse(parts[2]);

                if (document.RootElement.ValueKind != JsonValueKind.Object)
                    throw new ArgumentException(
                        "Аргументы должны быть JSON-объектом.");

                foreach (var property in document.RootElement.EnumerateObject())
                    if (!familyArguments.TryAdd(
                            property.Name,
                            property.Value.Clone()))
                        throw new ArgumentException(
                            "Повторяющееся поле аргументов.");
            }

            var familyResult = await ExecuteToolAsync(
                familyServer,
                toolName,
                familyArguments,
                cancellationToken);

            if (!shortFamilyCommand)
            {
                Console.WriteLine(familyResult.Text);
                return;
            }

            if (toolName.Equals("recommend-family-from-passport", StringComparison.OrdinalIgnoreCase))
                Console.WriteLine($"Анализ паспорта запущен; runId={ParseFamilyJob(familyResult).RunId}");

            var job = await WaitFamilyJobAsync(
                familyServer,
                familyResult,
                cancellationToken);

            var stage = familyServer[3] - '0';
            var version = PublishFamilyResult(stage, job);

            Console.WriteLine($"MCP{stage} готов. JSON: {version}");

            if (stage == 6)
            {
                var html = Path.Combine(
                    FamilyReportRoot(6),
                    version + ".html");

                if (File.Exists(html))
                    Console.WriteLine($"HTML: {html}");
            }

            return;
        }
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
                if (prefix == "mcp3" &&
                    parts[1].Equals("compare-model-versions", StringComparison.OrdinalIgnoreCase) &&
                    !parts[2].TrimStart().StartsWith("{", StringComparison.Ordinal))
                {
                    var versions = parts[2].Split(
                        (char[]?)null,
                        StringSplitOptions.RemoveEmptyEntries);

                    if (versions.Length != 2)
                    {
                        Console.WriteLine(
                            "Использование: mcp3-call compare-model-versions V003 V007 или previous latest; также поддержан JSON с version1, version2, mode.");
                        return;
                    }

                    arguments["version1"] = versions[0];
                    arguments["version2"] = versions[1];
                }
                else
                {
                    try
                    {
                        using var json = JsonDocument.Parse(parts[2]);

                        if (json.RootElement.ValueKind != JsonValueKind.Object)
                            throw new JsonException();

                        foreach (var property in json.RootElement.EnumerateObject())
                            if (!arguments.TryAdd(
                                property.Name,
                                property.Value.Clone()))
                                throw new JsonException();
                    }
                    catch (JsonException)
                    {
                        Console.WriteLine(
                            "MCP: аргументы должны быть JSON-объектом без повторяющихся свойств.");
                        return;
                    }
                }
            }
            if (isCall &&
     parts[1] == "get-model-elements-parameters" &&
     parts.Length == 2)
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
                if (result.IsError != true &&
    parts[1] == "get-model" &&
    result.StructuredContent is { } modelData &&
    TryPrintModelSnapshot(modelData.ToString()))
                    return;
                if (result.IsError != true &&
prefix == "mcp3" &&
parts[1] == "compare-model-versions")
                {
                    foreach (var content in result.Content.OfType<TextContentBlock>())
                        Console.WriteLine(SafeText(content.Text));

                    return;
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
    private static bool TryPrintModelSnapshot(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("snapshot", out var snapshot) ||
                snapshot.ValueKind != JsonValueKind.Object ||
                !snapshot.TryGetProperty("elements", out var elements) ||
                elements.ValueKind != JsonValueKind.Array)
                return false;

            foreach (var element in elements.EnumerateArray())
            {
                string Value(string name)
                {
                    if (!element.TryGetProperty(name, out var value) ||
                        value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                        return "не определено";

                    var text = SafeText(value.ToString());
                    return string.IsNullOrWhiteSpace(text) ? "не определено" : text;
                }

                Console.WriteLine($"Категория: {Value("category")}");
                Console.WriteLine($"🆔 Element-ID: {Value("elementId")}");
                Console.WriteLine($"🔽 Имя семейства: {Value("familyName")}");
                Console.WriteLine($"⏬ Имя типа: {Value("typeName")}");

                PrintParameters("instanceParameters");
                PrintParameters("typeParameters");

                Console.WriteLine();

                void PrintParameters(string propertyName)
                {
                    if (!element.TryGetProperty(propertyName, out var parameters) ||
                        parameters.ValueKind != JsonValueKind.Array)
                        return;

                    foreach (var parameter in parameters.EnumerateArray())
                    {
                        if (parameter.ValueKind != JsonValueKind.Object)
                            continue;

                        var name = parameter.TryGetProperty("name", out var n)
                            ? SafeText(n.ToString())
                            : "Параметр";

                        string value = "не определено";

                        foreach (var field in new[] { "displayValue", "convertedValue", "rawValue" })
                        {
                            if (parameter.TryGetProperty(field, out var v) &&
                                v.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined &&
                                !string.IsNullOrWhiteSpace(v.ToString()))
                            {
                                value = SafeText(v.ToString());
                                break;
                            }
                        }

                        if (value != "не определено")
                            Console.WriteLine($"⬪ {name}: {value}");
                    }
                }
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
    private static string SafeText(string text)
    {
        text = Regex.Replace(text, @"(?i)\b(?:sk-[\w-]+|Bearer\s+\S+|(?:api[_ -]?key|token|password|secret|пароль|токен)\s*[:=]\s*\S+)", "[скрыто]");
        text = Regex.Replace(text, @"[\p{Cc}\p{Cf}]", " ");
        return Regex.Replace(text, @"\s+", " ").Trim();
    }
}
