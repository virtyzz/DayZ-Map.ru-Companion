using System.Diagnostics;

namespace CrosshairMarker;

internal sealed class PlayerPositionTrackingController : IDisposable
{
    private readonly PlayerPositionCaptureService capture;
    private readonly PlayerPositionMapBridge bridge;
    private readonly Func<PlayerPositionTrackingSettings> getSettings;
    private readonly Action<PlayerPositionTrackingSettings> save;
    private CancellationTokenSource? cancellation;
    private Task? worker;
    private DateTimeOffset lastErrorLoggedAt;
    private string lastLoggedError = "";

    public PlayerPositionTrackingController(PlayerPositionCaptureService capture, PlayerPositionMapBridge bridge, Func<PlayerPositionTrackingSettings> getSettings, Action<PlayerPositionTrackingSettings> save)
    { this.capture = capture; this.bridge = bridge; this.getSettings = getSettings; this.save = save; }

    public void Refresh()
    {
        var settings = getSettings(); settings.Normalize();
        if (settings.Enabled && !settings.Paused && cancellation is null) Start();
        if ((!settings.Enabled || settings.Paused) && cancellation is not null) Stop();
    }
    private void Start() { cancellation = new CancellationTokenSource(); worker = Task.Run(() => RunAsync(cancellation.Token)); }
    private void Stop() { cancellation?.Cancel(); cancellation?.Dispose(); cancellation = null; worker = null; }
    private async Task RunAsync(CancellationToken token)
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
                    var map = ResolveMap(session, settings);
                    if (session is null || map is null)
                    {
                        throw new DayZCompanionException("Выбранная карта изменилась или стала недоступна. Подтвердите её на вкладке перед продолжением.");
                    }
                    var recognized = await capture.CaptureAndRecognizeAsync(settings, map, token);
                    if (recognized.Position is null) throw new DayZCompanionException(recognized.Message);
                    bridge.Queue(settings, recognized.Position);
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
        finally { cancellation = null; }
    }
    private static TreasureMapOption? ResolveMap(TreasureDestinationSession? session, PlayerPositionTrackingSettings settings)
    {
        if (session is null) return null;

        var map = session.Maps.SingleOrDefault(item => string.Equals(item.Id, settings.MapId, StringComparison.Ordinal));
        if (map is null && !string.IsNullOrWhiteSpace(settings.MapId))
        {
            map = session.Maps.SingleOrDefault(item => string.Equals(item.Id, settings.MapId, StringComparison.OrdinalIgnoreCase)
                || string.Equals(item.Name, settings.MapId, StringComparison.OrdinalIgnoreCase));
        }

        // The local development page exposes one map. Accept it as the
        // destination even if an older Companion version saved a stale ID.
        if (map is null && session.Maps.Count == 1) map = session.Maps[0];
        if (map is not null && !string.Equals(settings.MapId, map.Id, StringComparison.Ordinal)) settings.MapId = map.Id;
        return map;
    }

    public void Dispose() => Stop();
}
