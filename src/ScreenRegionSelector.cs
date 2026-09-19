using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace CrosshairMarker;

internal sealed class ScreenRegionSelector : Form
{
    private Point start;
    private Point current;
    private bool selecting;

    private ScreenRegionSelector(Bitmap screenshot)
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        DoubleBuffered = true;
        AutoScaleMode = AutoScaleMode.None;
        Cursor = Cursors.Cross;
        Bounds = SystemInformation.VirtualScreen;
        KeyPreview = true;
        BackgroundImage = CreatePreview(screenshot);
        BackgroundImageLayout = ImageLayout.None;
    }

    public static Rectangle? SelectRegion(Bitmap screenshot)
    {
        using var selector = new ScreenRegionSelector(screenshot);
        return selector.ShowDialog() == DialogResult.OK ? selector.Selection : null;
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);

        // DayZ clips and recentres the Windows pointer while it owns focus. A
        // global hotkey does not by itself transfer focus away from the game,
        // so explicitly make this selector the foreground window before the
        // user begins dragging.
        ReleaseCursorClip();
        ActivateSelector();
        BeginInvoke(ActivateSelector);
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
        InvalidateSelection(SelectionRectangle);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (!selecting) return;
        var previous = SelectionRectangle;
        current = e.Location;
        InvalidateSelection(Rectangle.Union(previous, SelectionRectangle));
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (!selecting || e.Button != MouseButtons.Left) return;
        current = e.Location;
        selecting = false;
        if (Selection.Width < 12 || Selection.Height < 12)
        {
            InvalidateSelection(SelectionRectangle);
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
        var rectangle = SelectionRectangle;
        using var clear = new SolidBrush(Color.FromArgb(80, Color.White));
        using var pen = new Pen(Color.FromArgb(255, 255, 180, 70), 2);
        e.Graphics.FillRectangle(clear, rectangle);
        e.Graphics.DrawRectangle(pen, rectangle);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) BackgroundImage?.Dispose();
        base.Dispose(disposing);
    }

    private Rectangle SelectionRectangle => new(Math.Min(start.X, current.X), Math.Min(start.Y, current.Y), Math.Abs(start.X - current.X), Math.Abs(start.Y - current.Y));

    private void InvalidateSelection(Rectangle rectangle)
    {
        rectangle.Inflate(4, 4);
        Invalidate(rectangle);
    }

    private static Bitmap CreatePreview(Bitmap screenshot)
    {
        var preview = new Bitmap(screenshot.Width, screenshot.Height, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(preview);
        graphics.DrawImageUnscaled(screenshot, Point.Empty);
        using var shade = new SolidBrush(Color.FromArgb(72, Color.Black));
        graphics.FillRectangle(shade, new Rectangle(Point.Empty, preview.Size));
        return preview;
    }

    private void ActivateSelector()
    {
        if (IsDisposed || !IsHandleCreated) return;
        ShowWindow(Handle, SwRestore);
        BringWindowToTop(Handle);
        SetForegroundWindow(Handle);
        Activate();
        Focus();
    }

    private static void ReleaseCursorClip()
    {
        // A null RECT removes a ClipCursor rectangle. It is intentionally only
        // done while the user has explicitly opened the capture selector.
        ClipCursor(IntPtr.Zero);
    }

    private const int SwRestore = 9;

    [DllImport("user32.dll")]
    private static extern bool ClipCursor(IntPtr rectangle);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr window, int command);
}
