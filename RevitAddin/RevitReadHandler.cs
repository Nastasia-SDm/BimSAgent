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
            BuiltInCategory[] requestedCategories = null;
            var requestedProperties = new Dictionary<BuiltInCategory, PropertyRequest[]>();
            try
            {
                var input = Serializer().DeserializeObject(json) as Dictionary<string, object>;
                if (input == null ||input.Keys.Any(k => k != "collection" && k != "fields" && k != "scope" && k != "categories" && k != "propertyRequests" && k != "documentSession") || !input.ContainsKey("collection") ||
                    !(Equals(input["collection"], "elements") || Equals(input["collection"], "document")) || !input.ContainsKey("fields") ||
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
                if (requestedCollection == "document" ? requested.Any(f => f != "SessionId") : requested.Any(f => f != "ElementId" && f != "Category" && f != "Name" && f != "FamilyName" && f != "TypeName" && f != "SystemProperties"))
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
                    pending.TrySetResult(Serializer().Serialize(rows));
                    NamedPipeBridge.Diagnostic("execute.complete");
                }
                catch (Exception ex)
                {
                    NamedPipeBridge.Diagnostic("execute.failed", ex);
                    pending.TrySetResult(Error("Ошибка Revit: " + ex.Message));
                }
            }
        }

        public string GetName() => "BIM-S: чтение документа";
        public void Dispose()
        {
            lock (gate) { pending?.TrySetCanceled(); externalEvent.Dispose(); }
        }
    }
}
