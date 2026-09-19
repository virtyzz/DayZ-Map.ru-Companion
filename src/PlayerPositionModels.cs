using System.Drawing;

namespace CrosshairMarker;

internal sealed class PlayerPositionTrackingSettings
{
    public bool Enabled { get; set; }
    public bool Paused { get; set; }
    public string MapId { get; set; } = "";
    public string ProfileId { get; set; } = "";
    public string MarkerName { get; set; } = "Я";
    public string MarkerType { get; set; } = "player";
    // This belongs solely to the transient live overlay.  It is deliberately
    // independent from the map's regular marker types and profile data.
    public string IndicatorShape { get; set; } = "triangle";
    public string MarkerColor { get; set; } = "#3498db";
    public int IntervalSeconds { get; set; } = 5;
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public string TrackingId { get; set; } = Guid.NewGuid().ToString("N");
    public string MarkerUid { get; set; } = Guid.NewGuid().ToString("N");
    public int? LastX { get; set; }
    public int? LastY { get; set; }
    public int? LastZ { get; set; }
    public DateTimeOffset? LastSentAt { get; set; }
    public int ConsecutiveErrors { get; set; }
    public string LastError { get; set; } = "";

    public bool HasRegion => Width > 0 && Height > 0;

    public void Normalize()
    {
        IntervalSeconds = Math.Clamp(IntervalSeconds, 2, 300);
        X = Math.Clamp(X, 0, 1); Y = Math.Clamp(Y, 0, 1);
        Width = Math.Clamp(Width, 0, 1 - X); Height = Math.Clamp(Height, 0, 1 - Y);
        MarkerName = (MarkerName ?? "").Trim()[..Math.Min((MarkerName ?? "").Trim().Length, 300)];
        MarkerType = string.IsNullOrWhiteSpace(MarkerType) ? "player" : MarkerType.Trim();
        IndicatorShape = PlayerPositionIndicatorShapes.Normalize(IndicatorShape);
        if (!System.Text.RegularExpressions.Regex.IsMatch(MarkerColor ?? "", "^#[0-9a-fA-F]{6}$")) MarkerColor = "#3498db";
        if (string.IsNullOrWhiteSpace(TrackingId)) TrackingId = Guid.NewGuid().ToString("N");
        if (string.IsNullOrWhiteSpace(MarkerUid)) MarkerUid = Guid.NewGuid().ToString("N");
    }

    public PlayerPositionTrackingSettings Clone() => (PlayerPositionTrackingSettings)MemberwiseClone();

    public Rectangle GetScreenRectangle(Rectangle bounds)
    {
        var left = bounds.Left + (int)Math.Round(bounds.Width * X);
        var top = bounds.Top + (int)Math.Round(bounds.Height * Y);
        var width = (int)Math.Round(bounds.Width * Width);
        var height = (int)Math.Round(bounds.Height * Height);
        return Rectangle.Intersect(bounds, new Rectangle(left, top, width, height));
    }

    public void SetScreenRectangle(Rectangle selection, Rectangle bounds)
    {
        var clipped = Rectangle.Intersect(selection, bounds);
        if (clipped.Width <= 0 || clipped.Height <= 0) throw new DayZCompanionException("Область координат пуста.");
        X = (double)(clipped.Left - bounds.Left) / bounds.Width;
        Y = (double)(clipped.Top - bounds.Top) / bounds.Height;
        Width = (double)clipped.Width / bounds.Width;
        Height = (double)clipped.Height / bounds.Height;
        Normalize();
    }
}

internal static class PlayerPositionIndicatorShapes
{
    private static readonly HashSet<string> Allowed = new(StringComparer.Ordinal)
    {
        "circle", "square", "diamond", "triangle", "heart", "cross", "star", "hexagon", "pentagon", "x", "paw"
    };

    public static string Normalize(string? value)
    {
        var shape = (value ?? "").Trim().ToLowerInvariant();
        if (shape == "arrow") return "triangle"; // migrate the former duplicate option
        return Allowed.Contains(shape) ? shape : "triangle";
    }
}

internal sealed record PlayerPosition(int X, int Y, int Z, DateTimeOffset MeasuredAt);
internal sealed record PlayerPositionOcrResult(string RawText, PlayerPosition? Position, string Message);
internal sealed record PlayerPositionDiagnostics(DateTimeOffset CapturedAt, string RawText, string Message, bool Recognized, string SourceImagePath, string ProcessedImagePath);
internal sealed record PlayerPositionUpdate(string SessionId, string MapId, string ProfileId, string TrackingId, string MarkerUid, TreasureMarkerTemplate Template, int X, int Y, int Z, DateTimeOffset MeasuredAt);
