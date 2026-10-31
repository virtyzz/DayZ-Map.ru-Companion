namespace CrosshairMarker;

internal sealed class PerformanceOverlayForm : Form
{
    private PerformanceSnapshot snapshot = new();
    private PerformanceSettings settings = new();
    private bool adjusting;
    private bool rendering;
    public event Action<PerformanceOverlayPlacement>? PlacementChanged;

    public PerformanceOverlayForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        MinimumSize = new Size(120, 32);
        MaximumSize = new Size(1200, 500);
        DoubleBuffered = true;
        ResizeRedraw = true;
    }

    protected override bool ShowWithoutActivation => true;
    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.ExStyle |= 0x08000000 | 0x00080000;
            if (!ShowInTaskbar) parameters.ExStyle |= 0x00000080;
            if (!settings.EditingEnabled) parameters.ExStyle |= 0x00000020;
            return parameters;
        }
    }

    public void Apply(PerformanceSettings next, PerformanceSnapshot value)
    {
        var modeChanged = settings.EditingEnabled != next.EditingEnabled;
        settings = next.Clone();
        settings.Normalize();
        snapshot = value;
        if (modeChanged && IsHandleCreated) RecreateHandle();
        if (!adjusting)
        {
            var area = MonitorInfo.ResolveScreen(settings.MonitorDeviceName).Bounds;
            Bounds = settings.GetBounds(area);
        }
        if (settings.ShowOverlay && (settings.EditingEnabled || (settings.Enabled && value.Running)))
        {
            if (!Visible) Show();
            RenderOverlay();
        }
        else Hide();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        RenderOverlay();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        RenderOverlay();
    }

    private void RenderOverlay()
    {
        if (rendering || !Visible || !IsHandleCreated || ClientSize.Width <= 0 || ClientSize.Height <= 0) return;
        rendering = true;
        try
        {
            using var bitmap = PerformanceOverlayRenderer.Render(ClientSize, settings, snapshot);
            LayeredWindowBitmap.Apply(this, bitmap);
        }
        catch (Exception ex) { AppRuntimeLog.Error("Could not render performance overlay", ex); }
        finally { rendering = false; }
    }

    protected override void WndProc(ref Message message)
    {
        const int wmNcHitTest = 0x84, wmMouseActivate = 0x21, wmEnterSizeMove = 0x231, wmExitSizeMove = 0x232;
        if (message.Msg == wmMouseActivate) { message.Result = new IntPtr(3); return; }
        if (message.Msg == wmNcHitTest && settings.EditingEnabled)
        {
            var point = PointToClient(new Point(unchecked((short)(message.LParam.ToInt64() & 0xffff)), unchecked((short)((message.LParam.ToInt64() >> 16) & 0xffff))));
            var left = point.X < 7; var right = point.X >= Width - 7;
            var top = point.Y < 7; var bottom = point.Y >= Height - 7;
            // Native hit-test codes provide dragging and resizing on all edges.
            var hit = top ? (left ? 13 : right ? 14 : 12) : bottom ? (left ? 16 : right ? 17 : 15) : left ? 10 : right ? 11 : 2;
            message.Result = new IntPtr(hit);
            return;
        }
        if (message.Msg == wmEnterSizeMove) adjusting = true;
        base.WndProc(ref message);
        if (message.Msg == wmExitSizeMove && adjusting)
        {
            adjusting = false;
            SavePlacement();
        }
    }

    private void SavePlacement()
    {
        var screen = Screen.FromRectangle(Bounds);
        var area = screen.Bounds;
        settings.MonitorDeviceName = screen.DeviceName;
        settings.Left = Math.Max(0, Left - area.Left);
        settings.Top = Math.Max(0, Top - area.Top);
        settings.Width = Width;
        settings.Height = Height;
        settings.Normalize();
        Bounds = settings.GetBounds(area);
        settings.Left = Left - area.Left;
        settings.Top = Top - area.Top;
        PlacementChanged?.Invoke(new(screen.DeviceName, settings.Left, settings.Top, Width, Height));
    }
}
