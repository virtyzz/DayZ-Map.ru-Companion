using System.Drawing;

namespace CrosshairMarker;

internal sealed class EditorWindowBounds
{
    public int Left { get; set; }
    public int Top { get; set; }
    public int Width { get; set; } = 1240;
    public int Height { get; set; } = 780;

    public Rectangle ToRectangle() => new(Left, Top, Width, Height);
    public EditorWindowBounds Clone() => new() { Left = Left, Top = Top, Width = Width, Height = Height };

    public void Normalize()
    {
        Width = Math.Clamp(Width, 1, 10000);
        Height = Math.Clamp(Height, 1, 10000);
    }

    public static EditorWindowBounds FromRectangle(Rectangle bounds) => new()
    {
        Left = bounds.Left,
        Top = bounds.Top,
        Width = bounds.Width,
        Height = bounds.Height
    };
}

internal static class EditorWindowPlacement
{
    internal const int MinimumWidth = 1060;
    internal const int MinimumHeight = 700;
    private static readonly Size DefaultSize = new(1240, 780);

    public static Rectangle Normalize(EditorWindowBounds? saved, Rectangle preferredArea, IEnumerable<Rectangle> workingAreas)
    {
        var areas = workingAreas.Where(area => area.Width > 0 && area.Height > 0).ToList();
        if (areas.Count == 0) areas.Add(preferredArea);
        var input = saved?.ToRectangle();
        var target = input is null ? preferredArea : FindBestArea(input.Value, areas, preferredArea);
        var requested = input ?? new Rectangle(target.Location, DefaultSize);
        var width = Math.Min(target.Width, Math.Max(MinimumWidth, requested.Width));
        var height = Math.Min(target.Height, Math.Max(MinimumHeight, requested.Height));
        var left = Math.Clamp(requested.Left, target.Left, target.Right - width);
        var top = Math.Clamp(requested.Top, target.Top, target.Bottom - height);
        return new Rectangle(left, top, width, height);
    }

    private static Rectangle FindBestArea(Rectangle bounds, IEnumerable<Rectangle> areas, Rectangle fallback)
    {
        var best = fallback;
        var bestArea = -1L;
        foreach (var area in areas)
        {
            var intersection = Rectangle.Intersect(bounds, area);
            var intersectArea = (long)intersection.Width * intersection.Height;
            if (intersectArea > bestArea)
            {
                best = area;
                bestArea = intersectArea;
            }
        }
        return best;
    }
}
