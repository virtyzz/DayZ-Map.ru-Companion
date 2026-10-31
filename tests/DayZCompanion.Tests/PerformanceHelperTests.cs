using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Principal;
using System.Windows.Forms;
using CrosshairMarker;

internal static class PerformanceHelperTests
{
    public static void ProtocolIsBounded()
    {
        using var buffer = new MemoryStream();
        PerformancePipe.WriteAsync(buffer, new PerformanceWorkerCommand(1, 2219), CancellationToken.None).GetAwaiter().GetResult();
        buffer.Position = 0;
        var message = PerformancePipe.ReadAsync<PerformanceWorkerCommand>(buffer, CancellationToken.None).GetAwaiter().GetResult();
        message.Validate();
        Check(message.QueryPort == 2219, "Query port was not preserved");
        foreach (var length in new[] { -1, 0, PerformancePipe.MaximumMessageSize + 1 })
        {
            var header = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(header, length);
            using var invalid = new MemoryStream(header);
            Expect<InvalidDataException>(() => PerformancePipe.ReadAsync<PerformanceWorkerCommand>(invalid, CancellationToken.None).GetAwaiter().GetResult());
        }
        Expect<InvalidDataException>(() => new PerformanceWorkerCommand(2, 0).Validate());
        Expect<InvalidDataException>(() => new PerformanceWorkerCommand(1, 65536).Validate());
        using var truncated = new MemoryStream(new byte[] { 20, 0, 0, 0, 1 });
        Expect<EndOfStreamException>(() => PerformancePipe.ReadAsync<PerformanceWorkerCommand>(truncated, CancellationToken.None).GetAwaiter().GetResult());
    }

    public static void PipeIdentifiesProcesses() => PipeIdentifiesProcessesAsync().GetAwaiter().GetResult();
    private static async Task PipeIdentifiesProcessesAsync()
    {
        var name = Guid.NewGuid().ToString("N");
        using var server = PerformancePipe.CreateServer(name);
        using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Anonymous);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var accept = server.WaitForConnectionAsync(deadline.Token);
        await client.ConnectAsync(deadline.Token); await accept;
        PerformancePipe.ValidateClient(server, Environment.ProcessId);
        PerformancePipe.ValidateServer(client, Environment.ProcessId);
        Expect<UnauthorizedAccessException>(() => PerformancePipe.ValidateClient(server, -1));
        Expect<UnauthorizedAccessException>(() => PerformancePipe.ValidateServer(client, -1));
        using var parent = Process.GetCurrentProcess();
        using var checkedParent = PerformancePipe.ValidateParent(parent.Id, parent.StartTime.ToUniversalTime().Ticks);
        Expect<UnauthorizedAccessException>(() => PerformancePipe.ValidateParent(parent.Id, 0));
        await PerformancePipe.WriteAsync(client, new PerformanceWorkerUpdate(1, new(Fps: 72, Ping: 60)), deadline.Token);
        var update = await PerformancePipe.ReadAsync<PerformanceWorkerUpdate>(server, deadline.Token);
        Check(update.Snapshot.Fps == 72 && update.Snapshot.Ping == 60, "Helper telemetry did not cross the pipe");
        client.Dispose();
        try { await PerformancePipe.ReadAsync<PerformanceWorkerUpdate>(server, deadline.Token); }
        catch (EndOfStreamException) { return; }
        throw new Exception("Helper did not detect the parent disconnect");
    }

    public static void CancelledElevationAllowsRetry()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var context = new ApplicationContext();
                using var sync = new WindowsFormsSynchronizationContext();
                SynchronizationContext.SetSynchronizationContext(sync);
                var launches = 0;
                using var helper = new PerformanceHelperClient(_ => { Interlocked.Increment(ref launches); throw new Win32Exception(1223); });
                var rejected = 0;
                helper.EnableRejected += _ => rejected++;
                var enabled = new PerformanceSettings { Enabled = true };
                helper.Apply(enabled);
                using var timer = new System.Windows.Forms.Timer { Interval = 30 };
                var phase = 0;
                var started = Stopwatch.StartNew();
                timer.Tick += (_, _) =>
                {
                    try
                    {
                        if (started.Elapsed.TotalSeconds > 5) throw new Exception("Elevation cancellation did not complete");
                        if (helper.Snapshot.HelperStarting) return;
                        Check(!helper.Snapshot.HelperCanStart && helper.Snapshot.HelperStatus.Contains("отменён"), "UAC cancellation leaves measurements enabled");
                        if (phase++ == 0) { helper.Apply(enabled); return; }
                        Check(launches == 2, "UAC was retried automatically or duplicate helpers started");
                        Check(rejected == 2, "The enabled checkbox was not reverted after denied permission");
                        helper.Apply(new PerformanceSettings());
                        Check(!helper.Snapshot.HelperCanStart, "Disabled measurements can still start a helper");
                        context.ExitThread();
                    }
                    catch (Exception ex) { failure = ex; context.ExitThread(); }
                };
                timer.Start(); Application.Run(context);
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure is not null) throw failure;
    }

    public static void TaskIdentityIsInstallationSpecific()
    {
        var current = Environment.ProcessPath!;
        Check(!PerformanceScheduledTask.IsProtectedInstall(current), "Test/development build was allowed persistent elevation");
        var first = PerformanceScheduledTask.TaskName(current, "S-1-5-21-100");
        Check(first == PerformanceScheduledTask.TaskName(current.ToUpperInvariant(), "S-1-5-21-100"), "Task identity depends on path casing");
        Check(first != PerformanceScheduledTask.TaskName(current, "S-1-5-21-101"), "Task identity is shared by different users");
        Check(first != PerformanceScheduledTask.TaskName(Path.Combine(Path.GetDirectoryName(current)!, "other.exe"), "S-1-5-21-100"), "Task identity is shared by different installations");
        const string user = "S-1-5-21-1-2-3-1001";
        Check(PerformanceScheduledTask.HasProtectedSecurity($"O:BAG:BAD:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;GRGX;;;{user})"), "Read/run access was rejected");
        Check(!PerformanceScheduledTask.HasProtectedSecurity($"O:{user}G:BAD:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;GRGX;;;{user})"), "A user-owned task can have its ACL changed by that user");
        Check(!PerformanceScheduledTask.HasProtectedSecurity($"O:BAG:BAD:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;GA;;;{user})"), "User task modifications were allowed");
        Check(!PerformanceScheduledTask.HasProtectedSecurity("O:BAG:BAD:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;GW;;;BU)"), "Users group can modify the task");
    }

    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Expect<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name);
    }
}
