using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace CrosshairMarker;

internal sealed class PreviewPanel : Panel
{
    private CrosshairProfile profile = CrosshairProfile.Default();

    public PreviewPanel()
    {
        DoubleBuffered = true;
        BackColor = Color.FromArgb(14, 14, 16);
    }

    public void ApplyProfile(CrosshairProfile nextProfile)
    {
        profile = nextProfile.Clone();
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;

        using var backgroundBrush = new LinearGradientBrush(
            ClientRectangle,
            Color.FromArgb(18, 18, 21),
            Color.FromArgb(10, 10, 12),
            LinearGradientMode.ForwardDiagonal);
        e.Graphics.FillRectangle(backgroundBrush, ClientRectangle);

        using var gridPen = new Pen(Color.FromArgb(28, 72, 72, 78), 1);
        for (var x = 0; x < Width; x += 32)
        {
            e.Graphics.DrawLine(gridPen, x, 0, x, Height);
        }
        for (var y = 0; y < Height; y += 32)
        {
            e.Graphics.DrawLine(gridPen, 0, y, Width, y);
        }

        using var centerPen = new Pen(Color.FromArgb(70, 238, 177, 91), 1.2f)
        {
            DashStyle = DashStyle.Dash
        };
        e.Graphics.DrawLine(centerPen, Width / 2, 0, Width / 2, Height);
        e.Graphics.DrawLine(centerPen, 0, Height / 2, Width, Height / 2);

        using var bitmap = CrosshairRenderer.RenderBitmap(ClientSize, profile);
        e.Graphics.DrawImageUnscaled(bitmap, 0, 0);
    }
}
