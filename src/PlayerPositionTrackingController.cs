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
        (string Server, Guid? ConnectionId, string MapId)? lastSource = null;
        PlayerPosition? lastPosition = null;
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
                    var server = getServer?.Invoke() ?? new PerformanceSnapshot();
                    if (session is null)
                    {
                        throw new DayZCompanionException("Откройте DayZ-Map в браузере — карта будет определена по текущему серверу DayZ.");
                    }
                    var map = PlayerPositionMapResolver.Resolve(session, server);
                    var source = (server.Server, server.ConnectionId, map.Id);
                    if (source != lastSource) lastPosition = null;
                    lastSource = source;
                    settings.SelectMap(map.Id);
                    var originalRegion = settings.Clone();
                    var recognized = await capture.CaptureAndRecognizeAsync(settings, map, token);
                    PlayerPosition? position = recognized.Position ?? throw new DayZCompanionException(recognized.Message);
                    if (lastPosition is { } previous && RequiresConfirmation(previous, position))
                    {
                        var retry = await capture.CaptureAndRecognizeAsync(settings, map, token);
                        position = ResolveConfirmation(previous, position, retry.Position);
                    }
                    token.ThrowIfCancellationRequested();
                    var current = getSettings();
                    if (!current.Enabled || current.Paused || !current.HasSameRegion(originalRegion)) continue;
                    var currentServer = getServer?.Invoke() ?? new PerformanceSnapshot();
                    if (server.Server != currentServer.Server || server.ConnectionId != currentServer.ConnectionId) continue;
                    if (PlayerPositionMapResolver.Resolve(session, currentServer).Id != map.Id) continue;
                    if (!current.RegionRelativeToWindow && settings.RegionRelativeToWindow) current.CopyRegionFrom(settings);
                    current.SelectMap(map.Id);
                    settings = current;
                    if (position is null) throw new DayZCompanionException("Скачок координат не подтвердился при повторном считывании.");
                    bridge.Queue(settings, position, currentServer);
                    lastPosition = position;
                    settings.LastX = position.X; settings.LastY = position.Y; settings.LastZ = position.Z;
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

    internal static bool RequiresConfirmation(PlayerPosition previous, PlayerPosition candidate)
    {
        var seconds = Math.Clamp((candidate.MeasuredAt - previous.MeasuredAt).TotalSeconds, 0, 10);
        // Allow HUD rounding and vehicle movement. Cap the allowance so a long
        // interval cannot make a single bad OCR reading look credible.
        return HorizontalDistance(previous, candidate) > 50 + 45 * seconds;
    }

    internal static PlayerPosition? ResolveConfirmation(PlayerPosition previous, PlayerPosition candidate, PlayerPosition? retry)
    {
        if (retry is null || retry.MeasuredAt <= candidate.MeasuredAt) return null;
        if (!RequiresConfirmation(previous, retry)) return retry;

        var seconds = (retry.MeasuredAt - candidate.MeasuredAt).TotalSeconds;
        // Allow movement between reads while requiring both reads to place the
        // player in the same area.
        return seconds <= 5 && HorizontalDistance(candidate, retry) <= 15 + 45 * seconds ? retry : null;
    }

    private static double HorizontalDistance(PlayerPosition first, PlayerPosition second)
    {
        var dx = (double)first.X - second.X;
        var dz = (double)first.Z - second.Z;
        return Math.Sqrt(dx * dx + dz * dz);
    }

    public void Dispose() => Stop();
}
