using System.Drawing;
using System.Windows.Forms;

namespace CrosshairMarker;

internal sealed class ScreenRegionSelector : Form
{
    private Point start;
    private Point current;
    private bool selecting;

    private ScreenRegionSelector()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        DoubleBuffered = true;
        Cursor = Cursors.Cross;
        BackColor = Color.Black;
        Opacity = .28;
        Bounds = SystemInformation.VirtualScreen;
        KeyPreview = true;
    }

    public static Rectangle? SelectRegion()
    {
        using var selector = new ScreenRegionSelector();
        return selector.ShowDialog() == DialogResult.OK ? selector.Selection : null;
    }

    private Rectangle Selection
    {
        get
        {
            var left = Math.Min(start.X, current.X);
            var top = Math.Min(start.Y, current.Y);
            return new Rectangle(left + Left, top + Top, Math.Abs(start.X - current.X), Math.Abs(start.Y - current.Y));
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        start = current = e.Location;
        selecting = true;
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (!selecting) return;
        current = e.Location;
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (!selecting || e.Button != MouseButtons.Left) return;
        current = e.Location;
        selecting = false;
        if (Selection.Width < 12 || Selection.Height < 12)
        {
            Invalidate();
            return;
        }
        DialogResult = DialogResult.OK;
        Close();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode != Keys.Escape) return;
        DialogResult = DialogResult.Cancel;
        Close();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (!selecting) return;
        var rectangle = new Rectangle(Math.Min(start.X, current.X), Math.Min(start.Y, current.Y), Math.Abs(start.X - current.X), Math.Abs(start.Y - current.Y));
        using var clear = new SolidBrush(Color.FromArgb(80, Color.White));
        using var pen = new Pen(Color.FromArgb(255, 255, 180, 70), 2);
        e.Graphics.FillRectangle(clear, rectangle);
        e.Graphics.DrawRectangle(pen, rectangle);
    }
}
