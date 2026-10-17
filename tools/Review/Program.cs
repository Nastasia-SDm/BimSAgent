using BimSAgentApp.Review;
using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; shutdown.Cancel(); };
Environment.ExitCode = await ReviewCommand.RunAsync(args, shutdown.Token);
