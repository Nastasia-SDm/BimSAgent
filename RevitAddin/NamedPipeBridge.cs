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
        private readonly Func<string, CancellationToken, Task<string>> request;

        public NamedPipeBridge(Func<string, CancellationToken, Task<string>> request)
        {
            this.request = request;
            var security = new PipeSecurity();
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

        private async Task ListenAsync(NamedPipeServerStream pipe)
        {
            using (pipe)
            using (shutdown.Token.Register(() => pipe.Dispose()))
            {
                while (!shutdown.IsCancellationRequested)
                {
                    try
                    {
                        await pipe.WaitForConnectionAsync(shutdown.Token).ConfigureAwait(false);
                        using (var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 1024, true))
                        using (var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, true) { AutoFlush = true })
                        {
                            var command = new StringBuilder();
                            var character = new char[1];
                            // Bounded request size; shutdown disposes the pipe to unblock reads.
                            while (command.Length < 65536 && await reader.ReadAsync(character, 0, 1).ConfigureAwait(false) != 0)
                            {
                                if (character[0] == '\n') break;
                                command.Append(character[0]);
                            }
                            var response = command.ToString().TrimEnd('\r') == "ping"
                                ? "Revit доступен" : command.Length >= 65536
                                    ? "{\"error\":\"Request too large\"}"
                                    : await request(command.ToString(), shutdown.Token).ConfigureAwait(false);
                            await writer.WriteLineAsync(response).ConfigureAwait(false);
                        }
                    }
                    catch (OperationCanceledException) { break; }
                    catch (ObjectDisposedException) { break; }
                    catch (IOException)
                    {
                        if (shutdown.IsCancellationRequested) break;
                        // A disconnected client must not stop the listener.
                    }
                    finally
                    {
                        if (!shutdown.IsCancellationRequested)
                        {
                            try { if (pipe.IsConnected) pipe.Disconnect(); }
                            catch (IOException) { }
                            catch (ObjectDisposedException) { }
                        }
                    }
                }
            }
        }

        public void Dispose()
        {
            shutdown.Cancel();
            // No UI-thread callbacks are used by the worker, so shutdown cannot wait on Revit.
            try { listener.GetAwaiter().GetResult(); }
            finally { shutdown.Dispose(); }
        }
    }
}
