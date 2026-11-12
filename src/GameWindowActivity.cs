using System.Diagnostics;
using System.Runtime.InteropServices;

namespace CrosshairMarker;

internal sealed record GameWindowState(IntPtr Handle, Rectangle ClientBounds);

internal static class GameWindowActivity
{
    private static IntPtr cachedWindow;
    private static uint cachedPid;

    public static GameWindowState? GetVisibleDayZWindow()
    {
        GetWindowThreadProcessId(cachedWindow, out var pid);
        if (cachedWindow == IntPtr.Zero || !IsWindow(cachedWindow) || pid != cachedPid)
        {
            cachedWindow = IntPtr.Zero;
            foreach (var process in Process.GetProcessesByName("DayZ_x64"))
            {
                using (process)
                {
                    try
                    {
                        var window = process.MainWindowHandle;
                        if (window == IntPtr.Zero) continue;
                        cachedWindow = window;
                        cachedPid = (uint)process.Id;
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                }
            }
        }
        return GetVisibleWindowState(cachedWindow);
    }

    internal static GameWindowState? GetVisibleWindowState(IntPtr handle)
    {
        if (handle == IntPtr.Zero || !IsWindowVisible(handle) || IsIconic(handle)) return null;
        // Windows on another virtual desktop can still have WS_VISIBLE.
        if (DwmGetWindowAttribute(handle, 14, out var cloaked, sizeof(int)) == 0 && cloaked != 0) return null;
        if (!GetClientRect(handle, out var rect)) return null;
        var origin = new Point(rect.Left, rect.Top);
        if (!ClientToScreen(handle, ref origin)) return null;
        var bounds = new Rectangle(origin.X, origin.Y, rect.Right - rect.Left, rect.Bottom - rect.Top);
        return bounds.Width >= 120 && bounds.Height >= 32 ? new(handle, bounds) : null;
    }

    // Keep our window directly above DayZ, below windows that cover the game.
    // Do not set a foreign owner: its destruction would destroy our HWND too.
    public static bool PlaceAboveGame(Form overlay, GameWindowState game)
    {
        if (!IsWindow(game.Handle)) return false;
        var gameTopmost = IsTopmost(game.Handle);
        if (overlay.TopMost != gameTopmost) overlay.TopMost = gameTopmost;
        var previous = GetWindow(game.Handle, 3); // GW_HWNDPREV
        if (previous == overlay.Handle) return true;
        if (previous != IntPtr.Zero && !gameTopmost && (GetWindowLongPtr(previous, -20).ToInt64() & 8) != 0)
            previous = IntPtr.Zero; // HWND_TOP preserves the non-topmost band.
        return SetWindowPos(overlay.Handle, previous, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010 | 0x0200);
    }

    public static bool IsTopmost(IntPtr window) => (GetWindowLongPtr(window, -20).ToInt64() & 8) != 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClientToScreen(IntPtr window, ref Point point);
    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out int value, int size);
    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr window, uint command);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr64(IntPtr window, int index);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong32(IntPtr window, int index);
    private static IntPtr GetWindowLongPtr(IntPtr window, int index) => IntPtr.Size == 8 ? GetWindowLongPtr64(window, index) : new(GetWindowLong32(window, index));
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
}
