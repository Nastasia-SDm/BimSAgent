namespace BimSAgentApp;

internal static class McpCommands
{
    public sealed record ToolResult(string Server, string Tool, bool IsError,
        string Text, string? StructuredContent);
    public static List<string> Events { get; } = [];
    public static string? FailTool { get; set; }
    private static bool _running;

    public static Task<string> GetToolsCatalogAsync(CancellationToken token)
    {
        Events.Add("catalog");
        return Task.FromResult("[]");
    }

    public static async Task<ToolResult> ExecuteToolAsync(string server, string tool,
        Dictionary<string, object?> arguments, CancellationToken token)
    {
        if (_running) throw new Exception("MCP steps overlapped.");
        _running = true;
        try
        {
            Events.Add(tool);
            await Task.Yield();
            token.ThrowIfCancellationRequested();
            if (tool == "compare-model-versions" &&
                (arguments["version1"]?.ToString() != "previous" ||
                 arguments["version2"]?.ToString() != "latest"))
                throw new Exception("Aliases were changed by the client.");
            return new(server, tool, tool == FailTool, "fixture", tool == "compare-model-versions"
                ? "{\"status\":\"complete\",\"semanticSchemaVersion\":1,\"sections\":{\"3d\":{\"status\":\"complete\",\"changed\":[{\"elementId\":123,\"elementLabel\":\"Стена\",\"semanticChanges\":[{\"name\":\"thickness\",\"old\":\"200 мм\",\"new\":\"250 мм\"}]}]},\"2d\":{\"status\":\"complete\"}}}"
                : "{\"status\":\"complete\",\"snapshot\":{\"status\":\"complete\",\"elements\":[{\"elementId\":123,\"instanceParameters\":[{\"name\":\"thickness\",\"displayValue\":\"250 мм\"}]}]}}");
        }
        finally { _running = false; }
    }
}
