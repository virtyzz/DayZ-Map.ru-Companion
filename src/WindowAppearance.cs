using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace CrosshairMarker;

internal static class WindowAppearance
{
    private const int ImmersiveDarkModeBefore20H1 = 19;
    private const int ImmersiveDarkMode = 20;
    private const int CaptionColor = 35;
    private const int TextColor = 36;
    private const int GetMinMaxInfo = 0x0024;
    private const int CalculateNonClientSize = 0x0083;
    private const int NonClientLeftButtonDown = 0x00A1;
    private const int HitTop = 12;

    internal static void ApplyDarkCaption(IntPtr window, Color? background = null)
    {
        var enabled = 1;
        if (DwmSetWindowAttribute(window, ImmersiveDarkMode, ref enabled, sizeof(int)) != 0)
            _ = DwmSetWindowAttribute(window, ImmersiveDarkModeBefore20H1, ref enabled, sizeof(int));

        if (background is not { } color || !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)) return;
        var caption = ColorTranslator.ToWin32(color);
        var text = ColorTranslator.ToWin32(Color.FromArgb(242, 245, 246));
        _ = DwmSetWindowAttribute(window, CaptionColor, ref caption, sizeof(int));
        _ = DwmSetWindowAttribute(window, TextColor, ref text, sizeof(int));
    }

    internal static Rectangle? GetProposedWindowBounds(Message message)
    {
        // RECT is also the first member of NCCALCSIZE_PARAMS when wParam is true.
        return message.Msg == CalculateNonClientSize && message.LParam != IntPtr.Zero
            ? Marshal.PtrToStructure<NativeRectangle>(message.LParam).ToRectangle()
            : null;
    }

    internal static void SetMaximizedBounds(ref Message message)
    {
        if (message.Msg != GetMinMaxInfo || message.LParam == IntPtr.Zero) return;

        // Limit the outer window. Clipping just the content leaves the system frame over the taskbar.
        var screen = Screen.FromHandle(message.HWnd);
        var work = screen.WorkingArea;
        var limits = Marshal.PtrToStructure<NativeMinMaxInfo>(message.LParam);
        limits.MaxPosition = new Point(work.Left - screen.Bounds.Left, work.Top - screen.Bounds.Top);
        limits.MaxSize = new Point(work.Width, work.Height);
        Marshal.StructureToPtr(limits, message.LParam, false);
        message.Result = IntPtr.Zero;
    }

    internal static void AdjustCaptionClient(ref Message message, Rectangle proposedWindow)
    {
        var bounds = Marshal.PtrToStructure<NativeRectangle>(message.LParam).ToRectangle();
        // Keep the outer outline. The client draws the sidebar right up to it.
        var adjusted = IsZoomed(message.HWnd)
            ? Rectangle.Intersect(proposedWindow, Screen.FromRectangle(proposedWindow).WorkingArea)
            : Rectangle.FromLTRB(bounds.Left, Math.Min(bounds.Top, proposedWindow.Top + 1), bounds.Right, bounds.Bottom);
        if (adjusted.IsEmpty || adjusted == bounds) return;
        Marshal.StructureToPtr(new NativeRectangle
        {
            Left = adjusted.Left,
            Top = adjusted.Top,
            Right = adjusted.Right,
            Bottom = adjusted.Bottom
        }, message.LParam, false);
        message.Result = IntPtr.Zero;
    }

    internal static void BeginTopResize(IntPtr window, Point cursor)
    {
        var screenPoint = (IntPtr)(((cursor.Y & 0xffff) << 16) | (cursor.X & 0xffff));
        _ = ReleaseCapture();
        _ = PostMessageW(window, NonClientLeftButtonDown, (IntPtr)HitTop, screenPoint);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRectangle
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public readonly Rectangle ToRectangle() => Rectangle.FromLTRB(Left, Top, Right, Bottom);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMinMaxInfo
    {
        public Point Reserved;
        public Point MaxSize;
        public Point MaxPosition;
        public Point MinTrackSize;
        public Point MaxTrackSize;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsZoomed(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessageW(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int valueSize);
}
