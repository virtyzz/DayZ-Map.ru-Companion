using System.Drawing;
using System.Runtime.InteropServices;

namespace CrosshairMarker;

internal sealed class GlobalMouseClickInterceptor : IDisposable
{
    private const int WhMouseLl = 14;
    private const int WmLButtonDown = 0x0201;
    private const int WmLButtonUp = 0x0202;
    private readonly Func<Point, bool> handleMouseDown;
    private readonly Func<Point, bool> handleMouseUp;
    private readonly HookProc callback;
    private readonly IntPtr hook;

    public GlobalMouseClickInterceptor(Func<Point, bool> handleMouseDown, Func<Point, bool> handleMouseUp)
    {
        this.handleMouseDown = handleMouseDown;
        this.handleMouseUp = handleMouseUp;
        callback = HookCallback;
        hook = SetWindowsHookEx(WhMouseLl, callback, GetModuleHandle(null), 0);
        if (hook == IntPtr.Zero)
        {
            AppRuntimeLog.Error($"Could not install Battle Pass mouse interceptor (Win32 error {Marshal.GetLastWin32Error()})");
        }
        else
        {
            AppRuntimeLog.Info("Battle Pass mouse interceptor installed.");
        }
    }

    private IntPtr HookCallback(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0 && (message.ToInt32() == WmLButtonDown || message.ToInt32() == WmLButtonUp))
        {
            var mouse = Marshal.PtrToStructure<MouseHookData>(data);
            var point = new Point(mouse.X, mouse.Y);
            var handled = message.ToInt32() == WmLButtonDown ? handleMouseDown(point) : handleMouseUp(point);
            if (handled) return new IntPtr(1);
        }
        return CallNextHookEx(hook, code, message, data);
    }

    public void Dispose()
    {
        if (hook != IntPtr.Zero) UnhookWindowsHookEx(hook);
    }

    private delegate IntPtr HookProc(int code, IntPtr message, IntPtr data);

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseHookData
    {
        public int X;
        public int Y;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc callback, IntPtr module, uint threadId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? moduleName);
    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
}
