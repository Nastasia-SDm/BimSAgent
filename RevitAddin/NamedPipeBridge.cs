using System;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace BimS.Revit2024
{
    // UTF-8 lines: one request and one response per connection. No Revit API calls.
    internal sealed class NamedPipeBridge : IDisposable
    {
        private readonly CancellationTokenSource shutdown = new CancellationTokenSource();
        private readonly Task listener;
        private readonly PipeSecurity security;
        private readonly Func<string, CancellationToken, Task<string>> request;

        public NamedPipeBridge(Func<string, CancellationToken, Task<string>> request)
        {
            this.request = request;
            security = new PipeSecurity();
            security.SetAccessRuleProtection(true, false);
            security.AddAccessRule(new PipeAccessRule(
                new SecurityIdentifier(WellKnownSidType.NetworkSid, null),
                PipeAccessRights.FullControl, AccessControlType.Deny));
            using (var identity = WindowsIdentity.GetCurrent())
                security.AddAccessRule(new PipeAccessRule(identity.User,
                    PipeAccessRights.FullControl, AccessControlType.Allow));

            // Create synchronously so failure to bind is reported during add-in startup.
            var pipe = new NamedPipeServerStream("BIMS_REVIT_BRIDGE", PipeDirection.InOut, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 4096, 4096, security);
            listener = Task.Run(() => ListenAsync(pipe));
        }

        private NamedPipeServerStream CreatePipe() => new NamedPipeServerStream("BIMS_REVIT_BRIDGE",
            PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 4096, 4096, security);

        internal static void Diagnostic(string stage, Exception error = null)
        {
            try
            {
                var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BIM-S");
                Directory.CreateDirectory(folder);
                var line = DateTime.UtcNow.ToString("O") + " pid=" + System.Diagnostics.Process.GetCurrentProcess().Id + " " + stage;
                // Only types, HRESULT and code locations: no Message, Data, request or response values.
                for (var ex = error; ex != null; ex = ex.InnerException)
                    line += "\n" + ex.GetType().FullName + " HRESULT=" + ex.HResult + "\n" + ex.StackTrace;
                lock (typeof(NamedPipeBridge)) File.AppendAllText(Path.Combine(folder, "bridge.log"), line + "\n", Encoding.UTF8);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private async Task ListenAsync(NamedPipeServerStream pipe)
        {
            var assembly = typeof(NamedPipeBridge).Assembly;
            Diagnostic("listener.start assembly=" + assembly.Location + " mvid=" + assembly.ManifestModule.ModuleVersionId);
            try
            {
                while (!shutdown.IsCancellationRequested)
                {
                    var stage = "create";
                    var id = Guid.NewGuid().ToString("N");
                    try
                    {
                        if (pipe == null) pipe = CreatePipe();
                        var current = pipe;
                        using (current)
                        using (shutdown.Token.Register(() => current.Dispose()))
                        {
                            stage = "wait";
                            Diagnostic(id + " " + stage);
                            await current.WaitForConnectionAsync(shutdown.Token).ConfigureAwait(false);
                            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token))
                            using (deadline.Token.Register(() => current.Dispose()))
                            {
                                deadline.CancelAfter(TimeSpan.FromSeconds(75));
                                using (var reader = new StreamReader(current, new UTF8Encoding(false), false, 1024, true))
                                using (var writer = new StreamWriter(current, new UTF8Encoding(false), 1024, true) { AutoFlush = true })
                                {
                                    stage = "read";
                                    Diagnostic(id + " " + stage);
                                    var command = new StringBuilder();
                                    var character = new char[1];
                                    while (command.Length < 65536 && await reader.ReadAsync(character, 0, 1).ConfigureAwait(false) != 0)
                                    {
                                        if (character[0] == '\n') break;
                                        command.Append(character[0]);
                                    }
                                    string response;
                                    stage = "request";
                                    Diagnostic(id + " " + stage);
                                    try
                                    {
                                        response = command.ToString().TrimEnd('\r') == "ping" ? "Revit доступен"
                                            : command.Length >= 65536 ? "{\"error\":\"Request too large\"}"
                                            : await request(command.ToString(), deadline.Token).ConfigureAwait(false);
                                    }
                                    catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { throw; }
                                    catch (Exception ex) when (!(ex is OutOfMemoryException) && !(ex is AccessViolationException))
                                    {
                                        Diagnostic(id + " request.failed", ex);
                                        response = new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(new
                                        { error = "Ошибка обработки запроса Revit-мостом.", stage, exceptionType = ex.GetType().Name, diagnosticId = id });
                                    }
                                    stage = "write";
                                    Diagnostic(id + " " + stage);
                                    await writer.WriteLineAsync(response).ConfigureAwait(false);
                                    stage = "flush";
                                    await writer.FlushAsync().ConfigureAwait(false);
                                    stage = "dispose-streams";
                                }
                            }
                        }
                        Diagnostic(id + " connection.complete");
                    }
                    catch (Exception ex) when (!(ex is OutOfMemoryException) && !(ex is AccessViolationException))
                    {
                        Diagnostic(id + " failed stage=" + stage + " shutdown=" + shutdown.IsCancellationRequested, ex);
                        if (shutdown.IsCancellationRequested) break;
                        // The peer may already be gone: log instead of attempting another write to a broken pipe.
                        await Task.Delay(100, shutdown.Token).ConfigureAwait(false);
                    }
                    finally
                    {
                        pipe = null; // Never reuse a disconnected or broken native pipe handle.
                    }
                }
            }
            catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
            catch (Exception ex) { Diagnostic("listener.faulted", ex); throw; }
            finally { pipe?.Dispose(); Diagnostic("listener.exit shutdown=" + shutdown.IsCancellationRequested); }
        }
        public void Dispose()
        {
            Diagnostic("bridge.Dispose");
            shutdown.Cancel();
            // No UI-thread callbacks are used by the worker, so shutdown cannot wait on Revit.
            try { listener.GetAwaiter().GetResult(); }
            finally { shutdown.Dispose(); }
        }
    }
}
