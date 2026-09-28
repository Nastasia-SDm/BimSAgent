using System;
using System.Collections.Generic;
using System.Linq;

namespace BimS.Revit2024
{
    // Data query, deliberately independent of MCP tool names.
    internal sealed class ViewGraphRequest
    {
        internal string Scope;
        internal string DocumentSession;
        internal string[] Fields;
        internal long[] SheetIds = new long[0], ViewIds = new long[0];
        internal bool IncludeTemplates, IncludeAnnotations;
        internal static ViewGraphRequest Parse(Dictionary<string, object> input)
        {
            var allowed = new[] { "collection", "fields", "scope", "documentSession", "sheetIds", "viewIds", "includeTemplates", "includeAnnotations" };
            if (input.Keys.Any(k => !allowed.Contains(k))) throw new ArgumentException("Unknown field");
            var result = new ViewGraphRequest();
            if (!input.TryGetValue("fields", out var fields) || !(fields is object[] names) || names.Length == 0 ||
                names.Any(n => !(n is string s) || !new[] { "Sheets", "Views", "Placements", "Elements", "Relations" }.Contains(s)))
                throw new ArgumentException("Invalid fields");
            result.Fields = names.Cast<string>().Distinct().ToArray();
            if (!input.TryGetValue("scope", out var scope) || !(scope is string text) || !new[] { "document", "sheets", "views" }.Contains(text))
                throw new ArgumentException("Invalid scope");
            result.Scope = text;
            if (input.TryGetValue("documentSession", out var session))
            {
                if (!(session is string id) || !Guid.TryParseExact(id, "N", out _)) throw new ArgumentException("Invalid session");
                result.DocumentSession = id;
            }
            if (input.TryGetValue("includeTemplates", out var templates))
                result.IncludeTemplates = templates is bool b ? b : throw new ArgumentException("Invalid includeTemplates");
            if (input.TryGetValue("includeAnnotations", out var annotations))
                result.IncludeAnnotations = annotations is bool b ? b : throw new ArgumentException("Invalid includeAnnotations");
            if (input.TryGetValue("sheetIds", out var sheets)) result.SheetIds = Ids(sheets);
            if (input.TryGetValue("viewIds", out var views)) result.ViewIds = Ids(views);
            if ((text == "document" && (input.ContainsKey("sheetIds") || input.ContainsKey("viewIds"))) ||
                (text == "sheets" && (result.SheetIds.Length == 0 || input.ContainsKey("viewIds"))) ||
                (text == "views" && (result.ViewIds.Length == 0 || input.ContainsKey("sheetIds")))) throw new ArgumentException("Invalid filters");
            return result;
        }
        private static long[] Ids(object value)
        {
            if (!(value is object[] values) || values.Length == 0 || values.Any(v => !(v is int || v is long) || Convert.ToInt64(v) <= 0))
                throw new ArgumentException("Invalid IDs");
            return values.Select(Convert.ToInt64).Distinct().ToArray();
        }
    }
}
