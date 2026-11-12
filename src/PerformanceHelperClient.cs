using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;

namespace CrosshairMarker;

internal sealed class PerformanceHelperClient : IDisposable
{
    private sealed class Session : IDisposable
    {
        public readonly string Name = Guid.NewGuid().ToString("N");
        public readonly CancellationTokenSource Lifetime = new();
        public readonly SemaphoreSlim Writes = new(1);
        public NamedPipeServerStream? Pipe;
        public Process? Worker;
        public bool Connected;
        public void Dispose()
        {
            Lifetime.Cancel();
            Pipe?.Dispose();
            Worker?.Dispose();
        }
    }

    private readonly SynchronizationContext sync;
    private readonly Func<ProcessStartInfo, Process> launch;
    private readonly System.Windows.Forms.Timer freshness = new() { Interval = 1000 };
    private PerformanceSettings settings = new();
    private Session? session;
    private bool disposed;
    private long lastUpdate;
    public PerformanceSnapshot Snapshot { get; private set; } = Disabled();
    public event Action<PerformanceSnapshot>? Changed;
    public event Action<string>? EnableRejected;
    internal int? WorkerProcessId => session?.Worker?.Id;

    public PerformanceHelperClient(Func<ProcessStartInfo, Process>? launch = null)
    {
        sync = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        if (SynchronizationContext.Current is null) SynchronizationContext.SetSynchronizationContext(sync);
        this.launch = launch ?? (start => Process.Start(start) ?? throw new IOException("Не удалось запустить помощник."));
        freshness.Tick += (_, _) =>
        {
            if (session?.Connected == true && lastUpdate != 0 && Stopwatch.GetElapsedTime(lastUpdate).TotalSeconds > 5)
                Set(Snapshot with { Fps = null, Ping = null, FrameTime = null, OnePercentLow = null, Jitter = null,
                    FrameTimeHistory = null, MapName = "", MapMeasuredAt = null, HelperStatus = "Ожидание данных помощника…" });
        };
        freshness.Start();
    }

    public void Apply(PerformanceSettings next, bool allowElevation = false)
    {
        var wasEnabled = settings.Enabled;
        var portChanged = settings.QueryPort != next.QueryPort;
        settings = next.Clone();
        if (!settings.Enabled)
        {
            Stop();
            Set(Disabled());
        }
        else if (session is null)
        {
            if (!wasEnabled || allowElevation) Start();
        }
        else if (portChanged && session.Connected) _ = ConfigureAsync(session, settings.QueryPort);
    }

    public void Start()
    {
        if (disposed || !settings.Enabled || session is not null) return;
        var next = new Session();
        session = next;
        Set(Waiting("Запуск помощника измерений…") with { HelperCanStart = false, HelperStarting = true });
        _ = RunAsync(next);
    }

    private async Task RunAsync(Session active)
    {
        string? failure = null;
        try
        {
            active.Pipe = PerformancePipe.CreateServer(active.Name);
            var executable = Environment.ProcessPath ?? throw new IOException("Не найден EXE Companion.");
            if (string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase))
                throw new IOException("Для запуска помощника откройте DayZMapCompanion.exe.");
            using var parent = Process.GetCurrentProcess();
            var ticks = parent.StartTime.ToUniversalTime().Ticks;
            var scheduled = await PerformanceScheduledTask.LaunchAsync(active.Name, parent.Id, ticks, launch, active.Lifetime.Token);
            if (!scheduled)
            {
                var start = new ProcessStartInfo(executable) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
                foreach (var argument in new[] { PerformanceWorker.Argument, active.Name, parent.Id.ToString(), ticks.ToString() }) start.ArgumentList.Add(argument);
                active.Worker = await Task.Run(() => launch(start));
            }
            if (active.Lifetime.IsCancellationRequested) return;
            using var connect = CancellationTokenSource.CreateLinkedTokenSource(active.Lifetime.Token);
            connect.CancelAfter(TimeSpan.FromSeconds(15));
            await active.Pipe.WaitForConnectionAsync(connect.Token);
            if (scheduled)
            {
                var pid = PerformancePipe.ClientProcessId(active.Pipe);
                PerformanceProcessIdentity.ValidateHelperExecutable(pid);
                active.Worker = Process.GetProcessById(pid);
            }
            else PerformancePipe.ValidateClient(active.Pipe, active.Worker!.Id);
            active.Connected = true;
            lastUpdate = Stopwatch.GetTimestamp();
            await ConfigureAsync(active, settings.QueryPort);
            while (!active.Lifetime.IsCancellationRequested)
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(active.Lifetime.Token);
                deadline.CancelAfter(TimeSpan.FromSeconds(10));
                var update = await PerformancePipe.ReadAsync<PerformanceWorkerUpdate>(active.Pipe, deadline.Token);
                if (update.Version != 1 || update.Snapshot is null) throw new InvalidDataException("Неверный ответ помощника.");
                if (session != active || disposed) return;
                lastUpdate = Stopwatch.GetTimestamp();
                Set(update.Snapshot with { HelperStatus = "Помощник измерений работает.", HelperCanStart = false, HelperStarting = false });
            }
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            failure = "Запуск отменён. Остальные функции Companion доступны без прав администратора.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException or Win32Exception or System.Text.Json.JsonException or ObjectDisposedException or System.Runtime.InteropServices.COMException)
        {
            if (!active.Lifetime.IsCancellationRequested)
            {
                failure = ex is OperationCanceledException ? "Помощник не ответил. Можно повторить запуск." : "Помощник недоступен: " + ex.Message;
                AppRuntimeLog.Error("Performance helper unavailable", ex);
            }
        }
        finally
        {
            active.Dispose();
            if (session == active)
            {
                session = null;
                if (!disposed && settings.Enabled)
                {
                    settings.Enabled = false;
                    var message = failure ?? "Измерения остановлены. Для повторного запуска включите галочку.";
                    Set(Disabled() with { HelperStatus = message });
                    EnableRejected?.Invoke(message);
                }
            }
        }
    }

    private async Task ConfigureAsync(Session active, int queryPort)
    {
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(active.Lifetime.Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(5));
            await active.Writes.WaitAsync(deadline.Token);
            try { await PerformancePipe.WriteAsync(active.Pipe!, new PerformanceWorkerCommand(1, queryPort), deadline.Token); }
            finally { active.Writes.Release(); }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
            active.Dispose();
        }
    }

    private void Set(PerformanceSnapshot snapshot)
    {
        if (disposed) return;
        Snapshot = snapshot;
        Changed?.Invoke(snapshot);
    }

    private static PerformanceSnapshot Disabled() => new(FpsStatus: "Измерения выключены", PingStatus: "Измерения выключены", HelperStatus: "Помощник не запущен.");
    private static PerformanceSnapshot Waiting(string message) => new(FpsStatus: "Ожидание разрешения", PingStatus: "Ожидание разрешения", HelperStatus: message);

    private void Stop()
    {
        var previous = session;
        session = null;
        previous?.Dispose();
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        freshness.Stop(); freshness.Dispose();
        Stop();
    }
}
