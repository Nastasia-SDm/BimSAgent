using ModelContextProtocol.Client;
using System.Text.RegularExpressions;

namespace BimSAgentApp;

internal static class McpCommands
{
    private const string ServerDirectory = @"D:\BIM-S-MCP\BIM-S_MCP-Server";

    public static async Task HandleAsync(string command, CancellationToken cancellationToken)
    {
        if (!command.Equals("mcp-tools", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("Неизвестная MCP-команда. Доступна: mcp-tools.");
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
        catch (OperationCanceledException) { Console.WriteLine("MCP: время подключения истекло."); }
        catch (Exception)
        {
            // Protocol/process exception messages can contain server output or secrets.
            Console.WriteLine("MCP: не удалось получить tools. Проверьте сервер и его сборку Debug/net10.0.");
        }
    }

    private static string SafeText(string text)
    {
        text = Regex.Replace(text, @"(?i)\b(?:sk-[\w-]+|Bearer\s+\S+|(?:api[_ -]?key|token|password|secret|пароль|токен)\s*[:=]\s*\S+)", "[скрыто]");
        text = Regex.Replace(text, @"[\p{Cc}\p{Cf}]", " ");
        return Regex.Replace(text, @"\s+", " ").Trim();
    }
}
