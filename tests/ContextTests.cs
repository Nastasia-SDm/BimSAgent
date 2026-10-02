using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BimSAgentApp;

static class ContextTests
{
    public static void Run(Action<bool, string> check, string output)
    {
        var model = Snapshot(false); var documentation = Snapshot(true);
        var comparisonData = JsonNode.Parse("""
            {"status":"complete","version1":"V003","version2":"V007","jsonPath":"report.json",
             "sections":{"3d":{"status":"complete","counts":{"changed":1,"unchanged":599},
             "changed":[{"elementId":123,"elementLabel":"Стена","oldState":{"secretNoise":"unused"},
             "semanticChanges":[{"parameterKey":"unused","name":"Высота","old":"1000 мм","new":"2650.888 мм","exactValues":null}]}],
             "added":[{"elementId":777,"elementLabel":"Стена"}],"removed":[]},"2d":{"status":"complete","changed":[],"added":[],"removed":[]}},
             "limitations":["Сохранённая область снимков."]}
            """)!.AsObject();
        McpCommands.ToolResult Compare(JsonObject data, bool error = false) => new("mcp3", "compare-model-versions", error, "text not for final", data.ToJsonString());
        var comparison = Compare(comparisonData);
        var cases = new (string Name, McpCommands.ToolResult[] Results)[]
        {
            ("MCP1 -> MCP3", [model, comparison]),
            ("MCP1 -> MCP2 -> MCP3", [model, documentation, comparison]),
            ("MCP1 only", [model]), ("MCP2 only", [documentation])
        };
        var sizes = new List<object>();
        foreach (var (name, results) in cases)
        {
            var original = JsonSerializer.Serialize(results);
            var compact = McpFinalContext.Build(results, "Текущее состояние");
            check(compact.BeforeUtf8Bytes == Encoding.UTF8.GetByteCount(original), "baseline byte measurement: " + name);
            check(compact.AfterUtf8Bytes <= McpFinalContext.MaxBytes && compact.AfterUtf8Bytes < compact.BeforeUtf8Bytes / 5, "bounded size reduction: " + name);
            check(JsonSerializer.Serialize(results) == original, "full internal results untouched: " + name);
            check(!compact.Text.Contains("rawValue") && !compact.Text.Contains("snapshot\"") && !compact.Text.Contains("oldState"), "no raw snapshots: " + name);
            var data = JsonNode.Parse(compact.Text)!;
            if (results.Contains(comparison))
                check(data["results"]!.AsArray().Count == 1 && compact.Text.Contains("2650.888 мм"), "successful comparison supersedes upstream snapshots: " + name);
            else check(compact.Text.Contains("omittedElements") && compact.Text.Contains("omittedParameters"), "current-state omissions explicit: " + name);
            sizes.Add(new { scenario = name, compact.BeforeUtf8Bytes, compact.AfterUtf8Bytes });
            Console.WriteLine($"SIZE {name}: {compact.BeforeUtf8Bytes} -> {compact.AfterUtf8Bytes} UTF-8 bytes");
        }
        File.WriteAllText(Path.Combine(output, "mcp-context-sizes.json"), JsonSerializer.Serialize(sizes, new JsonSerializerOptions { WriteIndented = true }));
        var requested = McpFinalContext.Build([model], "Элемент ID: 1599, параметр Параметр 29").Text;
        check(requested.Contains("1599") && requested.Contains("Параметр 29") && !requested.Contains("Параметр 0\""), "explicit ID outside first page and exact parameter name");
        var noMatch = McpFinalContext.Build([model], "ID: 999999").Text;
        check(noMatch.Contains("\"matchedCount\":0"), "missing requested ID is not replaced with unrelated rows");
        var aliases = new JsonObject { ["status"] = "complete", ["comparisons"] = new JsonObject { ["3d"] = comparisonData.DeepClone(), ["2d"] = comparisonData.DeepClone() } };
        check(JsonNode.Parse(McpFinalContext.Build([model, documentation, Compare(aliases)], "").Text)!["results"]!.AsArray().Count == 1, "independently resolved both sections suppress snapshots");
        var only3d = (JsonObject)comparisonData.DeepClone(); only3d["sections"]!["2d"]!["status"] = "notRequested";
        check(JsonNode.Parse(McpFinalContext.Build([documentation, Compare(only3d)], "").Text)!["results"]!.AsArray().Count == 2, "uncompared dimension is retained compactly");
        check(JsonNode.Parse(McpFinalContext.Build([model, Compare(comparisonData, true)], "").Text)!["results"]!.AsArray().Count == 2, "failed comparison does not suppress upstream state");
        var huge = (JsonObject)comparisonData.DeepClone(); huge["limitations"] = new JsonArray(new string('x', 100000));
        var limited = McpFinalContext.Build([Compare(huge)], "");
        check(limited.AfterUtf8Bytes <= McpFinalContext.MaxBytes && limited.Text.Contains("context-byte-budget-exceeded"), "oversize comparison fails closed with explicit omission");
        var unknown = new McpCommands.ToolResult("mcp1", "unknown", false, new string('x', 100000), "not json");
        check(McpFinalContext.Build([unknown], "").Text.Contains("unsupported-result-schema"), "malformed/unknown result never leaks raw text");
        check(McpFinalContext.StripHistoricalContext("question" + McpFinalContext.Marker + new string('x', 100000)) == "question", "legacy MCP payload removed from outgoing history");
    }

    private static McpCommands.ToolResult Snapshot(bool documentation)
    {
        var rows = new JsonArray(); var parameters = new JsonArray();
        for (var i = 0; i < 600; i++)
        {
            var values = new JsonArray();
            for (var p = 0; p < 30; p++) values.Add(new JsonObject
            {
                ["parameterId"] = p, ["ownerElementId"] = 1000 + i, ["source"] = "instance", ["name"] = "Параметр " + p,
                ["displayValue"] = p + " мм", ["rawValue"] = p / 304.8, ["convertedValue"] = p,
                ["unitTypeId"] = "millimeters", ["storageType"] = "Double", ["status"] = "ok",
                ["builtInParameterNames"] = new JsonArray("SYNTHETIC_PARAMETER"), ["errors"] = new JsonArray()
            });
            var row = new JsonObject { ["elementId"] = 1000 + i, ["category"] = "Стены", ["typeName"] = "Fixture" };
            if (documentation)
            {
                row["properties"] = new JsonObject { ["text"] = new JsonObject { ["value"] = "Fixture", ["status"] = "ok" } };
                parameters.Add(new JsonObject { ["ElementId"] = 1000 + i, ["InstanceParameters"] = values });
            }
            else row["instanceParameters"] = values;
            rows.Add(row);
        }
        var snapshot = new JsonObject { ["status"] = "complete", ["elements"] = rows };
        if (documentation) { snapshot["parameters"] = parameters; snapshot["sheets"] = new JsonArray(); snapshot["views"] = new JsonArray(); snapshot["placements"] = new JsonArray(); }
        return new(documentation ? "mcp2" : "mcp1", documentation ? "get-documentation" : "get-model", false,
            "snapshot export complete", new JsonObject { ["status"] = "complete", ["snapshot"] = snapshot }.ToJsonString());
    }
}
