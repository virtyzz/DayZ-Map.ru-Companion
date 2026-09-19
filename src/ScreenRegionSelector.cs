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
    private readonly Action<Rectangle>? complete;
    private readonly Action? cancel;

    private ScreenRegionSelector(Bitmap screenshot, Rectangle displayBounds, Action<Rectangle>? complete = null, Action? cancel = null)
    {
        if (screenshot.Width != displayBounds.Width || screenshot.Height != displayBounds.Height)
            throw new ArgumentException("The screenshot must match the displayed bounds.", nameof(screenshot));

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        DoubleBuffered = true;
        AutoScaleMode = AutoScaleMode.None;
        StartPosition = FormStartPosition.Manual;
        Cursor = Cursors.Cross;
        // A WinForms top-level window has one DPI context. Spanning the whole
        // virtual desktop therefore makes its client coordinates disagree with
        // a physical-pixel screenshot when monitors have different scale
        // factors (and is especially visible for monitors left of the primary
        // display, whose X coordinate is negative). Keep every selector on the
        // one display that was captured instead.
        Bounds = displayBounds;
        KeyPreview = true;
        BackgroundImage = CreatePreview(screenshot);
        BackgroundImageLayout = ImageLayout.None;
        this.complete = complete;
        this.cancel = cancel;
    }

    public static Rectangle? SelectRegion(Bitmap screenshot)
    {
        var virtualBounds = SystemInformation.VirtualScreen;
        if (screenshot.Width != virtualBounds.Width || screenshot.Height != virtualBounds.Height)
            throw new ArgumentException("The screenshot must match the virtual screen.", nameof(screenshot));

        return new MultiMonitorSelector(screenshot, virtualBounds).Select();
    }

    public static Rectangle? SelectRegion(Bitmap screenshot, Rectangle displayBounds)
    {
        using var selector = new ScreenRegionSelector(screenshot, displayBounds);
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
        if (complete is not null)
        {
            complete(Selection);
            return;
        }
        DialogResult = DialogResult.OK;
        Close();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode != Keys.Escape) return;
        if (cancel is not null)
        {
            cancel();
            return;
        }
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

    private sealed class MultiMonitorSelector
    {
        private readonly Bitmap screenshot;
        private readonly Rectangle virtualBounds;
        private readonly List<ScreenRegionSelector> selectors = [];
        private Rectangle? selection;
        private bool finished;

        public MultiMonitorSelector(Bitmap screenshot, Rectangle virtualBounds)
        {
            this.screenshot = screenshot;
            this.virtualBounds = virtualBounds;
        }

        public Rectangle? Select()
        {
            foreach (var screen in Screen.AllScreens)
            {
                var bounds = Rectangle.Intersect(screen.Bounds, virtualBounds);
                if (bounds.Width <= 0 || bounds.Height <= 0) continue;

                var source = new Rectangle(bounds.Left - virtualBounds.Left, bounds.Top - virtualBounds.Top, bounds.Width, bounds.Height);
                using var displayShot = screenshot.Clone(source, PixelFormat.Format32bppArgb);
                selectors.Add(new ScreenRegionSelector(displayShot, bounds, Complete, Cancel));
            }

            if (selectors.Count == 0) return null;

            var primary = selectors[0];
            primary.Shown += (_, _) =>
            {
                foreach (var selector in selectors.Skip(1)) selector.Show(primary);
            };
            primary.FormClosed += (_, _) => Cancel();
            primary.ShowDialog();
            return selection;
        }

        private void Complete(Rectangle value)
        {
            if (finished) return;
            finished = true;
            selection = value;
            CloseAll(DialogResult.OK);
        }

        private void Cancel()
        {
            if (finished) return;
            finished = true;
            CloseAll(DialogResult.Cancel);
        }

        private void CloseAll(DialogResult result)
        {
            foreach (var selector in selectors.Where(selector => !selector.IsDisposed && selector.Visible).ToArray())
            {
                if (selector == selectors[0]) selector.DialogResult = result;
                selector.Close();
            }
        }
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
