using System.Text.Json;
using BimS.Families;

namespace BimSAgentApp;

internal static class FamilyLibraryCommands
{
    internal sealed class PipelineState
    {
        public string PipelineId { get; set; } = "";
        public string Mode { get; set; } = "empty";
        public bool UseLlm { get; set; }
        public int Stage { get; set; } = 4;
        public string? CurrentRunId { get; set; }
        public string? Mcp4Path { get; set; }
        public string? Mcp5Path { get; set; }
        public string? Mcp6Path { get; set; }
        public string? HtmlPath { get; set; }
        public string Status { get; set; } = "running";
    }

    public static async Task<int> RunAsync(string[] args, CancellationToken ct)
    {
        PipelineState? state = null;
        string? checkpoint = null;
        try
        {
            if (args.Length < 3 || args[1] != "pipeline")
                throw new ArgumentException("family pipeline --synthetic | --empty | --library [--llm]; family pipeline --resume <pipelineId>");
            if (args[2] == "--resume")
            {
                if (args.Length != 4) throw new ArgumentException("Для продолжения нужен pipelineId.");
                checkpoint = Path.Combine(Roots.Run(6, args[3]), "pipeline.json");
                state = Store.Load<PipelineState>(checkpoint);
                if (state.PipelineId != args[3] || state.Stage is < 4 or > 7 || state.Status is "complete" or "empty")
                    throw new InvalidDataException("Некорректный или завершённый конвейер.");
            }
            else
            {
                if (args.Length > 4 || args.Length == 4 && args[3] != "--llm") throw new ArgumentException("Неизвестные аргументы конвейера.");
                var mode = args[2] switch { "--synthetic" => "synthetic", "--empty" => "empty", "--library" => "library", _ => throw new ArgumentException("Укажите --synthetic, --empty или --library.") };
                state = new() { PipelineId = Guid.NewGuid().ToString("N"), Mode = mode, UseLlm = args.Length == 4 };
                checkpoint = Path.Combine(Roots.Run(6, state.PipelineId), "pipeline.json");
            }
            Store.Save(checkpoint, state);
            Console.WriteLine($"Конвейер: {state.PipelineId}; режим: {state.Mode}");
            while (state.Stage <= 6)
            {
                ct.ThrowIfCancellationRequested();
                var server = "mcp" + state.Stage;
                if (state.CurrentRunId == null)
                {
                    var tool = state.Stage switch { 4 => "extract-family-library", 5 => "compare-family-library", _ => "recommend-family-unification" };
                    var arguments = new Dictionary<string, object?>();
                    if (state.Stage == 4) arguments["mode"] = state.Mode;
                    else arguments["mcp4Path"] = Roots.Input(4, state.Mcp4Path ?? throw new InvalidDataException("Отсутствует результат MCP4."));
                    if (state.Stage == 6)
                    {
                        arguments["mcp5Path"] = Roots.Input(5, state.Mcp5Path ?? throw new InvalidDataException("Отсутствует результат MCP5."));
                        arguments["useLlm"] = state.UseLlm;
                    }
                    var started = await Call(server, tool, arguments, ct);
                    state.CurrentRunId = started.RunId;
                    Store.Save(checkpoint, state);
                }
                var statusTool = state.Stage switch { 4 => "get-extraction-status", 5 => "get-comparison-status", _ => "get-unification-status" };
                var job = await Call(server, statusTool, new() { ["runId"] = state.CurrentRunId }, ct);
                if (job.Status is "cancelled" or "failed" or "interrupted")
                    job = await Call(server, "resume-family-run", new() { ["runId"] = state.CurrentRunId }, ct);
                var deadline = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(30);
                while (job.Status is "queued" or "running")
                {
                    if (DateTimeOffset.UtcNow >= deadline) throw new TimeoutException("Истекло время ожидания этапа; используйте продолжение конвейера.");
                    await Task.Delay(750, ct);
                    job = await Call(server, statusTool, new() { ["runId"] = state.CurrentRunId }, ct);
                }
                Console.WriteLine($"MCP{state.Stage}: {job.Status}; запуск {job.RunId}");
                if (job.Status is not ("complete" or "partial" or "empty") || job.ResultPath == null)
                    throw new InvalidOperationException($"MCP{state.Stage}: {job.Error ?? job.Status}. Продолжение: family pipeline --resume {state.PipelineId}");
                var path = Roots.Input(state.Stage, job.ResultPath);
                if (state.Stage == 4) state.Mcp4Path = path;
                if (state.Stage == 5) state.Mcp5Path = path;
                if (state.Stage == 6) { state.Mcp6Path = path; state.HtmlPath = job.HtmlPath == null ? null : Roots.Inside(Roots.Report(6), job.HtmlPath); state.Status = job.Status; }
                state.Stage++; state.CurrentRunId = null;
                Store.Save(checkpoint, state);
            }
            Console.WriteLine($"JSON: {state.Mcp6Path}\nHTML: {state.HtmlPath}");
            return 0;
        }
        catch (Exception error) when (error is ArgumentException or InvalidDataException or InvalidOperationException or IOException or TimeoutException or OperationCanceledException)
        {
            if (state != null && checkpoint != null)
            {
                if (state.CurrentRunId != null)
                {
                    using var cancelDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    try { await Call("mcp" + state.Stage, "cancel-family-run", new() { ["runId"] = state.CurrentRunId }, cancelDeadline.Token); }
                    catch (Exception cancelError) when (cancelError is not OutOfMemoryException and not AccessViolationException) { }
                }
                state.Status = error is OperationCanceledException ? "cancelled" : "failed";
                Store.Save(checkpoint, state);
            }
            Console.Error.WriteLine(error is OperationCanceledException ? "Конвейер отменён; контрольная точка сохранена." : error.Message);
            return 1;
        }
    }

    private static async Task<JobState> Call(string server, string tool, Dictionary<string, object?> arguments, CancellationToken ct)
    {
        var result = await McpCommands.ExecuteToolAsync(server, tool, arguments, ct);
        if (result.IsError || result.StructuredContent == null) throw new InvalidOperationException($"{server}/{tool}: {result.Text}");
        var job = JsonSerializer.Deserialize<JobState>(result.StructuredContent, Store.Json) ?? throw new InvalidDataException("Отсутствует статус задания.");
        if (!Guid.TryParseExact(job.RunId, "N", out _) || "mcp" + job.Stage != server) throw new InvalidDataException("Идентификатор этапа не совпадает.");
        return job;
    }
}
