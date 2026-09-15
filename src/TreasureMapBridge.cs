namespace CrosshairMarker;

internal sealed record TreasureMapOption(string Id, string Name, int Width = 0, int Height = 0);
internal sealed record TreasureProfileOption(string MapId, string Id, string Name, bool Writable, string MarkerName, string MarkerType, string MarkerColor);
internal sealed record TreasureDestinationSession(string SessionId, DateTimeOffset ExpiresAt, List<TreasureMapOption> Maps, List<TreasureProfileOption> Profiles);
internal sealed record TreasureMarkerTemplate(string Name, string Type, string Color);
internal sealed record TreasureMapBatch(string SessionId, string MapId, string ProfileId, TreasureMarkerTemplate Template, List<TreasureCoordinate> Coordinates);
internal sealed record TreasureCoordinate(string CaptureId, int X, int Z);
internal sealed record TreasureDeliveryOutcome(string CaptureId, string Result, string Message);

internal sealed class TreasureMapBridge
{
    private readonly object sync = new();
    private TreasureDestinationSession? session;
    private TreasureMapBatch? pending;
    private string? deliveryFeedback;
    private Dictionary<string, TreasureDeliveryOutcome> deliveryOutcomes = [];

    public TreasureDestinationSession? GetSession()
    {
        lock (sync)
        {
            ExpireUnsafe();
            return session;
        }
    }

    public void SetSession(TreasureDestinationSession value)
    {
        if (string.IsNullOrWhiteSpace(value.SessionId) || value.SessionId.Length < 20) throw new DayZCompanionException("Недействительный идентификатор сеанса карты.");
        if (value.Maps.Count == 0) throw new DayZCompanionException("Карта не передала список карт.");
        value = value with { ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5) };
        lock (sync)
        {
            session = value;
            pending = null;
        }
    }

    public void Queue(string mapId, string profileId, string markerName, string markerType, string markerColor, IEnumerable<TreasureCapture> captures)
    {
        lock (sync)
        {
            ExpireUnsafe();
            if (session is null) throw new DayZCompanionException("Откройте DayZ-Map и подключите Companion.");
            var map = session.Maps.SingleOrDefault(item => item.Id == mapId);
            if (map is null) throw new DayZCompanionException("Выбранная карта недоступна.");
            var profile = session.Profiles.SingleOrDefault(item => item.MapId == mapId && item.Id == profileId);
            if (profile is null || !profile.Writable) throw new DayZCompanionException("Выбранный профиль недоступен для записи.");
            var coordinates = captures.Where(item => item.HasCoordinates)
                .Select(item => new TreasureCoordinate(item.Id, item.X!.Value, item.Z!.Value)).ToList();
            if (coordinates.Count == 0) throw new DayZCompanionException("Нет координат для отправки.");
            if (map.Width > 0 && map.Height > 0 && coordinates.Any(item => item.X < 0 || item.X > map.Width || item.Z < 0 || item.Z > map.Height))
                throw new DayZCompanionException($"Координаты должны быть в пределах карты: X 0–{map.Width}, Z 0–{map.Height}.");
            if (coordinates.GroupBy(item => (item.X, item.Z)).Any(group => group.Count() > 1)) throw new DayZCompanionException("В очереди есть дублирующиеся координаты. Удалите или исправьте их перед отправкой.");
            var normalizedColor = markerColor ?? "";
            var template = new TreasureMarkerTemplate(
                markerName.Trim().Length > 300 ? markerName.Trim()[..300] : markerName.Trim(),
                string.IsNullOrWhiteSpace(markerType) ? profile.MarkerType : markerType.Trim(),
                System.Text.RegularExpressions.Regex.IsMatch(normalizedColor, "^#[0-9a-fA-F]{6}$") ? normalizedColor : profile.MarkerColor);
            pending = new TreasureMapBatch(session.SessionId, mapId, profileId, template, coordinates);
            deliveryFeedback = "Пакет ожидает приёма картой.";
        }
    }

    public TreasureMapBatch? GetPending(string sessionId)
    {
        lock (sync)
        {
            ExpireUnsafe();
            if (pending is null || session is null || !string.Equals(session.SessionId, sessionId, StringComparison.Ordinal)) return null;
            return pending;
        }
    }

    public void Acknowledge(string sessionId, bool success, string message, IEnumerable<TreasureDeliveryOutcome>? outcomes = null)
    {
        lock (sync)
        {
            ExpireUnsafe();
            if (pending is null || session is null || !string.Equals(session.SessionId, sessionId, StringComparison.Ordinal)) return;
            deliveryFeedback = success ? "Карта: " + message : "Ошибка импорта на карте: " + message;
            if (success)
            {
                // Do not guess a successful result for every capture when an
                // older map page does not return per-coordinate outcomes.
                // Guessing was the cause of skipped coordinates appearing as
                // "added" in Companion.
                deliveryOutcomes = (outcomes ?? [])
                    .ToDictionary(item => item.CaptureId, StringComparer.Ordinal);
                pending = null;
            }
        }
    }

    public string? GetDeliveryFeedback()
    {
        lock (sync) { ExpireUnsafe(); return deliveryFeedback; }
    }

    public IReadOnlyDictionary<string, TreasureDeliveryOutcome> GetDeliveryOutcomes()
    {
        lock (sync) { return new Dictionary<string, TreasureDeliveryOutcome>(deliveryOutcomes, StringComparer.Ordinal); }
    }

    private void ExpireUnsafe()
    {
        if (session is not null && session.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            session = null;
            pending = null;
        }
    }
}
