using System.Runtime.InteropServices;

namespace CrosshairMarker;

// TopMost alone does not reassert Z order when another topmost window covers us.
// Only maintain forms that are already visible: Hide() is also used during capture.
internal sealed class OverlayWindowWatchdog : IDisposable
{
    private readonly Form window;
    private readonly Action repaint;
    private readonly Func<bool> maintainTopmost;
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 3000 };
    private DateTime nextFailureLog;

    public OverlayWindowWatchdog(Form window, Action repaint, Func<bool>? maintainTopmost = null)
    {
        this.window = window;
        this.repaint = repaint;
        this.maintainTopmost = maintainTopmost ?? (() => true);
        window.VisibleChanged += OnVisibleChanged;
        timer.Tick += OnTick;
    }

    private void OnVisibleChanged(object? sender, EventArgs e)
    {
        timer.Enabled = window.Visible;
        if (window.Visible) Refresh();
    }

    private void OnTick(object? sender, EventArgs e) => Refresh();

    private void Refresh()
    {
        if (window.IsDisposed || !window.Visible || !window.IsHandleCreated || !maintainTopmost()) return;
        const uint noSize = 0x0001, noMove = 0x0002, noActivate = 0x0010, noOwnerZOrder = 0x0200;
        if (!SetWindowPos(window.Handle, new IntPtr(-1), 0, 0, 0, 0, noSize | noMove | noActivate | noOwnerZOrder)
            && DateTime.UtcNow >= nextFailureLog)
        {
            var error = Marshal.GetLastWin32Error();
            nextFailureLog = DateTime.UtcNow.AddMinutes(1);
            AppRuntimeLog.Info($"{window.GetType().Name} topmost refresh failed. Win32Error={error}.");
        }
        repaint();
    }

    public void Dispose()
    {
        timer.Stop();
        timer.Dispose();
        window.VisibleChanged -= OnVisibleChanged;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
}
