using System.IO.Pipes;
using System.Security.Principal;

namespace CrosshairMarker;

internal static class PerformanceWorker
{
    public const string Argument = "--performance-worker";

    public static int Run(string[] args)
    {
        if (args.Length != 3 || !Guid.TryParseExact(args[0], "N", out _)
            || !int.TryParse(args[1], out var parentPid) || !long.TryParse(args[2], out var startTicks)) return 2;
        var result = 0;
        var thread = new Thread(() =>
        {
            try { RunCore(args[0], parentPid, startTicks); }
            catch (Exception ex) { AppRuntimeLog.Error("Performance helper stopped", ex); result = 1; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return result;
    }

    private static void RunCore(string pipeName, int parentPid, long startTicks)
    {
        if (!PerformancePipe.IsElevated()) throw new UnauthorizedAccessException("Помощнику нужны повышенные права.");
        using var parent = PerformancePipe.ValidateParent(parentPid, startTicks);
        using var lifetime = new CancellationTokenSource();
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous, TokenImpersonationLevel.Anonymous);
        pipe.ConnectAsync(10000, lifetime.Token).GetAwaiter().GetResult();
        PerformancePipe.ValidateServer(pipe, parentPid);
        using var startup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var initial = PerformancePipe.ReadAsync<PerformanceWorkerCommand>(pipe, startup.Token).GetAwaiter().GetResult();
        initial.Validate();

        ApplicationConfiguration.Initialize();
        using var context = new ApplicationContext();
        using var sync = new WindowsFormsSynchronizationContext();
        SynchronizationContext.SetSynchronizationContext(sync);
        using var collector = new PerformanceMonitor(parent.SessionId);
        var writing = 0;
        var writeTask = Task.CompletedTask;
        collector.Changed += snapshot =>
        {
            if (lifetime.IsCancellationRequested || Interlocked.CompareExchange(ref writing, 1, 0) != 0) return;
            writeTask = Task.Run(async () =>
            {
                try
                {
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                    deadline.CancelAfter(TimeSpan.FromSeconds(5));
                    await PerformancePipe.WriteAsync(pipe, new PerformanceWorkerUpdate(1, snapshot), deadline.Token);
                }
                catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
                {
                    lifetime.Cancel();
                }
                finally { Interlocked.Exchange(ref writing, 0); }
            });
        };
        var commands = Task.Run(async () =>
        {
            try
            {
                while (!lifetime.IsCancellationRequested)
                {
                    var command = await PerformancePipe.ReadAsync<PerformanceWorkerCommand>(pipe, lifetime.Token);
                    command.Validate();
                    sync.Post(_ =>
                    {
                        if (!lifetime.IsCancellationRequested) collector.Apply(new PerformanceSettings { Enabled = true, QueryPort = command.QueryPort });
                    }, null);
                }
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or System.Text.Json.JsonException)
            {
                if (!lifetime.IsCancellationRequested && ex is not EndOfStreamException) AppRuntimeLog.Error("Performance helper pipe closed", ex);
            }
            finally
            {
                lifetime.Cancel();
                sync.Post(async _ =>
                {
                    try { await collector.StopAsync(); }
                    catch (Exception ex) { AppRuntimeLog.Error("Performance helper cleanup failed", ex); }
                    finally { context.ExitThread(); }
                }, null);
            }
        });
        var parentWatch = Task.Run(async () =>
        {
            try { await parent.WaitForExitAsync(lifetime.Token); lifetime.Cancel(); }
            catch (OperationCanceledException) { }
        });
        collector.Apply(new PerformanceSettings { Enabled = true, QueryPort = initial.QueryPort });
        Application.Run(context);
        lifetime.Cancel();
        commands.GetAwaiter().GetResult();
        parentWatch.GetAwaiter().GetResult();
        writeTask.GetAwaiter().GetResult();
    }
}
