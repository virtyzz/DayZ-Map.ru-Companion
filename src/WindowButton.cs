using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace CrosshairMarker;

internal enum WindowButtonKind
{
    Minimize,
    Maximize,
    Close
}

internal sealed class WindowButton : Control
{
    private bool hover;
    private bool pressed;

    public WindowButtonKind Kind { get; init; }
    public Color SurfaceColor { get; set; } = Color.FromArgb(42, 42, 42);
    public Color HoverColor { get; set; } = Color.FromArgb(56, 56, 56);
    public Color PressedColor { get; set; } = Color.FromArgb(34, 34, 34);
    public Color BorderColor { get; set; } = Color.FromArgb(72, 72, 72);
    public Color GlyphColor { get; set; } = Color.White;

    public WindowButton()
    {
        DoubleBuffered = true;
        Cursor = Cursors.Hand;
        Size = new Size(42, 34);
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        hover = true;
        Invalidate();
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        hover = false;
        pressed = false;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        pressed = true;
        Invalidate();
        base.OnMouseDown(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        pressed = false;
        Invalidate();
        base.OnMouseUp(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;

        var fill = pressed ? PressedColor : hover ? HoverColor : SurfaceColor;
        var rect = new RectangleF(1.5f, 1.5f, Width - 4f, Height - 4f);
        using var path = RoundedPath(rect, 10);
        using var brush = new SolidBrush(fill);
        using var borderPen = new Pen(BorderColor, 1.2f);
        e.Graphics.FillPath(brush, path);
        e.Graphics.DrawPath(borderPen, path);

        using var glyphPen = new Pen(GlyphColor, 1.8f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };

        var cx = Width / 2f;
        var cy = Height / 2f;
        switch (Kind)
        {
            case WindowButtonKind.Minimize:
                e.Graphics.DrawLine(glyphPen, cx - 5, cy + 3, cx + 5, cy + 3);
                break;
            case WindowButtonKind.Maximize:
                e.Graphics.DrawRectangle(glyphPen, cx - 4.5f, cy - 4.5f, 9, 9);
                break;
            case WindowButtonKind.Close:
                e.Graphics.DrawLine(glyphPen, cx - 4, cy - 4, cx + 4, cy + 4);
                e.Graphics.DrawLine(glyphPen, cx + 4, cy - 4, cx - 4, cy + 4);
                break;
        }
    }

    private static GraphicsPath RoundedPath(RectangleF bounds, int radius)
    {
        var path = new GraphicsPath();
        var diameter = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
        path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}
