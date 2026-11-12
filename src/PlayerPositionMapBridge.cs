namespace CrosshairMarker;

internal sealed class PlayerPositionMapBridge
{
    private readonly object sync = new();
    private TreasureDestinationSession? session;
    private long sessionClaimedAt;
    private PlayerPositionUpdate? pending;
    private PlayerPositionUpdate? latest;
    private bool trackingEnabled;
    private PlayerPositionPublication publication = new("", "private", []);
    private string? feedback;
    public event Action<PlayerPositionUpdate>? Delivered;
    public event Action<TreasureDestinationSession>? SessionChanged;

    public void SetSession(TreasureDestinationSession value, long? claimedAt = null)
    {
        if (string.IsNullOrWhiteSpace(value.SessionId) || value.SessionId.Length < 20) throw new DayZCompanionException("Недействительный идентификатор сеанса карты.");
        if (value.Maps.Count == 0) throw new DayZCompanionException("Карта не передала список карт.");
        // The browser republishes its session periodically and whenever its page
        // is refreshed. Keep a queued position across that handshake: otherwise
        // an update can be silently discarded precisely while the map reconnects.
        TreasureDestinationSession current;
        lock (sync)
        {
            ExpireUnsafe();
            var candidateClaimedAt = claimedAt.GetValueOrDefault(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            // Several map pages can heartbeat concurrently. The last map the
            // user selected owns the live indicator; background heartbeats do
            // not steal it merely because they happen to arrive later.
            if (session is not null && session.SessionId != value.SessionId && candidateClaimedAt < sessionClaimedAt)
            {
                return;
            }
            session = value with { ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5) };
            sessionClaimedAt = candidateClaimedAt;
            current = session;
        }
        SessionChanged?.Invoke(current);
    }
    public TreasureDestinationSession? GetSession() { lock (sync) { ExpireUnsafe(); return session; } }
    public PlayerPositionUpdate? GetPending(string sessionId) { lock (sync) { ExpireUnsafe(); return session?.SessionId == sessionId ? pending : null; } }
    public bool IsActiveSession(string sessionId) { lock (sync) { ExpireUnsafe(); return session?.SessionId == sessionId; } }
    public bool IsTrackingEnabled { get { lock (sync) return trackingEnabled; } }
    public PlayerPositionPublication GetPublication() { lock (sync) return publication; }
    public string? GetFeedback() { lock (sync) { ExpireUnsafe(); return feedback; } }

    public void SetTrackingEnabled(bool enabled)
    {
        lock (sync)
        {
            trackingEnabled = enabled;
            if (!enabled)
            {
                pending = null;
                latest = null;
                feedback = "Отслеживание позиции отключено.";
            }
            else if (feedback == "Отслеживание позиции отключено.")
            {
                feedback = null;
            }
        }
    }

    public void Queue(PlayerPositionTrackingSettings settings, PlayerPosition position, PerformanceSnapshot? server = null)
    {
        settings.Normalize();
        lock (sync)
        {
            ExpireUnsafe();
            publication = new(settings.MapId, settings.Visibility, settings.ProfileIds.ToArray());
            if (session is null) throw new DayZCompanionException("Откройте DayZ-Map в браузере — Companion подключится автоматически.");
            var map = session.Maps.SingleOrDefault(item => item.Id == settings.MapId) ?? throw new DayZCompanionException("Выбранная карта недоступна.");
            if (map.Width > 0 && map.Height > 0 && (position.X < 0 || position.X > map.Width || position.Z < 0 || position.Z > map.Height)) throw new DayZCompanionException("Координаты вне выбранной карты.");
            pending = new PlayerPositionUpdate(session.SessionId, settings.MapId, "", settings.TrackingId, settings.MarkerUid, new TreasureMarkerTemplate(settings.MarkerName, settings.IndicatorShape, settings.MarkerColor), position.X, position.Y, position.Z, position.MeasuredAt,
                settings.CenterOnPlayer, settings.KeepZoom, settings.CenteringZoom, settings.ShowLabel, settings.MarkerSize, settings.LabelContent,
                settings.Visibility, settings.ProfileIds.ToArray(), settings.IntervalSeconds, settings.AutoSelectMap, GetServerInfo(server));
            latest = pending;
            feedback = "Обновление позиции ожидает карту.";
        }
    }
    private static PlayerPositionServerInfo? GetServerInfo(PerformanceSnapshot? snapshot)
    {
        if (snapshot is null || !snapshot.Running || string.IsNullOrWhiteSpace(snapshot.Server)) return null;
        var fresh = snapshot.MapMeasuredAt is { } measured && DateTimeOffset.UtcNow - measured <= TimeSpan.FromSeconds(15);
        return new(fresh ? snapshot.ServerName : "", snapshot.Server, fresh ? snapshot.MapName : "");
    }
    public void UpdateSettings(PlayerPositionTrackingSettings settings)
    {
        settings.Normalize();
        lock (sync)
        {
            ExpireUnsafe();
            publication = new(settings.MapId, settings.Visibility, settings.ProfileIds.ToArray());
            if (latest is not null && latest.MapId != settings.MapId) { latest = null; pending = null; }
            if (!trackingEnabled || latest is null || session?.SessionId != latest.SessionId || latest.MapId != settings.MapId) return;
            var updated = latest with
            {
                Template = new TreasureMarkerTemplate(settings.MarkerName, settings.IndicatorShape, settings.MarkerColor),
                CenterOnPlayer = settings.CenterOnPlayer, KeepZoom = settings.KeepZoom, CenteringZoom = settings.CenteringZoom,
                ShowLabel = settings.ShowLabel, MarkerSize = settings.MarkerSize, LabelContent = settings.LabelContent,
                Visibility = settings.Visibility,
                ProfileIds = (latest.ProfileIds ?? []).SequenceEqual(settings.ProfileIds) ? latest.ProfileIds : settings.ProfileIds.ToArray(),
                IntervalSeconds = settings.IntervalSeconds, AutoSelectMap = settings.AutoSelectMap
            };
            if (updated == latest) return;
            latest = pending = updated;
        }
    }
    public void Acknowledge(string sessionId, string trackingId, bool success, string message)
    {
        PlayerPositionUpdate? delivered = null;
        lock (sync)
        {
            ExpireUnsafe();
            if (session?.SessionId != sessionId || pending?.TrackingId != trackingId) return;
            feedback = success ? "Позиция обновлена на карте." : "Ошибка обновления позиции: " + message;
            if (success) { delivered = pending; pending = null; }
        }
        // Handlers may acquire the settings lock and call back into the bridge.
        // Never run them while holding sync.
        if (delivered is not null) Delivered?.Invoke(delivered);
    }
    private void ExpireUnsafe() { if (session is not null && session.ExpiresAt <= DateTimeOffset.UtcNow) { session = null; sessionClaimedAt = 0; pending = null; latest = null; } }
}
