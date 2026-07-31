using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace CrosshairMarker;

internal sealed class HotkeyManager : NativeWindow, IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private readonly Dictionary<int, Action> actions = [];
    private int nextId = 1;
    private bool disposed;

    public HotkeyManager()
    {
        CreateHandle(new CreateParams());
    }

    public bool Register(Keys key, HotkeyModifiers modifiers, Action action)
    {
        var id = nextId++;
        if (!NativeMethods.RegisterHotKey(Handle, id, (uint)modifiers, (uint)key))
        {
            return false;
        }

        actions[id] = action;
        return true;
    }

    public void UnregisterAll()
    {
        foreach (var id in actions.Keys.ToList())
        {
            NativeMethods.UnregisterHotKey(Handle, id);
        }
        actions.Clear();
        nextId = 1;
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_HOTKEY && actions.TryGetValue(m.WParam.ToInt32(), out var action))
        {
            action();
            return;
        }

        base.WndProc(ref m);
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        UnregisterAll();
        DestroyHandle();
        disposed = true;
    }

    private static class NativeMethods
    {
        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    }
}

[Flags]
internal enum HotkeyModifiers : uint
{
    None = 0,
    Alt = 0x0001,
    Control = 0x0002,
    Shift = 0x0004,
    Win = 0x0008,
    NoRepeat = 0x4000
}
