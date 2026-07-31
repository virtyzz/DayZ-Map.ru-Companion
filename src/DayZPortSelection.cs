namespace CrosshairMarker;

internal static class DayZPortSelection
{
    public static IEnumerable<int> Candidates(DayZCompanionSettings settings)
    {
        if (!settings.AutoPort)
        {
            if (settings.Port is >= 1 and <= 65535) yield return settings.Port;
            yield break;
        }

        foreach (var port in Enumerable.Range(49950, 50)) yield return port;
    }

    public static int? FirstAvailable(DayZCompanionSettings settings, Func<int, bool> isAvailable)
    {
        foreach (var port in Candidates(settings))
        {
            if (isAvailable(port)) return port;
        }
        return null;
    }
}
