namespace CrosshairMarker;

internal sealed class PerformanceSettings
{
    public bool Enabled { get; set; }
    public bool ShowOverlay { get; set; } = true;
    public bool ShowOnlyInGame { get; set; } = true;
    public int QueryPort { get; set; } // 0 = automatic
    public bool EditingEnabled { get; set; }
    public string? MonitorDeviceName { get; set; }
    public int Left { get; set; } = 16;
    public int Top { get; set; } = 16;
    public int Width { get; set; } = 265;
    public int Height { get; set; } = 42;
    public int Opacity { get; set; } = 90;
    public int? TextOpacity { get; set; }
    public int? BackgroundOpacity { get; set; }
    public string TextColor { get; set; } = "#EEB15B";
    public string BackgroundColor { get; set; } = "#141416";
    public string FontFamily { get; set; } = "Segoe UI";
    public int FontSize { get; set; } = 12;
    public bool Bold { get; set; } = true;
    public bool Italic { get; set; }
    public bool ShowFps { get; set; } = true;
    public bool ShowPing { get; set; } = true;
    public bool ShowFrameTime { get; set; }
    public bool ShowFrameGraph { get; set; }
    public bool ShowOnePercentLow { get; set; }
    public bool ShowJitter { get; set; }
    public bool ColorByThresholds { get; set; }
    public int FpsWarning { get; set; } = 60;
    public int FpsCritical { get; set; } = 30;
    public int PingWarning { get; set; } = 100;
    public int PingCritical { get; set; } = 200;
    public string GoodColor { get; set; } = "#6BCB77";
    public string WarningColor { get; set; } = "#FFCF5C";
    public string CriticalColor { get; set; } = "#FF6B6B";
    public bool VerticalLayout { get; set; }
    public string Alignment { get; set; } = "Center";

    public void Normalize()
    {
        QueryPort = Math.Clamp(QueryPort, 0, 65535);
        Left = Math.Clamp(Left, 0, 100000);
        Top = Math.Clamp(Top, 0, 100000);
        Width = Math.Clamp(Width, 120, 1200);
        Height = Math.Clamp(Height, 32, 500);
        Opacity = Math.Clamp(Opacity, 10, 100);
        TextOpacity = Math.Clamp(TextOpacity ?? Opacity, 0, 100);
        BackgroundOpacity = Math.Clamp(BackgroundOpacity ?? Opacity, 0, 100);
        FontSize = Math.Clamp(FontSize, 8, 72);
        TextColor = ValidColor(TextColor) ? TextColor : "#EEB15B";
        BackgroundColor = ValidColor(BackgroundColor) ? BackgroundColor : "#141416";
        GoodColor = ValidColor(GoodColor) ? GoodColor : "#6BCB77";
        WarningColor = ValidColor(WarningColor) ? WarningColor : "#FFCF5C";
        CriticalColor = ValidColor(CriticalColor) ? CriticalColor : "#FF6B6B";
        FpsWarning = Math.Clamp(FpsWarning, 1, 1000);
        FpsCritical = Math.Clamp(FpsCritical, 1, FpsWarning);
        PingWarning = Math.Clamp(PingWarning, 1, 10000);
        PingCritical = Math.Clamp(PingCritical, PingWarning, 10000);
        FontFamily = string.IsNullOrWhiteSpace(FontFamily) ? "Segoe UI" : FontFamily.Trim()[..Math.Min(FontFamily.Trim().Length, 100)];
        if (Alignment is not ("Left" or "Center" or "Right")) Alignment = "Center";
        if (!ShowFps && !ShowPing && !ShowFrameTime && !ShowOnePercentLow && !ShowJitter && !ShowFrameGraph) ShowFps = true;
    }

    internal string GetMetricColor(double? value, bool fps)
    {
        if (!ColorByThresholds || !value.HasValue) return TextColor;
        return fps ? value < FpsCritical ? CriticalColor : value < FpsWarning ? WarningColor : GoodColor
            : value >= PingCritical ? CriticalColor : value >= PingWarning ? WarningColor : GoodColor;
    }

    private static bool ValidColor(string? value) => value is { Length: 7 } && value[0] == '#' && value.Skip(1).All(Uri.IsHexDigit);

    internal Rectangle GetBounds(Rectangle screenBounds)
    {
        var width = Math.Min(Width, screenBounds.Width);
        var height = Math.Min(Height, screenBounds.Height);
        return new Rectangle(screenBounds.Left + Math.Clamp(Left, 0, screenBounds.Width - width),
            screenBounds.Top + Math.Clamp(Top, 0, screenBounds.Height - height), width, height);
    }
    public PerformanceSettings Clone() => (PerformanceSettings)MemberwiseClone();
}

internal sealed record PerformanceOverlayPlacement(string MonitorDeviceName, int Left, int Top, int Width, int Height);

internal sealed record PerformanceSnapshot(
    bool Running = false, double? Fps = null, double? Ping = null,
    string Server = "", string Adapter = "", string Query = "",
    string FpsStatus = "Ожидание DayZ", string PingStatus = "Ожидание подключения",
    double? FrameTime = null, double? OnePercentLow = null, double? Jitter = null, double?[]? FrameTimeHistory = null,
    string HelperStatus = "", bool HelperCanStart = false, bool HelperStarting = false,
    string MapName = "", DateTimeOffset? MapMeasuredAt = null, string ServerName = "");
