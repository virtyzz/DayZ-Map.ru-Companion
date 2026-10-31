using System.Diagnostics;
using System.Net;

namespace CrosshairMarker;

internal sealed class PerformanceMonitor : IDisposable
{
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 1000 };
    private PerformanceSettings settings = new();
    private CancellationTokenSource session = new();
    private CancellationTokenSource connectionLifetime = new();
    private PresentMonReader? fps;
    private int? pid;
    private DayZConnection? connection;
    private IPEndPoint? query;
    private bool detecting, pinging, disposed;
    private DateTime nextDetection, nextPing, nextFpsStart;
    private int failures;
    private readonly PingStatisticsWindow pingStatistics = new();
    private readonly FrameGraphHistory graphHistory = new();
    private readonly List<Task> pending = new();
    private readonly int sessionId;
    public PerformanceSnapshot Snapshot { get; private set; } = new(FpsStatus: "Измерения выключены", PingStatus: "Измерения выключены");
    public event Action<PerformanceSnapshot>? Changed;

    public PerformanceMonitor(int? sessionId = null)
    {
        this.sessionId = sessionId ?? Process.GetCurrentProcess().SessionId;
        timer.Tick += (_, _) => Tick();
        timer.Start();
    }

    public void Apply(PerformanceSettings next)
    {
        var reset = settings.Enabled != next.Enabled || settings.QueryPort != next.QueryPort;
        settings = next.Clone();
        if (reset) Reset();
        Tick();
    }

    private void Reset()
    {
        session.Cancel();
        session.Dispose();
        session = new CancellationTokenSource();
        ResetConnection();
        StopReader();
        pid = null;
        nextFpsStart = DateTime.MinValue;
        detecting = false;
        nextDetection = DateTime.MinValue;
        graphHistory.Clear();
        Snapshot = new PerformanceSnapshot(FpsStatus: settings.Enabled ? "Ожидание DayZ" : "Измерения выключены",
            PingStatus: settings.Enabled ? "Ожидание подключения" : "Измерения выключены");
    }

    private void ResetConnection()
    {
        connectionLifetime.Cancel();
        connectionLifetime.Dispose();
        connectionLifetime = CancellationTokenSource.CreateLinkedTokenSource(session.Token);
        connection = null;
        query = null;
        pinging = false;
        failures = 0;
        pingStatistics.Clear();
        nextPing = DateTime.MinValue;
        Snapshot = Snapshot with { Ping = null, Jitter = null, Server = "", Adapter = "", Query = "", PingStatus = "Ожидание подключения" };
    }

    private void Tick()
    {
        if (disposed) return;
        if (!settings.Enabled) { Changed?.Invoke(Snapshot); return; }
        int? current = null;
        foreach (var process in Process.GetProcessesByName("DayZ_x64"))
        {
            using (process)
            {
                try { if (current is null && !process.HasExited && process.SessionId == sessionId) current = process.Id; }
                catch (InvalidOperationException) { }
            }
        }
        if (current != pid)
        {
            Reset();
            pid = current;
        }
        var now = DateTime.UtcNow;
        if (pid is { } frameProcess && (fps is null || fps.HasExited) && now >= nextFpsStart)
        {
            if (fps is not null) AppRuntimeLog.Info($"Restarting ended FPS collector for DayZ PID {frameProcess}.");
            StopReader();
            fps = new PresentMonReader(frameProcess);
            nextFpsStart = now.AddSeconds(5);
        }
        var frame = fps?.Read();
        Snapshot = Snapshot with { Running = pid.HasValue, Fps = frame?.Fps, FrameTime = frame?.FrameTime, OnePercentLow = frame?.OnePercentLow,
            FrameTimeHistory = graphHistory.Add(DateTimeOffset.UtcNow, frame?.PeakFrameTime), FpsStatus = frame?.Status ?? "Ожидание DayZ" };
        if (pid is { } game && !detecting && now >= nextDetection)
        {
            detecting = true;
            nextDetection = now.AddSeconds(6);
            Track(DetectAsync(game, session.Token));
        }
        if (connection is not null && !pinging && now >= nextPing)
        {
            pinging = true;
            nextPing = now.AddSeconds(1);
            Track(PingAsync(connection, connectionLifetime.Token));
        }
        Changed?.Invoke(Snapshot);
    }

    private async Task DetectAsync(int game, CancellationToken token)
    {
        try
        {
            var found = await Task.Run(() => DayZConnectionProbe.DetectAsync(game, token), token);
            token.ThrowIfCancellationRequested();
            if (found != connection)
            {
                ResetConnection();
                connection = found;
            }
            if (found is not null)
                Snapshot = Snapshot with { Server = found.Server.ToString(), Adapter = found.Adapter };
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested)
            {
                ResetConnection();
                Snapshot = Snapshot with { PingStatus = ex.Message };
            }
        }
        finally { if (!token.IsCancellationRequested) detecting = false; }
    }

    private async Task PingAsync(DayZConnection target, CancellationToken token)
    {
        try
        {
            A2sReply? result = null;
            if (query is null)
            {
                Snapshot = Snapshot with { Ping = null, PingStatus = "Поиск query-порта…" };
                var ports = settings.QueryPort > 0 ? new List<int> { settings.QueryPort }
                    : await A2sQueryClient.FindPortsAsync(target.Server, token);
                var steamPorts = settings.QueryPort == 0 ? ports.ToHashSet() : new HashSet<int>();
                // Only a small set of conventional candidates; never scan the port range.
                if (settings.QueryPort == 0)
                    ports.AddRange(new[] { target.Server.Port + 1, target.Server.Port + 3, 27016, 27015 });
                foreach (var port in ports.Where(p => p is > 0 and <= 65535).Distinct().Take(8))
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        var endpoint = new IPEndPoint(target.Server.Address, port);
                        var reply = await A2sQueryClient.QueryAsync(endpoint, target.LocalAddress, token);
                        // Several DayZ servers can share one IP. Never choose a neighbour's query service.
                        if (settings.QueryPort == 0 && (reply.GamePort is { } gamePort
                                ? gamePort != target.Server.Port : !steamPorts.Contains(port))) continue;
                        result = reply;
                        token.ThrowIfCancellationRequested();
                        query = endpoint;
                        break;
                    }
                    catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or OperationCanceledException)
                    {
                        token.ThrowIfCancellationRequested();
                    }
                    await Task.Delay(600, token);
                }
                if (result is null) throw new IOException("Сервер не ответил на запрос пинга.");
            }
            else result = await A2sQueryClient.QueryAsync(query, target.LocalAddress, token);
            token.ThrowIfCancellationRequested();
            failures = 0;
            Snapshot = Snapshot with { Ping = result.Milliseconds, Jitter = pingStatistics.Add(DateTimeOffset.UtcNow, result.Milliseconds),
                Query = query!.ToString(), PingStatus = result.Name + " · A2S_INFO · каждую секунду" };
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested)
            {
                failures++;
                pingStatistics.Clear();
                Snapshot = Snapshot with { Ping = null, Jitter = null, PingStatus = query is null ? ex.Message : "Нет ответа от query-порта" };
                if (query is null)
                {
                    // Do not repeat discovery on every update when the port is unavailable.
                    nextPing = DateTime.UtcNow.AddSeconds(60);
                }
                else if (failures >= 3) nextPing = DateTime.UtcNow.AddSeconds(Math.Min(30, failures * 2));
            }
        }
        finally
        {
            if (!token.IsCancellationRequested)
            {
                pinging = false;
                // Discovery takes several transactions; do not immediately send an extra sample.
                if (nextPing < DateTime.UtcNow) nextPing = DateTime.UtcNow.AddSeconds(1);
            }
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        timer.Stop();
        timer.Dispose();
        session.Cancel();
        connectionLifetime.Cancel();
        StopReader();
        session.Dispose();
        connectionLifetime.Dispose();
    }

    private void Track(Task task)
    {
        pending.RemoveAll(item => item.IsCompleted);
        pending.Add(task);
    }

    private void StopReader()
    {
        if (fps is not { } reader) return;
        fps = null;
        Track(reader.StopAsync());
    }

    public async Task StopAsync()
    {
        Dispose();
        try { await Task.WhenAll(pending.ToArray()); }
        catch (OperationCanceledException) { }
    }
}
