namespace CrosshairMarker;

internal sealed record DayZCorsHeaders(
    string Origin,
    string Methods,
    string AllowedHeaders,
    bool AllowPrivateNetwork);

internal static class DayZCorsPolicy
{
    private static readonly string[] ProductionOrigins = ["https://dayz-map.ru", "https://www.dayz-map.ru"];

    public static bool TryCreate(string? origin, bool allowDevelopmentOrigin, bool privateNetworkRequest, out DayZCorsHeaders? headers)
    {
        if (origin is null ||
            (!ProductionOrigins.Contains(origin, StringComparer.Ordinal) &&
             !(allowDevelopmentOrigin && origin == "http://localhost:8000")))
        {
            headers = null;
            return false;
        }

        headers = new DayZCorsHeaders(origin, "GET, POST, OPTIONS", "Content-Type", privateNetworkRequest);
        return true;
    }
}
