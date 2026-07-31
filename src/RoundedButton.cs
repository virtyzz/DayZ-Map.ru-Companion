using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace CrosshairMarker;

internal sealed class RoundedButton : Button
{
    private bool hover;
    private bool pressed;

    public int Radius { get; set; } = 10;
    public Color BorderColor { get; set; } = Color.FromArgb(62, 62, 62);
    public Color HoverBackColor { get; set; } = Color.FromArgb(42, 42, 42);
    public Color PressedBackColor { get; set; } = Color.FromArgb(52, 52, 52);
    public Color HighlightColor { get; set; } = Color.Transparent;

    public RoundedButton()
    {
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        DoubleBuffered = true;
        ResizeRedraw = true;
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

    protected override void OnMouseDown(MouseEventArgs mevent)
    {
        pressed = true;
        Invalidate();
        base.OnMouseDown(mevent);
    }

    protected override void OnMouseUp(MouseEventArgs mevent)
    {
        pressed = false;
        Invalidate();
        base.OnMouseUp(mevent);
    }

    protected override void OnPaint(PaintEventArgs pevent)
    {
        var bounds = ClientRectangle;
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        pevent.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        pevent.Graphics.PixelOffsetMode = PixelOffsetMode.Half;
        var fill = !Enabled
            ? Color.FromArgb(38, 38, 38)
            : pressed
                ? PressedBackColor
                : hover
                    ? HoverBackColor
                    : BackColor;

        using var path = CreatePath(bounds, Radius, 1f);
        using var brush = new SolidBrush(fill);
        pevent.Graphics.FillPath(brush, path);

        using var pen = new Pen(BorderColor, 1f);
        pevent.Graphics.DrawPath(pen, path);

        if (HighlightColor.A > 0)
        {
            using var highlightBrush = new LinearGradientBrush(
                new Rectangle(0, 0, Width, Math.Max(1, Height / 2)),
                HighlightColor,
                Color.Transparent,
                LinearGradientMode.Vertical);
            pevent.Graphics.SetClip(path);
            pevent.Graphics.FillRectangle(highlightBrush, 0, 0, Width, Height / 2);
            pevent.Graphics.ResetClip();
        }

        var textBounds = new Rectangle(
            Padding.Left,
            0,
            Math.Max(1, ClientRectangle.Width - Padding.Horizontal),
            ClientRectangle.Height);
        var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis;
        flags |= TextAlign switch
        {
            ContentAlignment.TopLeft or ContentAlignment.MiddleLeft or ContentAlignment.BottomLeft => TextFormatFlags.Left,
            ContentAlignment.TopRight or ContentAlignment.MiddleRight or ContentAlignment.BottomRight => TextFormatFlags.Right,
            _ => TextFormatFlags.HorizontalCenter
        };

        TextRenderer.DrawText(
            pevent.Graphics,
            Text,
            Font,
            textBounds,
            Enabled ? ForeColor : Color.FromArgb(105, 105, 105),
            flags);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        using var path = CreatePath(ClientRectangle, Radius, 1f);
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
            Math.Max(1, bounds.Width - (inset * 2)),
            Math.Max(1, bounds.Height - (inset * 2)));
        var diameter = Math.Min(Math.Max(1, radius * 2), Math.Min(rect.Width, rect.Height));

        path.AddArc(rect.X, rect.Y, diameter, diameter, 180, 90);
        path.AddArc(rect.Right - diameter, rect.Y, diameter, diameter, 270, 90);
        path.AddArc(rect.Right - diameter, rect.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rect.X, rect.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}
