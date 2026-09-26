using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BimSAgentApp;

internal static class McpCommands
{
    private const string ServerDirectory = @"D:\BIM-S-MCP\BIM-S_MCP-Server";

    public static async Task HandleAsync(string command, CancellationToken cancellationToken)
    {
        var parts = command.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var isCall = parts.Length > 0 && parts[0].Equals("mcp-call", StringComparison.OrdinalIgnoreCase);
        if (isCall && parts.Length != 2)
        {
            Console.WriteLine("Использование: mcp-call <tool-name>");
            return;
        }
        if (!isCall && !command.Equals("mcp-tools", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("Неизвестная MCP-команда. Доступны: mcp-tools; mcp-call <tool-name>.");
            return;
        }
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            // Do not inherit credentials, attach a logging provider or echo server stderr.
            var environment = new Dictionary<string, string?>();
            foreach (var name in new[] { "PATH", "SystemRoot", "TEMP", "TMP", "DOTNET_ROOT", "ProgramFiles" })
                environment[name] = Environment.GetEnvironmentVariable(name);
            var transport = new StdioClientTransport(new StdioClientTransportOptions
            {
                Name = "BIM-S_MCP-Server",
                Command = "dotnet",
                Arguments = [Path.Combine(ServerDirectory, "bin", "Debug", "net10.0", "BIM-S_MCP-Server.dll")],
                WorkingDirectory = ServerDirectory,
                InheritEnvironmentVariables = false,
                EnvironmentVariables = environment,
                StandardErrorLines = _ => { }
            });
            await using var client = await McpClient.CreateAsync(transport, cancellationToken: timeout.Token);
            if (isCall)
            {
                var result = await client.CallToolAsync(parts[1], new Dictionary<string, object?>(),
                    cancellationToken: timeout.Token);
                if (result.IsError == true)
                    Console.WriteLine("MCP: инструмент вернул ошибку.");
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

    private static string SafeText(string text)
    {
        text = Regex.Replace(text, @"(?i)\b(?:sk-[\w-]+|Bearer\s+\S+|(?:api[_ -]?key|token|password|secret|пароль|токен)\s*[:=]\s*\S+)", "[скрыто]");
        text = Regex.Replace(text, @"[\p{Cc}\p{Cf}]", " ");
        return Regex.Replace(text, @"\s+", " ").Trim();
    }
}
