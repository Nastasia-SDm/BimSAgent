using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace BimSAgentApp;

internal static class McpFinalContext
{
    internal const int MaxBytes = 48 * 1024;
    internal const string Marker = "\n\nMCP результаты:\n";
    private static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    internal sealed record Context(string Text, long BeforeUtf8Bytes, int AfterUtf8Bytes);

    internal static string StripHistoricalContext(string text)
    {
        var index = text.IndexOf(Marker, StringComparison.Ordinal);
        return index < 0 ? text : text[..index];
    }

    internal static Context Build(IReadOnlyList<McpCommands.ToolResult> results, string question)
    {
        // Measure the former payload without allocating another full snapshot string.
        using var counter = new ByteCounter();
        JsonSerializer.Serialize(counter, results);
        var parsed = results.Select(r => Parse(r.StructuredContent)).ToArray();
        var items = new JsonArray();
        var envelope = new JsonObject { ["results"] = items, ["omittedResults"] = 0 };
        for (var i = 0; i < results.Count; i++)
        {
            var r = results[i]; var data = parsed[i];
            var dimension = r.Server == "mcp1" ? "3d" : r.Server == "mcp2" ? "2d" : null;
            if (!r.IsError && dimension != null && Enumerable.Range(i + 1, results.Count - i - 1)
                .Any(j => results[j].Server == "mcp3" && results[j].Tool == "compare-model-versions"
                    && !results[j].IsError && Complete(parsed[j], dimension))) continue;

            JsonObject projection;
            if (r.IsError || data?["status"]?.ToString() == "error")
            {
                projection = Pick(data, "status", "stage", "message", "causeStage");
                projection["status"] = "error";
            }
            else if (r.Server == "mcp3" && r.Tool == "compare-model-versions" && data != null)
                projection = Comparison(data);
            else if (r.Server is "mcp4" or "mcp5" or "mcp6" && data != null)
                projection = Pick(data, "runId", "stage", "status", "resultPath", "htmlPath", "error");
            else if (data?["snapshot"] is JsonObject snapshot)
                projection = Current(data, snapshot, question);
            else
            {
                // Unknown tool schemas must never forward arbitrary Text or JSON blobs.
                projection = Pick(data, "status", "processedElementCount", "jsonPath", "htmlPath", "filePath");
                projection["dataOmitted"] = true;
                projection["reason"] = "unsupported-result-schema";
            }
            var item = new JsonObject { ["server"] = r.Server, ["tool"] = r.Tool, ["isError"] = r.IsError, ["data"] = projection };
            items.Add(item);
            if (Bytes(envelope) <= MaxBytes) continue;
            items.RemoveAt(items.Count - 1);
            item["data"] = new JsonObject { ["status"] = r.IsError ? "error" : data?["status"]?.ToString(),
                ["dataOmitted"] = true, ["reason"] = "context-byte-budget-exceeded" };
            items.Add(item);
            if (Bytes(envelope) > MaxBytes) items.RemoveAt(items.Count - 1);
            envelope["omittedResults"] = envelope["omittedResults"]!.GetValue<int>() + 1;
        }
        var text = envelope.ToJsonString(Json);
        // Reserve enough space even for the omission counter growing in pathological plans.
        while (Encoding.UTF8.GetByteCount(text) > MaxBytes && items.Count > 0)
        {
            items.RemoveAt(items.Count - 1);
            envelope["omittedResults"] = envelope["omittedResults"]!.GetValue<int>() + 1;
            text = envelope.ToJsonString(Json);
        }
        return new(text, counter.Count, Encoding.UTF8.GetByteCount(text));
    }

    private static JsonObject? Parse(string? json)
    {
        try { return json == null ? null : JsonNode.Parse(json) as JsonObject; }
        catch (JsonException) { return null; }
    }
    private static bool Complete(JsonObject? data, string dimension) => data?["status"]?.ToString() == "complete"
        && (data["sections"]?[dimension]?["status"]?.ToString() == "complete"
            || data["comparisons"] is JsonObject comparisons && comparisons[dimension] is JsonObject part && Complete(part, dimension));

    private static JsonObject Comparison(JsonObject data)
    {
        var result = Pick(data, "status", "version1", "version2", "resolvedVersions", "counts", "limitations");
        if (data["comparisons"] is JsonObject comparisons)
        {
            var parts = new JsonObject();
            foreach (var dimension in new[] { "3d", "2d" })
                if (comparisons[dimension] is JsonObject part) parts[dimension] = Comparison(part);
            result["comparisons"] = parts;
        }
        if (data["sections"] is JsonObject sections)
        {
            var projected = new JsonObject();
            foreach (var dimension in new[] { "3d", "2d" })
            {
                if (sections[dimension] is not JsonObject section) continue;
                var output = Pick(section, "status", "counts");
                foreach (var group in new[] { "changed", "added", "removed" })
                {
                    var elements = new JsonArray();
                    foreach (var element in Rows(section[group]))
                    {
                        var row = Pick(element, "elementId", "elementLabel");
                        if (group == "changed")
                        {
                            if (element["semanticChanges"] is JsonArray changes)
                                row["semanticChanges"] = new JsonArray(changes.OfType<JsonObject>()
                                    .Select(c => (JsonNode)Pick(c, "name", "old", "new", "exactValues")).ToArray());
                            else row["dataOmitted"] = true;
                        }
                        elements.Add(row);
                    }
                    output[group] = elements;
                }
                projected[dimension] = output;
            }
            result["sections"] = projected;
        }
        return result;
    }

    private static JsonObject Current(JsonObject data, JsonObject snapshot, string question)
    {
        var result = Pick(data, "status", "jsonPath", "htmlPath", "processedElementCount");
        result["snapshotStatus"] = snapshot["status"]?.DeepClone();
        result["coverage"] = snapshot["coverage"]?.DeepClone();
        var ids = Regex.Matches(question, @"(?:\bElementId|\bID|элемент(?:а|ов)?)[\s:#№]*(\d+(?:\s*[,;]\s*\d+)*)", RegexOptions.IgnoreCase)
            .SelectMany(m => Regex.Matches(m.Groups[1].Value, @"\d+").Select(n => n.Value)).ToHashSet();
        var requestedNames = Rows(snapshot["parameters"]).Concat(Rows(snapshot["elements"]))
            .SelectMany(Parameters).Select(p => p["name"]?.ToString()).OfType<string>()
            .Where(n => n.Length >= 3 && question.Contains(n, StringComparison.OrdinalIgnoreCase)).ToHashSet(StringComparer.Ordinal);
        var parameterRows = Rows(snapshot["parameters"]).GroupBy(Id).ToDictionary(g => g.Key, g => g.First());
        var groups = new JsonObject();
        foreach (var group in new[] { "sheets", "views", "placements", "elements" })
        {
            if (snapshot[group] is not JsonArray source) continue;
            var all = source.OfType<JsonObject>().ToArray();
            var selected = all.Where(e => (ids.Count == 0 || ids.Contains(Id(e))) &&
                (requestedNames.Count == 0 || Parameters(parameterRows.GetValueOrDefault(Id(e)) ?? e)
                    .Any(p => requestedNames.Contains(p["name"]?.ToString() ?? "")))).ToArray();
            var rows = new JsonArray();
            foreach (var element in selected.Take(12))
            {
                var row = Pick(element, "elementId", "name", "category", "familyName", "typeName", "typeId", "ownerViewId", "kind");
                var parameterOwner = parameterRows.GetValueOrDefault(Id(element)) ?? element;
                var parameters = Parameters(parameterOwner).ToArray();
                var matching = parameters.Where(p => requestedNames.Count == 0 || requestedNames.Contains(p["name"]?.ToString() ?? "")).ToArray();
                var values = new JsonArray();
                foreach (var p in matching.Take(12))
                {
                    var value = p["displayValue"];
                    if (value == null || string.IsNullOrWhiteSpace(value.ToString())) value = p["convertedValue"] ?? p["rawValue"];
                    var v = Pick(p, "name", "source", "parameterId", "ownerElementId", "status");
                    v["value"] = value?.DeepClone();
                    if (ReferenceEquals(value, p["convertedValue"]) && value != null) v["unit"] = p["unitTypeId"]?.DeepClone();
                    if (Bytes(v) <= 2048) values.Add(v);
                }
                row["parameters"] = values;
                row["parameterCount"] = parameters.Length;
                row["matchedParameterCount"] = matching.Length;
                row["omittedParameters"] = parameters.Length - values.Count;
                if (element["properties"] is JsonObject properties)
                {
                    var projectedProperties = new JsonObject();
                    foreach (var property in properties.Take(12))
                    {
                        var v = property.Value is JsonObject obj ? Pick(obj, "status", "value", "unit", "coordinateSystem") : null;
                        if (v != null && Bytes(v) <= 2048) projectedProperties[property.Key] = v;
                    }
                    row["properties"] = projectedProperties;
                    row["omittedProperties"] = properties.Count - projectedProperties.Count;
                }
                rows.Add(row);
            }
            groups[group] = new JsonObject { ["totalCount"] = all.Length, ["matchedCount"] = selected.Length,
                ["omittedElements"] = all.Length - rows.Count, ["items"] = rows };
        }
        result["selection"] = ids.Count > 0 ? "explicit-element-ids" : "bounded-overview";
        result["requestedElementIds"] = new JsonArray(ids.Take(50).Select(x => (JsonNode)JsonValue.Create(x)!).ToArray());
        result["groups"] = groups;
        result["relationsTotalCount"] = (snapshot["relations"] as JsonArray)?.Count;
        result["relationsOmitted"] = (snapshot["relations"] as JsonArray)?.Count ?? 0;
        result["errorCount"] = (snapshot["errors"] as JsonArray)?.Count ?? 0;
        result["warningCount"] = (snapshot["warnings"] as JsonArray)?.Count ?? 0;
        result["unprocessedElementCount"] = (snapshot["unprocessedElementIds"] as JsonArray)?.Count ?? 0;
        result["detailScope"] = "Selected saved fields only; omitted data is unknown, not absent. Narrow by ElementId or exact parameter name.";
        return result;
    }

    private static string Id(JsonObject row) => (row["elementId"] ?? row["ElementId"])?.ToString() ?? "";
    private static IEnumerable<JsonObject> Rows(JsonNode? node) => node is JsonArray a ? a.OfType<JsonObject>() : [];
    private static IEnumerable<JsonObject> Parameters(JsonObject row) => new[] { "instanceParameters", "typeParameters", "InstanceParameters", "TypeParameters" }.SelectMany(k => Rows(row[k]));
    private static JsonObject Pick(JsonObject? source, params string[] fields)
    {
        var result = new JsonObject();
        if (source != null) foreach (var field in fields) if (source.ContainsKey(field)) result[field] = source[field]?.DeepClone();
        return result;
    }
    private static int Bytes(JsonNode node) => Encoding.UTF8.GetByteCount(node.ToJsonString(Json));
    private sealed class ByteCounter : Stream
    {
        public long Count { get; private set; }
        public override void Write(byte[] buffer, int offset, int count) => Count += count;
        public override void Write(ReadOnlySpan<byte> buffer) => Count += buffer.Length;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => Count;
        public override long Position { get => Count; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
