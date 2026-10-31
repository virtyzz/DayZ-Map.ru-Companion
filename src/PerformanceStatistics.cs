namespace CrosshairMarker;

internal sealed record FrameStatistics(double? Fps, double? FrameTime, double? OnePercentLow, double? PeakFrameTime, string Status);

internal sealed class FrameStatisticsWindow
{
    private readonly Queue<(double Time, double Interval, string Chain)> frames = new();
    private double latestTime;

    public void Add((double Time, double Interval, string Chain) frame)
    {
        latestTime = Math.Max(latestTime, frame.Time);
        frames.Enqueue(frame);
        while (frames.Count > 0 && (latestTime - frames.Peek().Time > 30000 || frames.Count > 100000)) frames.Dequeue();
    }

    public FrameStatistics Read()
    {
        // Select the dominant current swap chain, then use only its history.
        var current = frames.Where(f => f.Time >= latestTime - 1000).GroupBy(f => f.Chain)
            .OrderByDescending(g => g.Count()).FirstOrDefault();
        if (current is null || current.Count() < 2) return new(null, null, null, null, "Ожидание кадров DayZ");
        var mean = current.Average(f => f.Interval);
        var history = frames.Where(f => f.Chain == current.Key && f.Time >= latestTime - 30000).Select(f => f.Interval).ToArray();
        double? low = history.Length < 100 ? null : 1000 / history.OrderDescending().Take((int)Math.Ceiling(history.Length * .01)).Average();
        return new(1000 / mean, mean, low, current.Max(f => f.Interval), "Обновление раз в секунду · PresentMon");
    }
}

internal sealed class PingStatisticsWindow
{
    private readonly Queue<(DateTimeOffset Time, double Value)> samples = new();
    public double? Add(DateTimeOffset now, double milliseconds)
    {
        samples.Enqueue((now, milliseconds));
        while (samples.Count > 0 && (now - samples.Peek().Time > TimeSpan.FromSeconds(30) || samples.Count > 100)) samples.Dequeue();
        var values = samples.Select(s => s.Value).ToArray();
        return values.Length < 2 ? null : values.Zip(values.Skip(1), (a, b) => Math.Abs(b - a)).Average();
    }
    public void Clear() => samples.Clear();
}

internal sealed class FrameGraphHistory
{
    private readonly SortedDictionary<long, double?> samples = new();
    public double?[] Add(DateTimeOffset now, double? peak)
    {
        var second = now.ToUnixTimeSeconds();
        if (samples.TryGetValue(second, out var previous) && previous.HasValue && peak.HasValue) peak = Math.Max(previous.Value, peak.Value);
        samples[second] = peak;
        foreach (var key in samples.Keys.Where(s => s < second - 59).ToArray()) samples.Remove(key);
        return Enumerable.Range(0, 60).Select(i => samples.GetValueOrDefault(second - 59 + i)).ToArray();
    }
    public void Clear() => samples.Clear();
}
