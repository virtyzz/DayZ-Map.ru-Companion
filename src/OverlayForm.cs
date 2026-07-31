using System.Drawing;
using System.Drawing.Drawing2D;
using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace CrosshairMarker;

internal sealed class OverlayForm : Form
{
    private const int WS_EX_LAYERED = 0x00080000;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    private readonly System.Windows.Forms.Timer watchdogTimer = new();
    private CrosshairProfile profile = CrosshairProfile.Default();
    private string? targetMonitorDeviceName;
    private OverlayWindowSize windowSize = OverlayWindowSize.Compact200;
    private DateTime lastWatchdogLogUtc = DateTime.MinValue;

    public OverlayForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        Bounds = Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1920, 1080);
        ResizeRedraw = true;
        watchdogTimer.Interval = 15000;
        watchdogTimer.Tick += OnWatchdogTick;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
            return cp;
        }
    }

    public void ApplyProfile(CrosshairProfile nextProfile)
    {
        profile = nextProfile.Clone();
        if (IsHandleCreated)
        {
            UpdateLayeredBitmap();
        }
    }

    public void ApplyMonitor(string? deviceName)
    {
        targetMonitorDeviceName = deviceName;
        if (IsHandleCreated)
        {
            PinToDesktop();
            UpdateLayeredBitmap();
        }
    }

    public void ApplyWindowSize(OverlayWindowSize nextWindowSize)
    {
        windowSize = nextWindowSize;
        if (IsHandleCreated)
        {
            PinToDesktop();
            UpdateLayeredBitmap();
        }
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        watchdogTimer.Start();
        AppRuntimeLog.Info("Overlay shown.");
        PinToDesktop();
        UpdateLayeredBitmap();
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (Visible)
        {
            watchdogTimer.Start();
        }
        else
        {
            watchdogTimer.Stop();
            AppRuntimeLog.Info("Overlay hidden.");
        }
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (IsHandleCreated)
        {
            UpdateLayeredBitmap();
        }
    }

    private void UpdateLayeredBitmap()
    {
        if (ClientSize.Width <= 0 || ClientSize.Height <= 0)
        {
            AppRuntimeLog.Info($"Skipped overlay render because client size is {ClientSize.Width}x{ClientSize.Height}.");
            return;
        }

        try
        {
            var screen = MonitorInfo.ResolveScreen(targetMonitorDeviceName);
            using var bitmap = CrosshairRenderer.RenderBitmap(ClientSize, profile);
            ClearOutsideWorkingArea(bitmap, screen);
            ApplyLayeredBitmap(bitmap);
        }
        catch (Exception ex)
        {
            AppRuntimeLog.Error("Overlay render failed", ex);
        }
    }

    private static void ClearOutsideWorkingArea(Bitmap bitmap, Screen screen)
    {
        var bounds = screen.Bounds;
        var workingArea = screen.WorkingArea;
        if (workingArea == bounds)
        {
            return;
        }

        using var graphics = Graphics.FromImage(bitmap);
        graphics.CompositingMode = CompositingMode.SourceCopy;
        using var transparent = new SolidBrush(Color.Transparent);

        if (workingArea.Top > bounds.Top)
        {
            graphics.FillRectangle(
                transparent,
                0,
                0,
                bitmap.Width,
                workingArea.Top - bounds.Top);
        }

        if (workingArea.Bottom < bounds.Bottom)
        {
            graphics.FillRectangle(
                transparent,
                0,
                workingArea.Bottom - bounds.Top,
                bitmap.Width,
                bounds.Bottom - workingArea.Bottom);
        }

        if (workingArea.Left > bounds.Left)
        {
            graphics.FillRectangle(
                transparent,
                0,
                0,
                workingArea.Left - bounds.Left,
                bitmap.Height);
        }

        if (workingArea.Right < bounds.Right)
        {
            graphics.FillRectangle(
                transparent,
                workingArea.Right - bounds.Left,
                0,
                bounds.Right - workingArea.Right,
                bitmap.Height);
        }
    }

    private void PinToDesktop()
    {
        var screen = MonitorInfo.ResolveScreen(targetMonitorDeviceName);
        Bounds = GetOverlayBounds(screen);
        SetOverlayTopmost();
    }

    private Rectangle GetOverlayBounds(Screen screen)
    {
        var bounds = screen.Bounds;
        var scale = windowSize switch
        {
            OverlayWindowSize.QuarterScreen => 0.25,
            OverlayWindowSize.HalfScreen => 0.5,
            OverlayWindowSize.ThreeQuartersScreen => 0.75,
            OverlayWindowSize.FullScreen => 1.0,
            _ => 0.0
        };

        if (scale == 0.0)
        {
            const int compactSize = 200;
            return new Rectangle(
                bounds.Left + (bounds.Width - compactSize) / 2,
                bounds.Top + (bounds.Height - compactSize) / 2,
                compactSize,
                compactSize);
        }

        var width = Math.Max(1, (int)Math.Round(bounds.Width * scale));
        var height = Math.Max(1, (int)Math.Round(bounds.Height * scale));
        return new Rectangle(
            bounds.Left + (bounds.Width - width) / 2,
            bounds.Top + (bounds.Height - height) / 2,
            width,
            height);
    }

    private void SetOverlayTopmost()
    {
        if (!NativeMethods.SetWindowPos(
            Handle,
            NativeMethods.HWND_TOPMOST,
            Left,
            Top,
            Width,
            Height,
            NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW))
        {
            AppRuntimeLog.Info($"SetWindowPos failed. Win32Error={Marshal.GetLastWin32Error()}.");
        }
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        if (IsHandleCreated)
        {
            AppRuntimeLog.Info("Display settings changed; refreshing overlay.");
            PinToDesktop();
            UpdateLayeredBitmap();
        }
    }

    private void OnWatchdogTick(object? sender, EventArgs e)
    {
        if (!Visible || !IsHandleCreated)
        {
            return;
        }

        var expectedBounds = MonitorInfo.ResolveScreen(targetMonitorDeviceName).Bounds;
        if (Bounds != expectedBounds)
        {
            AppRuntimeLog.Info($"Overlay bounds drifted from {Bounds} to {expectedBounds}; correcting.");
        }
        else if ((DateTime.UtcNow - lastWatchdogLogUtc).TotalMinutes >= 5)
        {
            lastWatchdogLogUtc = DateTime.UtcNow;
            AppRuntimeLog.Info($"Overlay watchdog refresh. Bounds={Bounds}, profile={profile.Name}.");
        }

        PinToDesktop();
        UpdateLayeredBitmap();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            watchdogTimer.Stop();
            watchdogTimer.Dispose();
            SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        }
        base.Dispose(disposing);
    }

    private void ApplyLayeredBitmap(Bitmap bitmap)
    {
        var screenDc = NativeMethods.GetDC(IntPtr.Zero);
        var memoryDc = NativeMethods.CreateCompatibleDC(screenDc);
        var bitmapHandle = bitmap.GetHbitmap(Color.FromArgb(0));
        var oldBitmap = NativeMethods.SelectObject(memoryDc, bitmapHandle);

        try
        {
            var top = new NativeMethods.Point(Left, Top);
            var size = new NativeMethods.Size(bitmap.Width, bitmap.Height);
            var source = new NativeMethods.Point(0, 0);
            var blend = new NativeMethods.BlendFunction
            {
                BlendOp = NativeMethods.AC_SRC_OVER,
                BlendFlags = 0,
                SourceConstantAlpha = 255,
                AlphaFormat = NativeMethods.AC_SRC_ALPHA
            };

            if (!NativeMethods.UpdateLayeredWindow(
                Handle,
                screenDc,
                ref top,
                ref size,
                memoryDc,
                ref source,
                0,
                ref blend,
                NativeMethods.ULW_ALPHA))
            {
                AppRuntimeLog.Info($"UpdateLayeredWindow failed. Win32Error={Marshal.GetLastWin32Error()}.");
            }
        }
        finally
        {
            NativeMethods.SelectObject(memoryDc, oldBitmap);
            NativeMethods.DeleteObject(bitmapHandle);
            NativeMethods.DeleteDC(memoryDc);
            NativeMethods.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    private static class NativeMethods
    {
        public static readonly IntPtr HWND_TOPMOST = new(-1);
        public const uint SWP_NOACTIVATE = 0x0010;
        public const uint SWP_SHOWWINDOW = 0x0040;
        public const byte AC_SRC_OVER = 0x00;
        public const byte AC_SRC_ALPHA = 0x01;
        public const int ULW_ALPHA = 0x00000002;

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool SetWindowPos(
            IntPtr hWnd,
            IntPtr hWndInsertAfter,
            int x,
            int y,
            int cx,
            int cy,
            uint uFlags);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool UpdateLayeredWindow(
            IntPtr hwnd,
            IntPtr hdcDst,
            ref Point pptDst,
            ref Size psize,
            IntPtr hdcSrc,
            ref Point pptSrc,
            int crKey,
            ref BlendFunction pblend,
            int dwFlags);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr GetDC(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDc);

        [DllImport("gdi32.dll", SetLastError = true)]
        public static extern IntPtr CreateCompatibleDC(IntPtr hDc);

        [DllImport("gdi32.dll", SetLastError = true)]
        public static extern bool DeleteDC(IntPtr hdc);

        [DllImport("gdi32.dll", SetLastError = true)]
        public static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);

        [DllImport("gdi32.dll", SetLastError = true)]
        public static extern bool DeleteObject(IntPtr hObject);

        [StructLayout(LayoutKind.Sequential)]
        public struct Point
        {
            public int X;
            public int Y;

            public Point(int x, int y)
            {
                X = x;
                Y = y;
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct Size
        {
            public int Cx;
            public int Cy;

            public Size(int cx, int cy)
            {
                Cx = cx;
                Cy = cy;
            }
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct BlendFunction
        {
            public byte BlendOp;
            public byte BlendFlags;
            public byte SourceConstantAlpha;
            public byte AlphaFormat;
        }
    }
}
