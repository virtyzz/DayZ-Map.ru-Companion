namespace CrosshairMarker;

internal sealed record DayZFileProbeResult(bool Writable, string? Error);

internal static class DayZFileAccessProbe
{
    public static DayZFileProbeResult Probe(string directory) => Probe(
        directory,
        path => File.Open(path, FileMode.CreateNew, FileAccess.Write, FileShare.None),
        path =>
        {
            if (File.Exists(path)) File.Delete(path);
        });

    internal static DayZFileProbeResult Probe(string directory, Func<string, Stream> create, Action<string> delete)
    {
        var path = Path.Combine(directory, ".PrivateMarkers." + Guid.NewGuid().ToString("N") + ".probe");
        try
        {
            using (create(path)) { }
            return new DayZFileProbeResult(true, null);
        }
        catch (IOException ex)
        {
            return new DayZFileProbeResult(false, "Нет доступа для записи рядом с PrivateMarkers.json: " + ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            return new DayZFileProbeResult(false, "Нет доступа для записи рядом с PrivateMarkers.json: " + ex.Message);
        }
        finally
        {
            try { delete(path); } catch { }
        }
    }
}
