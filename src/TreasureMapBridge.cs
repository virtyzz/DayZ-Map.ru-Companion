namespace CrosshairMarker;

internal sealed record TreasureMapOption(string Id, string Name, int Width = 0, int Height = 0);
internal sealed record TreasureProfileOption(string MapId, string Id, string Name, bool Writable, string MarkerName, string MarkerType, string MarkerColor);
internal sealed record TreasureDestinationSession(string SessionId, DateTimeOffset ExpiresAt, List<TreasureMapOption> Maps, List<TreasureProfileOption> Profiles);
internal sealed record TreasureMarkerTemplate(string Name, string Type, string Color);
internal sealed record DayZPrivateMarker(int Type, int Uid, string Name, string Icon, List<double> Position, int CurrentSubgroup, int ColorA, int ColorR, int ColorG, int ColorB, string CreatorSteamID, double CircleRadius, int CircleColorA, int CircleColorR, int CircleColorG, int CircleColorB, int CircleStriked, int CircleLayer, int ShowAllPlayerNametags);
internal sealed record TreasurePendingMarker(string CaptureId, DayZPrivateMarker Marker);
internal sealed record TreasureMapBatch(string SessionId, string MapId, string ProfileId, TreasureMarkerTemplate Template, List<TreasureCoordinate> Coordinates, List<TreasurePendingMarker> Markers);
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
            pending = new TreasureMapBatch(session.SessionId, mapId, profileId, template, coordinates, CreateDayZMarkers(coordinates, template));
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

    private static List<TreasurePendingMarker> CreateDayZMarkers(IEnumerable<TreasureCoordinate> coordinates, TreasureMarkerTemplate template)
    {
        var color = ParseColor(template.Color);
        var usedUids = new HashSet<int>();
        return coordinates.Select(coordinate =>
        {
            var marker = new DayZPrivateMarker(5, CreateUid(coordinate.CaptureId, usedUids), template.Name, GetIconPath(template.Type), [coordinate.X, 0.0, coordinate.Z], 0, 255, color.R, color.G, color.B, "", 0.0, 255, 255, 255, 255, 0, -1, 0);
            return new TreasurePendingMarker(coordinate.CaptureId, marker);
        }).ToList();
    }

    private static (int R, int G, int B) ParseColor(string color)
    {
        var match = System.Text.RegularExpressions.Regex.Match(color ?? "", "^#(?<r>[0-9a-fA-F]{2})(?<g>[0-9a-fA-F]{2})(?<b>[0-9a-fA-F]{2})$");
        return match.Success
            ? (Convert.ToInt32(match.Groups["r"].Value, 16), Convert.ToInt32(match.Groups["g"].Value, 16), Convert.ToInt32(match.Groups["b"].Value, 16))
            : (245, 166, 35);
    }

    private static int CreateUid(string captureId, HashSet<int> usedUids)
    {
        uint hash = 2166136261;
        foreach (var character in captureId ?? "") { hash ^= character; hash *= 16777619; }
        var uid = 1_000_000_000 + (int)(hash % 1_000_000_000);
        while (!usedUids.Add(uid)) uid = uid == 1_999_999_999 ? 1_000_000_000 : uid + 1;
        return uid;
    }

    private static string GetIconPath(string type)
    {
        var icon = type?.Trim().ToLowerInvariant();
        var supported = new HashSet<string>(StringComparer.Ordinal) { "cross", "home", "camp", "safezone", "blackmarket", "hospital", "sniper", "player", "flag", "star", "car", "parking", "heli", "rail", "ship", "scooter", "bank", "restaurant", "post", "castle", "ranger-station", "water", "triangle", "cow", "bear", "car-repair", "communications", "roadblock", "stadium", "skull", "rocket", "bbq", "ping", "circle" };
        if (!supported.Contains(icon ?? "")) icon = "marker";
        return "LBmaster_Groups\\gui\\icons\\" + icon + ".paa";
    }
}
