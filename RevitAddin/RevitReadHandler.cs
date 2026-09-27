using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace BimS.Revit2024
{
    internal sealed class RevitReadHandler : IExternalEventHandler, IDisposable
    {
        private readonly object gate = new object();
        private readonly ExternalEvent externalEvent;
        private TaskCompletionSource<string> pending;
        private string[] fields;
        private string scope;
        private string collection;
        private string documentSession;
        private bool includeDocumentSession;
        private long[] elementIds;
        private readonly List<KeyValuePair<Document, SessionIdentity>> documentSessions =
            new List<KeyValuePair<Document, SessionIdentity>>();
        private sealed class SessionIdentity { public readonly string Id = Guid.NewGuid().ToString("N"); }

        // Called only from Execute: wrappers can differ for the same open Revit document.
        private string GetDocumentSession(Document document)
        {
            documentSessions.RemoveAll(entry => !entry.Key.IsValidObject);
            foreach (var entry in documentSessions)
                if (entry.Key.Equals(document)) return entry.Value.Id;
            var identity = new SessionIdentity();
            documentSessions.Add(new KeyValuePair<Document, SessionIdentity>(document, identity));
            return identity.Id;
        }
        private BuiltInCategory[] categories;
        private Dictionary<BuiltInCategory, PropertyRequest[]> propertyRequests;

        public sealed class PropertyRequest
        {
            public string key { get; set; }
            public string label { get; set; }
            public string method { get; set; }
            public string identifier { get; set; }
            public string source { get; set; }
            public string classFilter { get; set; }
            public string fallback { get; set; }
            public string note { get; set; }
        }

        private static Dictionary<BuiltInCategory, PropertyRequest[]> ParseProperties(object value)
        {
            var input = value as Dictionary<string, object>;
            if (input == null) throw new ArgumentException();
            var result = new Dictionary<BuiltInCategory, PropertyRequest[]>();
            var categoryNames = new HashSet<string>(Enum.GetNames(typeof(BuiltInCategory)), StringComparer.Ordinal);
            var parameterNames = new HashSet<string>(Enum.GetNames(typeof(BuiltInParameter)), StringComparer.Ordinal);
            foreach (var pair in input)
            {
                if (!categoryNames.Contains(pair.Key) || pair.Key == "INVALID" || !(pair.Value is object[] items)) throw new ArgumentException();
                var requests = new List<PropertyRequest>();
                foreach (var item in items)
                {
                    var map = item as Dictionary<string, object>;
                    var allowed = new[] { "key", "label", "method", "identifier", "source", "classFilter", "fallback", "note" };
                    if (map == null || map.Keys.Any(k => !allowed.Contains(k)) || map.Values.Any(v => v != null && !(v is string))) throw new ArgumentException();
                    var r = Serializer().ConvertToType<PropertyRequest>(map);
                    if (string.IsNullOrWhiteSpace(r.key) || string.IsNullOrWhiteSpace(r.label) ||
                        !new[] { "parameter", "api", "unsupported" }.Contains(r.method) ||
                        !new[] { "instance", "type", "instanceThenType" }.Contains(r.source) ||
                        (r.classFilter != null && !new[] { "Floor", "notFloor", "Rebar" }.Contains(r.classFilter)) ||
                        (r.fallback != null && !new[] { "Rebar.TotalLength", "Rebar.Volume" }.Contains(r.fallback))) throw new ArgumentException();
                    if (r.method == "parameter" && (r.identifier == null || !parameterNames.Contains(r.identifier) || r.identifier == "INVALID")) throw new ArgumentException();
                    if (r.method == "api" && (!new[] { "Element.LevelId", "Rebar.TotalLength", "Rebar.Volume" }.Contains(r.identifier) || r.source != "instance")) throw new ArgumentException();
                    if (r.method == "unsupported" && string.IsNullOrWhiteSpace(r.note)) throw new ArgumentException();
                    requests.Add(r);
                }
                result.Add((BuiltInCategory)Enum.Parse(typeof(BuiltInCategory), pair.Key), requests.ToArray());
            }
            return result;
        }

        private static object ReadProperty(Document document, Element element, ElementType type, PropertyRequest request)
        {
            var result = new Dictionary<string, object>
            {
                ["key"] = request.key, ["label"] = request.label, ["value"] = null, ["unit"] = null,
                ["source"] = request.source, ["identifier"] = request.identifier,
                ["status"] = "missing", ["note"] = request.note
            };
            try
            {
                if (request.method == "unsupported") { result["status"] = "unsupported"; return result; }
                if ((request.classFilter == "Floor" && !(element is Floor)) ||
                    (request.classFilter == "notFloor" && element is Floor) ||
                    (request.classFilter == "Rebar" && !(element is Autodesk.Revit.DB.Structure.Rebar)))
                { result["status"] = "notApplicable"; return result; }
                Action<double, ForgeTypeId> number = (value, spec) =>
                {
                    ForgeTypeId unit;
                    string label;
                    if (UnitUtils.IsValidUnit(spec, UnitTypeId.Millimeters)) { unit = UnitTypeId.Millimeters; label = "мм"; }
                    else if (UnitUtils.IsValidUnit(spec, UnitTypeId.SquareMeters)) { unit = UnitTypeId.SquareMeters; label = "м²"; }
                    else if (UnitUtils.IsValidUnit(spec, UnitTypeId.CubicMeters)) { unit = UnitTypeId.CubicMeters; label = "м³"; }
                    else { result["status"] = "notApplicable"; return; }
                    var converted = UnitUtils.ConvertFromInternalUnits(value, unit);
                    if (double.IsNaN(converted) || double.IsInfinity(converted)) throw new InvalidOperationException();
                    result["value"] = converted; result["unit"] = label; result["status"] = "ok";
                };
                Action<string> api = operation =>
                {
                    result["identifier"] = operation; result["source"] = "instance";
                    if (operation == "Element.LevelId")
                    {
                        var level = document.GetElement(element.LevelId) as Level;
                        if (level != null && !string.IsNullOrWhiteSpace(level.Name))
                        { result["value"] = level.Name; result["relatedElementId"] = level.Id.Value; result["status"] = "ok"; }
                    }
                    else if (element is Autodesk.Revit.DB.Structure.Rebar rebar)
                    {
                        if (operation == "Rebar.TotalLength") number(rebar.TotalLength, SpecTypeId.Length);
                        else if (operation == "Rebar.Volume") number(rebar.Volume, SpecTypeId.Volume);
                    }
                    else result["status"] = "notApplicable";
                };
                if (request.method == "api") { api(request.identifier); return result; }
                var id = (BuiltInParameter)Enum.Parse(typeof(BuiltInParameter), request.identifier);
                var parameter = request.source == "type" ? type?.get_Parameter(id) : element.get_Parameter(id);
                result["source"] = request.source == "type" ? "type" : "instance";
                if ((parameter == null || !parameter.HasValue) && request.source == "instanceThenType")
                { parameter = type?.get_Parameter(id); result["source"] = "type"; }
                if (parameter == null || !parameter.HasValue)
                {
                    if (parameter == null && request.fallback != null) api(request.fallback);
                    return result;
                }
                if (parameter.StorageType == StorageType.Double) number(parameter.AsDouble(), parameter.Definition.GetDataType());
                else if (parameter.StorageType == StorageType.String)
                {
                    var text = parameter.AsString();
                    if (!string.IsNullOrWhiteSpace(text)) { result["value"] = text; result["status"] = "ok"; }
                }
                else if (parameter.StorageType == StorageType.ElementId)
                {
                    var referenced = document.GetElement(parameter.AsElementId());
                    if (referenced != null && !string.IsNullOrWhiteSpace(referenced.Name))
                    { result["value"] = referenced.Name; result["relatedElementId"] = referenced.Id.Value; result["status"] = "ok"; }
                }
                else result["status"] = "notApplicable";
            }
            catch (Exception) { result["value"] = null; result["unit"] = null; result["status"] = "error"; }
            return result;
        }

        // Constructed in OnStartup, within a valid Revit API context.
        public RevitReadHandler() { externalEvent = ExternalEvent.Create(this); }
        private static JavaScriptSerializer Serializer() => new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        private static string Error(string message) => Serializer().Serialize(new { error = message });

        public async Task<string> RequestAsync(string json, CancellationToken cancellationToken)
        {
            NamedPipeBridge.Diagnostic("request.parse.begin");
            string[] requested;
            string requestedScope;
            string requestedCollection;
            string requestedSession = null;
            bool requestedEnvelope = false;
            long[] requestedIds = null;
            BuiltInCategory[] requestedCategories = null;
            var requestedProperties = new Dictionary<BuiltInCategory, PropertyRequest[]>();
            try
            {
                var input = Serializer().DeserializeObject(json) as Dictionary<string, object>;
                if (input == null ||input.Keys.Any(k => k != "collection" && k != "fields" && k != "scope" && k != "categories" && k != "propertyRequests" && k != "documentSession" && k != "includeDocumentSession" && k != "elementIds") || !input.ContainsKey("collection") ||
                    !(Equals(input["collection"], "elements") || Equals(input["collection"], "document") || Equals(input["collection"], "elementParameters")) || !input.ContainsKey("fields") ||
                    !(input["fields"] is object[] values) || values.Length == 0 ||
                    values.Any(v => !(v is string)))
                    return Error("Ожидаются collection: elements и непустой массив fields.");
                if (!input.ContainsKey("scope") || !(Equals(input["scope"], "activeView") || Equals(input["scope"], "document")))
                    return Error("scope должен быть activeView или document.");
                requestedCollection = (string)input["collection"];
                if (input.TryGetValue("documentSession", out var session))
                {
                    if (!(session is string id) || !Guid.TryParseExact(id, "N", out _)) return Error("Некорректный documentSession.");
                    requestedSession = id;
                }
                requestedScope = (string)input["scope"];
                if (input.TryGetValue("includeDocumentSession", out var envelope))
                {
                    if (!(envelope is bool flag) || requestedCollection != "elements")
                        return Error("includeDocumentSession допустим только как bool для elements.");
                    requestedEnvelope = flag;
                }
                if (requestedCollection == "elementParameters")
                {
                    if (requestedScope != "document" || requestedSession == null ||
                        input.ContainsKey("categories") || input.ContainsKey("propertyRequests") ||
                        !input.TryGetValue("elementIds", out var ids) || !(ids is object[] idValues) ||
                        idValues.Length == 0 || idValues.Length > 100 ||
                        idValues.Any(v => !(v is int || v is long) || Convert.ToInt64(v) <= 0))
                        return Error("elementParameters требует documentSession, scope: document и 1–100 положительных целых elementIds.");
                    requestedIds = idValues.Select(Convert.ToInt64).Distinct().ToArray();
                }
                else if (input.ContainsKey("elementIds")) return Error("elementIds допустим только для elementParameters.");
                if (input.TryGetValue("categories", out var categoryInput))
                {
                    if (!(categoryInput is object[] categoryValues) || categoryValues.Length == 0)
                        return Error("categories должен быть непустым массивом имён BuiltInCategory.");
                    var names = new HashSet<string>(Enum.GetNames(typeof(BuiltInCategory)), StringComparer.Ordinal);
                    var parsed = new HashSet<BuiltInCategory>();
                    foreach (var value in categoryValues)
                    {
                        if (!(value is string name) || !names.Contains(name) || name == "INVALID")
                            return Error("categories содержит недопустимое имя BuiltInCategory.");
                        parsed.Add((BuiltInCategory)Enum.Parse(typeof(BuiltInCategory), name));
                    }
                    requestedCategories = parsed.ToArray();
                }
                requested = values.Cast<string>().Distinct(StringComparer.Ordinal).ToArray();
                if (requestedCollection == "elementParameters" ? requested.Any(f => !new[] { "ElementId", "Category", "FamilyName", "TypeName", "TypeId", "InstanceParameters", "TypeParameters" }.Contains(f)) : requestedCollection == "document" ? requested.Any(f => f != "SessionId") : requested.Any(f => f != "ElementId" && f != "Category" && f != "Name" && f != "FamilyName" && f != "TypeName" && f != "SystemProperties"))
                    return Error("Поддерживаются только ElementId, Category, Name, FamilyName, TypeName, SystemProperties.");
                if (input.TryGetValue("propertyRequests", out var properties)) requestedProperties = ParseProperties(properties);
            }
            catch (Exception e) when (e is ArgumentException || e is InvalidOperationException)
            { NamedPipeBridge.Diagnostic("request.parse.failed", e); return Error("Некорректный JSON-запрос."); }
            NamedPipeBridge.Diagnostic("request.parse.complete");

            var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(60));
                using (timeout.Token.Register(() => completion.TrySetCanceled()))
                {
                    lock (gate)
                    {
                        if (pending != null) return Error("Мост занят предыдущим запросом.");
                        pending = completion;
                        fields = requested;
                        scope = requestedScope;
                        collection = requestedCollection;
                        documentSession = requestedSession;
                        includeDocumentSession = requestedEnvelope;
                        elementIds = requestedIds;
                        categories = requestedCategories;
                        propertyRequests = requestedProperties;
                        try
                        {
                            var status = externalEvent.Raise();
                            NamedPipeBridge.Diagnostic("externalEvent.raise=" + status);
                            if (status != ExternalEventRequest.Accepted && status != ExternalEventRequest.Pending)
                                completion.TrySetResult(Error("Revit не принял запрос."));
                        }
                        catch (Exception) { completion.TrySetResult(Error("Не удалось передать запрос Revit.")); }
                    }
                    try { return await completion.Task.ConfigureAwait(false); }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    { NamedPipeBridge.Diagnostic("externalEvent.timeout"); return Error("Истекло время ожидания Revit."); }
                    finally
                    {
                        lock (gate) { if (ReferenceEquals(pending, completion)) pending = null; }
                    }
                }
            }
        }

        public void Execute(UIApplication application)
        {
            NamedPipeBridge.Diagnostic("execute.enter");
            lock (gate)
            {
                if (pending == null || pending.Task.IsCompleted) return;
                try
                {
                    var document = application.ActiveUIDocument?.Document;
                    if (document == null) { pending.TrySetResult(Error("Нет открытого документа Revit.")); return; }
                    var sessionId = GetDocumentSession(document);
                    if (documentSession != null && documentSession != sessionId)
                    {
                        NamedPipeBridge.Diagnostic("execute.rejected.documentSession");
                        pending.TrySetResult(Error("Активен другой документ Revit.")); return;
                    }
                    if (collection == "document")
                    { pending.TrySetResult(Serializer().Serialize(new { SessionId = sessionId })); return; }
                    if (collection == "elementParameters")
                    {
                        var exported = ReadElementParameters(document);
                        pending.TrySetResult(Serializer().Serialize(exported));
                        NamedPipeBridge.Diagnostic("execute.complete");
                        return;
                    }
                    var rows = new List<Dictionary<string, object>>();
                    var activeView = scope == "activeView" ? application.ActiveUIDocument.ActiveView : null;
                    if (scope == "activeView" && (activeView == null || !FilteredElementCollector.IsViewValidForElementIteration(document, activeView.Id)))
                    {
                        pending.TrySetResult(Error("Активный вид не поддерживает сбор элементов."));
                        return;
                    }
                    using (var collector = scope == "document" ? new FilteredElementCollector(document) : new FilteredElementCollector(document, activeView.Id))
                    {
                        collector.WhereElementIsNotElementType();
                        if (categories != null)
                            collector.WhereElementIsViewIndependent()
                                .WherePasses(new ElementMulticategoryFilter(categories));
                        foreach (var element in collector)
                        {
                            if (pending.Task.IsCompleted) return;
                            var row = new Dictionary<string, object>();
                            ElementType elementType = null;
                            if (fields.Contains("FamilyName") || fields.Contains("TypeName") || fields.Contains("SystemProperties"))
                                elementType = document.GetElement(element.GetTypeId()) as ElementType;
                            foreach (var field in fields)
                            {
                                switch (field)
                                {
                                    case "SystemProperties":
                                        var requestedForElement = element.Category != null && propertyRequests.TryGetValue((BuiltInCategory)element.Category.Id.Value, out var descriptors)
                                            ? descriptors : new PropertyRequest[0];
                                        row[field] = requestedForElement.GroupBy(r => r.key).Select(group =>
                                        {
                                            var applicable = group.FirstOrDefault(r => r.classFilter == null ||
                                                (r.classFilter == "Floor" && element is Floor) ||
                                                (r.classFilter == "notFloor" && !(element is Floor)) ||
                                                (r.classFilter == "Rebar" && element is Autodesk.Revit.DB.Structure.Rebar));
                                            return ReadProperty(document, element, elementType, applicable ?? group.First());
                                        }).ToArray();
                                        break;
                                    case "FamilyName":
                                        row[field] = elementType == null || string.IsNullOrWhiteSpace(elementType.FamilyName) ? null : elementType.FamilyName;
                                        break;
                                    case "TypeName":
                                        row[field] = elementType == null || string.IsNullOrWhiteSpace(elementType.Name) ? null : elementType.Name;
                                        break;
                                    case "ElementId": row[field] = element.Id.Value; break;
                                    case "Category": row[field] = element.Category?.Name; break;
                                    case "Name": row[field] = element.Name; break;
                                }
                            }
                            rows.Add(row);
                        }
                    }
                    pending.TrySetResult(Serializer().Serialize(includeDocumentSession ? (object)new { documentSession = sessionId, elements = rows } : rows));
                    NamedPipeBridge.Diagnostic("execute.complete");
                }
                catch (Exception ex)
                {
                    NamedPipeBridge.Diagnostic("execute.failed", ex);
                    pending.TrySetResult(Error("Ошибка Revit: " + ex.Message));
                }
            }
        }

        private static readonly Dictionary<long, string[]> BuiltInNames = Enum.GetNames(typeof(BuiltInParameter))
            .GroupBy(name => (long)(BuiltInParameter)Enum.Parse(typeof(BuiltInParameter), name))
            .Where(group => group.Key != (long)BuiltInParameter.INVALID)
            .ToDictionary(group => group.Key, group => group.OrderBy(name => name, StringComparer.Ordinal).ToArray());

        private static object ReadError(string operation, Exception exception) =>
            new { operation, message = "Ошибка Revit API: " + exception.GetType().Name };

        private void CheckReadCancellation()
        {
            if (pending.Task.IsCompleted) throw new OperationCanceledException();
        }

        private object[] ReadParameters(Element owner, string source, List<object> ownerErrors)
        {
            var parameters = new Dictionary<long, Parameter>();
            Action<Parameter> add = parameter =>
            {
                if (parameter?.Definition is InternalDefinition definition &&
                    definition.BuiltInParameter != BuiltInParameter.INVALID)
                    parameters[parameter.Id.Value] = parameter;
            };
            try
            {
                foreach (Parameter parameter in owner.Parameters)
                {
                    CheckReadCancellation();
                    try { add(parameter); }
                    catch (Exception ex) { ownerErrors.Add(ReadError("parameter.definition", ex)); }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { ownerErrors.Add(ReadError(source + ".Parameters", ex)); }

            foreach (var builtIn in BuiltInNames.Keys)
            {
                CheckReadCancellation();
                try { add(owner.get_Parameter((BuiltInParameter)builtIn)); }
                catch (Exception ex) { ownerErrors.Add(ReadError(source + ".get_Parameter:" + builtIn, ex)); }
            }

            var result = new List<object>();
            foreach (var pair in parameters.OrderBy(item => item.Key))
            {
                CheckReadCancellation();
                var parameter = pair.Value;
                var errors = new List<object>();
                var row = new Dictionary<string, object>
                {
                    ["parameterId"] = pair.Key,
                    ["builtInParameterNames"] = BuiltInNames.TryGetValue(pair.Key, out var aliases) ? aliases : new string[0],
                    ["name"] = null, ["source"] = source, ["ownerElementId"] = owner.Id.Value,
                    ["storageType"] = null, ["hasValue"] = null, ["isReadOnly"] = null,
                    ["dataTypeId"] = null, ["rawValue"] = null, ["displayValue"] = null,
                    ["unitTypeId"] = null, ["convertedValue"] = null, ["status"] = "ok", ["errors"] = errors
                };
                Action<string, Action> read = (operation, action) =>
                {
                    try { action(); }
                    catch (Exception ex) { errors.Add(ReadError(operation, ex)); }
                };
                read("name", () => row["name"] = parameter.Definition.Name);
                read("isReadOnly", () => row["isReadOnly"] = parameter.IsReadOnly);
                ForgeTypeId spec = null;
                read("dataTypeId", () => { spec = parameter.Definition.GetDataType(); row["dataTypeId"] = spec?.TypeId; });
                StorageType storage = StorageType.None;
                bool hasValue = false;
                bool valueRead = false;
                read("storageType", () => { storage = parameter.StorageType; row["storageType"] = storage.ToString(); });
                read("hasValue", () => { hasValue = parameter.HasValue; row["hasValue"] = hasValue; });
                if (hasValue && storage != StorageType.None)
                {
                    read("rawValue", () =>
                    {
                        switch (storage)
                        {
                            case StorageType.Double:
                                var number = parameter.AsDouble();
                                if (double.IsNaN(number) || double.IsInfinity(number)) throw new InvalidOperationException();
                                row["rawValue"] = number; break;
                            case StorageType.Integer: row["rawValue"] = parameter.AsInteger(); break;
                            case StorageType.String: row["rawValue"] = parameter.AsString(); break;
                            case StorageType.ElementId: row["rawValue"] = parameter.AsElementId().Value; break;
                        }
                        valueRead = true;
                    });
                    if (storage == StorageType.Double || storage == StorageType.Integer)
                        read("displayValue", () => row["displayValue"] = parameter.AsValueString());
                    else if (storage == StorageType.String) row["displayValue"] = row["rawValue"];
                    if (storage == StorageType.Double && spec != null)
                        read("units", () =>
                        {
                            if (!UnitUtils.IsMeasurableSpec(spec)) return;
                            var unit = parameter.GetUnitTypeId();
                            row["unitTypeId"] = unit.TypeId;
                            if (valueRead)
                            {
                                var converted = UnitUtils.ConvertFromInternalUnits((double)row["rawValue"], unit);
                                if (double.IsNaN(converted) || double.IsInfinity(converted)) throw new InvalidOperationException();
                                row["convertedValue"] = converted;
                            }
                        });
                }
                row["status"] = errors.Count != 0 ? (valueRead ? "partial" : "error") :
                    !hasValue || storage == StorageType.None ? "noValue" : "ok";
                result.Add(row);
            }
            return result.ToArray();
        }

        private object ReadElementParameters(Document document)
        {
            var rows = new List<object>();
            var typeCache = new Dictionary<long, KeyValuePair<object[], List<object>>>();
            foreach (var id in elementIds)
            {
                CheckReadCancellation();
                var errors = new List<object>();
                var row = new Dictionary<string, object> { ["ElementId"] = id, ["status"] = "ok", ["errors"] = errors };
                try
                {
                    var element = document.GetElement(new ElementId(id));
                    if (element == null) { row["status"] = "notFound"; rows.Add(row); continue; }
                    var type = document.GetElement(element.GetTypeId()) as ElementType;
                    foreach (var field in fields)
                    {
                        try
                        {
                            switch (field)
                            {
                                case "ElementId": break;
                                case "Category": row[field] = element.Category?.Name; break;
                                case "FamilyName": row[field] = type?.FamilyName; break;
                                case "TypeName": row[field] = type?.Name; break;
                                case "TypeId": row[field] = type == null ? (object)null : type.Id.Value; break;
                                case "InstanceParameters": row[field] = ReadParameters(element, "instance", errors); break;
                                case "TypeParameters":
                                    if (type == null) { row[field] = new object[0]; break; }
                                    if (!typeCache.TryGetValue(type.Id.Value, out var cached))
                                    {
                                        var typeErrors = new List<object>();
                                        cached = new KeyValuePair<object[], List<object>>(ReadParameters(type, "type", typeErrors), typeErrors);
                                        typeCache.Add(type.Id.Value, cached);
                                    }
                                    row[field] = cached.Key;
                                    errors.AddRange(cached.Value);
                                    break;
                            }
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex) { row[field] = null; errors.Add(ReadError(field, ex)); }
                    }
                    bool parameterErrors = row.Values.OfType<object[]>().SelectMany(value => value)
                        .OfType<Dictionary<string, object>>().Any(value => (string)value["status"] == "error" || (string)value["status"] == "partial");
                    if (errors.Count != 0 || parameterErrors) row["status"] = "partial";
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { errors.Add(ReadError("element", ex)); row["status"] = "error"; }
                rows.Add(row);
            }
            return rows;
        }
        public string GetName() => "BIM-S: чтение документа";
        public void Dispose()
        {
            lock (gate) { pending?.TrySetCanceled(); externalEvent.Dispose(); }
        }
    }
}
