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
        private bool instancesOnly;

        // Constructed in OnStartup, within a valid Revit API context.
        public RevitReadHandler() { externalEvent = ExternalEvent.Create(this); }
        private static JavaScriptSerializer Serializer() => new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        private static string Error(string message) => Serializer().Serialize(new { error = message });

        public async Task<string> RequestAsync(string json, CancellationToken cancellationToken)
        {
            string[] requested;
            bool instancesOnly = false;
            try
            {
                var input = Serializer().DeserializeObject(json) as Dictionary<string, object>;
                if (input == null ||input.Keys.Any(k => k != "collection" && k != "fields" && k != "instancesOnly") || !input.ContainsKey("collection") ||
                    !Equals(input["collection"], "elements") || !input.ContainsKey("fields") ||
                    !(input["fields"] is object[] values) || values.Length == 0 ||
                    values.Any(v => !(v is string)))
                    return Error("Ожидаются collection: elements и непустой массив fields.");
                if (input.ContainsKey("instancesOnly"))
                {
                    if (!(input["instancesOnly"] is bool value))
                        return Error("instancesOnly должен быть true или false.");

                    instancesOnly = value;
                }
                requested = values.Cast<string>().Distinct(StringComparer.Ordinal).ToArray();
                if (requested.Any(f => f != "ElementId" && f != "Category" && f != "Name"))
                    return Error("Поддерживаются только ElementId, Category, Name.");
            }
            catch (Exception e) when (e is ArgumentException || e is InvalidOperationException)
            { return Error("Некорректный JSON-запрос."); }

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
                        this.instancesOnly = instancesOnly;
                        try
                        {
                            var status = externalEvent.Raise();
                            if (status != ExternalEventRequest.Accepted && status != ExternalEventRequest.Pending)
                                completion.TrySetResult(Error("Revit не принял запрос."));
                        }
                        catch (Exception) { completion.TrySetResult(Error("Не удалось передать запрос Revit.")); }
                    }
                    try { return await completion.Task.ConfigureAwait(false); }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    { return Error("Истекло время ожидания Revit."); }
                    finally
                    {
                        lock (gate) { if (ReferenceEquals(pending, completion)) pending = null; }
                    }
                }
            }
        }

        public void Execute(UIApplication application)
        {
            lock (gate)
            {
                if (pending == null || pending.Task.IsCompleted) return;
                try
                {
                    var document = application.ActiveUIDocument?.Document;
                    if (document == null) { pending.TrySetResult(Error("Нет открытого документа Revit.")); return; }
                    var rows = new List<Dictionary<string, object>>();
                    using (var collector = new FilteredElementCollector(document))
                    {
                        if (instancesOnly)
                            collector.WhereElementIsNotElementType();
                        foreach (var element in collector)
                        {
                            if (pending.Task.IsCompleted) return;
                            var row = new Dictionary<string, object>();
                            foreach (var field in fields)
                            {
                                switch (field)
                                {
                                    case "ElementId": row[field] = element.Id.Value; break;
                                    case "Category": row[field] = element.Category?.Name; break;
                                    case "Name": row[field] = element.Name; break;
                                }
                            }
                            rows.Add(row);
                        }
                    }
                    pending.TrySetResult(Serializer().Serialize(rows));
                }
                catch (Exception) { pending.TrySetResult(Error("Не удалось прочитать элементы документа.")); }
            }
        }

        public string GetName() => "BIM-S: чтение документа";
        public void Dispose()
        {
            lock (gate) { pending?.TrySetCanceled(); externalEvent.Dispose(); }
        }
    }
}
