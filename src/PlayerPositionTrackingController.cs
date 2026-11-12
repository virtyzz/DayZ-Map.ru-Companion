using System.Diagnostics;

namespace CrosshairMarker;

internal sealed class PlayerPositionTrackingController : IDisposable
{
    private readonly PlayerPositionCaptureService capture;
    private readonly PlayerPositionMapBridge bridge;
    private readonly Func<PlayerPositionTrackingSettings> getSettings;
    private readonly Action<PlayerPositionTrackingSettings> save;
    private readonly Func<PerformanceSnapshot>? getServer;
    private CancellationTokenSource? cancellation;
    private Task? worker;
    private readonly object lifecycle = new();
    private DateTimeOffset lastErrorLoggedAt;
    private string lastLoggedError = "";

    public PlayerPositionTrackingController(PlayerPositionCaptureService capture, PlayerPositionMapBridge bridge, Func<PlayerPositionTrackingSettings> getSettings, Action<PlayerPositionTrackingSettings> save, Func<PerformanceSnapshot>? getServer = null)
    { this.capture = capture; this.bridge = bridge; this.getSettings = getSettings; this.save = save; this.getServer = getServer; }

    public void Refresh()
    {
        var settings = getSettings(); settings.Normalize();
        if (settings.Enabled && !settings.Paused && cancellation is null) Start();
        if ((!settings.Enabled || settings.Paused) && cancellation is not null) Stop();
    }
    private void Start()
    {
        lock (lifecycle)
        {
            if (cancellation is not null) return;
            var owner = cancellation = new CancellationTokenSource();
            var token = owner.Token;
            capture.SetTrackingEnabled(true);
            worker = Task.Run(() => RunAsync(owner, token));
        }
    }
    private void Stop()
    {
        lock (lifecycle)
        {
            cancellation?.Cancel(); cancellation?.Dispose(); cancellation = null; worker = null;
            capture.SetTrackingEnabled(false);
        }
    }
    private async Task RunAsync(CancellationTokenSource owner, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                var cycleStarted = Stopwatch.GetTimestamp();
                var settings = getSettings(); settings.Normalize();
                if (!settings.Enabled || settings.Paused) break;
                try
                {
                    var session = bridge.GetSession();
                    var server = getServer?.Invoke();
                    var map = session is null ? null : PlayerPositionMapResolver.Resolve(session, server ?? new PerformanceSnapshot());
                    if (session is null || map is null)
                    {
                        throw new DayZCompanionException("Откройте DayZ-Map в браузере — карта будет определена по текущему серверу DayZ.");
                    }
                    settings.MapId = map.Id;
                    var originalRegion = settings.Clone();
                    var recognized = await capture.CaptureAndRecognizeAsync(settings, map, token);
                    if (recognized.Position is null) throw new DayZCompanionException(recognized.Message);
                    var current = getSettings();
                    if (current.HasSameRegion(originalRegion) && !current.RegionRelativeToWindow && settings.RegionRelativeToWindow)
                        current.CopyRegionFrom(settings);
                    if (!current.Enabled || current.Paused) continue;
                    var currentServer = getServer?.Invoke();
                    if (server?.Server != currentServer?.Server) continue;
                    if (PlayerPositionMapResolver.Resolve(session, currentServer ?? new PerformanceSnapshot()).Id != map.Id) continue;
                    current.MapId = map.Id;
                    settings = current;
                    bridge.Queue(settings, recognized.Position, currentServer);
                    settings.LastX = recognized.Position.X; settings.LastY = recognized.Position.Y; settings.LastZ = recognized.Position.Z;
                    settings.LastError = ""; settings.ConsecutiveErrors = 0;
                    lastLoggedError = "";
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    settings.ConsecutiveErrors++;
                    settings.LastError = ex.Message;
                    if (!string.Equals(lastLoggedError, ex.Message, StringComparison.Ordinal) || DateTimeOffset.UtcNow - lastErrorLoggedAt >= TimeSpan.FromSeconds(30))
                    {
                        AppRuntimeLog.Error("Player position tracking failed", ex);
                        lastLoggedError = ex.Message;
                        lastErrorLoggedAt = DateTimeOffset.UtcNow;
                    }
                }
                try
                {
                    save(settings);
                }
                catch (Exception ex)
                {
                    AppRuntimeLog.Error("Could not persist player position tracking state", ex);
                }
                // The configured interval is the cadence between measurements,
                // not an extra wait after OCR has already completed.
                var elapsed = Stopwatch.GetElapsedTime(cycleStarted);
                var remaining = TimeSpan.FromSeconds(settings.IntervalSeconds) - elapsed;
                if (remaining > TimeSpan.Zero) await Task.Delay(remaining, token);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            // A stopped worker may finish after tracking has already restarted.
            lock (lifecycle)
            {
                if (ReferenceEquals(cancellation, owner))
                {
                    cancellation = null;
                    capture.SetTrackingEnabled(false);
                    owner.Dispose();
                }
            }
        }
    }
    public void Dispose() => Stop();
}
