using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace CrosshairMarker;

internal sealed class RoundedPanel : Panel
{
    public int Radius { get; set; } = 12;
    public Color BorderColor { get; set; } = Color.FromArgb(46, 46, 46);
    public int BorderThickness { get; set; } = 1;
    public Color HighlightColor { get; set; } = Color.Transparent;
    public bool DrawTopHighlight { get; set; }

    public RoundedPanel()
    {
        DoubleBuffered = true;
        ResizeRedraw = true;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var bounds = ClientRectangle;
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        using var path = CreatePath(bounds, Radius, Math.Max(0.5f, BorderThickness / 2f));
        using var brush = new SolidBrush(BackColor);
        e.Graphics.FillPath(brush, path);

        if (BorderThickness > 0)
        {
            using var pen = new Pen(BorderColor, BorderThickness);
            e.Graphics.DrawPath(pen, path);
        }

        if (DrawTopHighlight && HighlightColor.A > 0)
        {
            using var clip = CreatePath(bounds, Radius, 1.5f);
            e.Graphics.SetClip(clip);
            using var highlightBrush = new LinearGradientBrush(
                new Rectangle(bounds.X, bounds.Y, bounds.Width, Math.Max(1, bounds.Height / 3)),
                HighlightColor,
                Color.Transparent,
                LinearGradientMode.Vertical);
            e.Graphics.FillRectangle(highlightBrush, bounds);
            e.Graphics.ResetClip();
        }
    }

    protected override void OnResize(EventArgs eventargs)
    {
        base.OnResize(eventargs);
        using var path = CreatePath(ClientRectangle, Radius, 0.5f);
        Region = new Region(path);
    }

    private static GraphicsPath CreatePath(Rectangle bounds, int radius, float inset)
    {
        var path = new GraphicsPath();
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return path;
        }

        var rect = new RectangleF(
            bounds.X + inset,
            bounds.Y + inset,
            Math.Max(1, bounds.Width - (inset * 2) - 1),
            Math.Max(1, bounds.Height - (inset * 2) - 1));
        var diameter = Math.Min(Math.Max(1, radius * 2), Math.Min(rect.Width, rect.Height));

        path.AddArc(rect.X, rect.Y, diameter, diameter, 180, 90);
        path.AddArc(rect.Right - diameter, rect.Y, diameter, diameter, 270, 90);
        path.AddArc(rect.Right - diameter, rect.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rect.X, rect.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}
