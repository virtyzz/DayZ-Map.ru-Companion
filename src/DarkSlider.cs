using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace CrosshairMarker;

internal sealed class DarkSlider : Control
{
    private bool dragging;
    private int value;

    public event EventHandler? ValueChanged;

    public int Minimum { get; set; }
    public int Maximum { get; set; } = 100;
    public int TickFrequency { get; set; } = 10;
    public Color TrackColor { get; set; } = Color.FromArgb(52, 52, 52);
    public Color FillColor { get; set; } = Color.FromArgb(52, 152, 219);
    public Color TickColor { get; set; } = Color.FromArgb(120, 120, 120);
    public Color ThumbColor { get; set; } = Color.FromArgb(52, 152, 219);

    public int Value
    {
        get => value;
        set
        {
            var next = Math.Clamp(value, Minimum, Maximum);
            if (this.value == next)
            {
                return;
            }

            this.value = next;
            Invalidate();
            ValueChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public DarkSlider()
    {
        DoubleBuffered = true;
        Height = 38;
        Cursor = Cursors.Hand;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;

        var track = TrackBounds;
        using var trackPen = new Pen(TrackColor, 6) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        using var fillPen = new Pen(FillColor, 6) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        e.Graphics.DrawLine(trackPen, track.Left, track.Top, track.Right, track.Top);
        e.Graphics.DrawLine(fillPen, track.Left, track.Top, ThumbX, track.Top);

        var ticks = Math.Max(1, TickFrequency);
        using var tickPen = new Pen(TickColor, 1);
        for (var current = Minimum; current <= Maximum; current += ticks)
        {
            var x = ValueToX(current);
            e.Graphics.DrawLine(tickPen, x, track.Top + 12, x, track.Top + 16);
        }

        using var glowBrush = new SolidBrush(Color.FromArgb(60, FillColor));
        e.Graphics.FillEllipse(glowBrush, ThumbX - 8, track.Top - 8, 16, 16);
        using var thumbBrush = new SolidBrush(ThumbColor);
        using var thumbPen = new Pen(FillColor, 2);
        e.Graphics.FillEllipse(thumbBrush, ThumbX - 6, track.Top - 6, 12, 12);
        e.Graphics.DrawEllipse(thumbPen, ThumbX - 6, track.Top - 6, 12, 12);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        dragging = true;
        Capture = true;
        UpdateFromMouse(e.X);
        base.OnMouseDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (dragging)
        {
            UpdateFromMouse(e.X);
        }

        base.OnMouseMove(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        dragging = false;
        Capture = false;
        base.OnMouseUp(e);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        Value += e.Delta > 0 ? 1 : -1;
        base.OnMouseWheel(e);
    }

    private Rectangle TrackBounds => new(12, 16, Math.Max(1, Width - 24), 1);

    private float ThumbX => ValueToX(Value);

    private float ValueToX(int candidate)
    {
        var track = TrackBounds;
        if (Maximum <= Minimum)
        {
            return track.Left;
        }

        var progress = (candidate - Minimum) / (float)(Maximum - Minimum);
        return track.Left + (track.Width * progress);
    }

    private void UpdateFromMouse(int x)
    {
        var track = TrackBounds;
        var progress = Math.Clamp((x - track.Left) / (float)track.Width, 0f, 1f);
        Value = Minimum + (int)Math.Round((Maximum - Minimum) * progress);
    }
}
