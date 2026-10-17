using BimSAgentApp.Rag;

namespace BimSAgentApp;

internal static class HelpCommands
{
    public static async Task HandleAsync(string input, CancellationToken cancellationToken)
    {
        var trimmed = input.Trim();
        if (trimmed.Equals("/help", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("Использование: /help <вопрос о BimSAgent>");
            return;
        }

        if (!trimmed.StartsWith("/help ", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Использование: /help <вопрос о BimSAgent>");

        var question = trimmed["/help".Length..].Trim();
        if (question.Length == 0)
        {
            Console.WriteLine("Использование: /help <вопрос о BimSAgent>");
            return;
        }

        var projectRoot = Directory.GetCurrentDirectory();
        var helpRagDirectory = Path.Combine(projectRoot, "help-rag-data");
        if (!Directory.Exists(helpRagDirectory))
            throw new InvalidOperationException("Индекс /help не найден. Сначала выполните: dotnet run -- help-index");

        var gitContext = await GitMcpCommands.GetCurrentBranchAsync(projectRoot, cancellationToken);
        var ragQuestion = $"""
            Вопрос разработчика о BimSAgent: {question}

            Актуальный Git-контекст, полученный через MCP:
            {gitContext}

            Ответь по документации BimSAgent из RAG. Если вопрос касается текущей Git-ветки, используй Git-контекст выше.
            """;

        await RagCommands.RunAsync(
            ["rag", "ask", ragQuestion, "--strategy", "structural"],
            cancellationToken,
            dataDirectory: helpRagDirectory);
    }
}
