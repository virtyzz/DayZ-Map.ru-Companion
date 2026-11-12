namespace CrosshairMarker;

internal static class PlayerPositionMapResolver
{
    private static string Key(string value) => new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    public static TreasureMapOption Resolve(TreasureDestinationSession session, PerformanceSnapshot snapshot)
    {
        if (!snapshot.Running || string.IsNullOrWhiteSpace(snapshot.Server) || snapshot.MapMeasuredAt is not { } measured
            || DateTimeOffset.UtcNow - measured > TimeSpan.FromSeconds(15) || string.IsNullOrWhiteSpace(snapshot.MapName))
            throw new DayZCompanionException("Ожидание определения карты по текущему серверу DayZ. Проверьте подключение и доступность query-порта.");
        var key = Key(snapshot.MapName);
        var id = key switch { "chernarus" or "chernarusplus" => "cherno", "deerisle" => "deerisle", _ => key };
        return session.Maps.SingleOrDefault(map => Key(map.Id) == id || Key(map.Name) == key)
            ?? throw new DayZCompanionException($"Карта сервера «{snapshot.MapName}» недоступна на сайте. Передача позиции приостановлена.");
    }
}
