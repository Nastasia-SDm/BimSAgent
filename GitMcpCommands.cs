using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace BimSAgentApp;

internal static class GitMcpCommands
{
    private const string ServerDirectory = @"D:\1. Job\Git-S-MCP";
    private const string AssemblyName = "GitSMcp";

    private static StdioClientTransport CreateTransport()
    {
        var environment = new Dictionary<string, string?>();
        foreach (var name in new[] { "PATH", "SystemRoot", "TEMP", "TMP", "DOTNET_ROOT", "ProgramFiles" })
            environment[name] = Environment.GetEnvironmentVariable(name);

        return new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = AssemblyName,
            Command = "dotnet",
            Arguments =
            [
                Path.Combine(ServerDirectory, "bin", "Debug", "net10.0", AssemblyName + ".dll")
            ],
            WorkingDirectory = ServerDirectory,
            InheritEnvironmentVariables = false,
            EnvironmentVariables = environment,
            ShutdownTimeout = TimeSpan.FromMilliseconds(500),
            StandardErrorLines = line => Console.Error.WriteLine($"Git-S-MCP: {line}")
        });
    }

    public static async Task<string> GetCurrentBranchAsync(
        string repositoryPath,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        await using var client = await McpClient.CreateAsync(
            CreateTransport(),
            cancellationToken: timeout.Token);

        var result = await client.CallToolAsync(
            "get-current-git-branch",
            new Dictionary<string, object?>
            {
                ["repositoryPath"] = Path.GetFullPath(repositoryPath)
            },
            cancellationToken: timeout.Token);

        var text = string.Join(
            "\n",
            result.Content.OfType<TextContentBlock>().Select(x => x.Text));

        if (result.IsError == true)
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(text) ? "Git-S-MCP вернул ошибку." : text);

        return text;
    }

    public static async Task HandleAsync(string command, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            await HandleCoreAsync(command, timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Console.Error.WriteLine("Git-S-MCP не ответил за 10 секунд.");
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception or McpException)
        {
            Console.Error.WriteLine($"Git-S-MCP: {e.Message}");
        }
    }
    private static async Task HandleCoreAsync(string command, CancellationToken cancellationToken)
    {
        var trimmed = command.Trim();

        if (trimmed.Equals("git-mcp-tools", StringComparison.OrdinalIgnoreCase))
        {
            await using var client = await McpClient.CreateAsync(
                CreateTransport(),
                cancellationToken: cancellationToken);

            foreach (var tool in await client.ListToolsAsync(cancellationToken: cancellationToken))
                Console.WriteLine($"{tool.Name}: {tool.Description}");

            return;
        }

        if (trimmed.Equals("git-mcp-branch", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("git-mcp-branch ", StringComparison.OrdinalIgnoreCase))
        {
            var rest = trimmed["git-mcp-branch".Length..].Trim();
            var repositoryPath = string.IsNullOrWhiteSpace(rest)
                ? Directory.GetCurrentDirectory()
                : rest.Trim('"');

            Console.WriteLine(await GetCurrentBranchAsync(repositoryPath, cancellationToken));
            return;
        }

        Console.WriteLine("Доступно: git-mcp-tools; git-mcp-branch [путь к Git-репозиторию].");
    }
}

