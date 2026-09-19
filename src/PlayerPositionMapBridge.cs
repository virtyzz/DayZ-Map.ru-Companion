namespace CrosshairMarker;

internal sealed class PlayerPositionMapBridge
{
    private readonly object sync = new();
    private TreasureDestinationSession? session;
    private long sessionClaimedAt;
    private PlayerPositionUpdate? pending;
    private bool trackingEnabled;
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
    public string? GetFeedback() { lock (sync) { ExpireUnsafe(); return feedback; } }

    public void SetTrackingEnabled(bool enabled)
    {
        lock (sync)
        {
            trackingEnabled = enabled;
            if (!enabled)
            {
                pending = null;
                feedback = "Отслеживание позиции отключено.";
            }
        }
    }

    public void Queue(PlayerPositionTrackingSettings settings, PlayerPosition position)
    {
        lock (sync)
        {
            ExpireUnsafe();
            if (session is null) throw new DayZCompanionException("Откройте DayZ-Map и подключите Companion.");
            var map = session.Maps.SingleOrDefault(item => item.Id == settings.MapId) ?? throw new DayZCompanionException("Выбранная карта недоступна.");
            if (map.Width > 0 && map.Height > 0 && (position.X < 0 || position.X > map.Width || position.Z < 0 || position.Z > map.Height)) throw new DayZCompanionException("Координаты вне выбранной карты.");
            pending = new PlayerPositionUpdate(session.SessionId, settings.MapId, "", settings.TrackingId, settings.MarkerUid, new TreasureMarkerTemplate(settings.MarkerName, settings.IndicatorShape, settings.MarkerColor), position.X, position.Y, position.Z, position.MeasuredAt);
            feedback = "Обновление позиции ожидает карту.";
        }
    }
    public void Acknowledge(string sessionId, string trackingId, bool success, string message)
    {
        lock (sync)
        {
            ExpireUnsafe();
            if (session?.SessionId != sessionId || pending?.TrackingId != trackingId) return;
            feedback = success ? "Позиция обновлена на карте." : "Ошибка обновления позиции: " + message;
            var delivered = pending;
            if (success) pending = null;
            if (success && delivered is not null) Delivered?.Invoke(delivered);
        }
    }
    private void ExpireUnsafe() { if (session is not null && session.ExpiresAt <= DateTimeOffset.UtcNow) { session = null; sessionClaimedAt = 0; pending = null; } }
}
