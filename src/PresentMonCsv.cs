using System.Globalization;

namespace CrosshairMarker;

internal sealed class PresentMonCsv
{
    private int timeIndex = -1, intervalIndex = -1, chainIndex = -1;
    private double timeScale;
    public bool HeaderSeen { get; private set; }

    public (double Time, double Interval, string Chain)? Read(string line)
    {
        var values = line.Split(',');
        int Index(string name) => Array.FindIndex(values, v => string.Equals(v.Trim().Trim('\uFEFF'), name, StringComparison.OrdinalIgnoreCase));
        var interval = Index("msBetweenPresents");
        if (interval >= 0)
        {
            // PresentMon v1 uses msBetweenPresents; v2 uses MsBetweenPresents.
            intervalIndex = interval;
            chainIndex = Index("SwapChainAddress");
            timeIndex = Index("TimeInSeconds");
            timeScale = 1000;
            if (timeIndex < 0) { timeIndex = Index("TimeInMs"); timeScale = 1; }
            HeaderSeen = timeIndex >= 0 && chainIndex >= 0;
            return null;
        }
        if (!HeaderSeen || values.Length <= Math.Max(timeIndex, Math.Max(intervalIndex, chainIndex))) return null;
        if (!double.TryParse(values[timeIndex], CultureInfo.InvariantCulture, out var time)
            || !double.TryParse(values[intervalIndex], CultureInfo.InvariantCulture, out var milliseconds)
            || !double.IsFinite(time) || !double.IsFinite(milliseconds) || milliseconds <= 0) return null;
        return (time * timeScale, milliseconds, values[chainIndex]);
    }
}
