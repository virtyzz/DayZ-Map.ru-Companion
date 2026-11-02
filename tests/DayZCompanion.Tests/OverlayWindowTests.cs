using System.Diagnostics;
using System.Runtime.InteropServices;
using CrosshairMarker;

internal static class OverlayWindowTests
{
    public static void RecoverTopmost()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { Run(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw new Exception("Overlay recovery failed", failure);
    }

    private static void Run()
    {
        using var quests = new BattlePassOverlayForm();
        using var performance = new PerformanceOverlayForm();
        quests.Apply(new BattlePassSettings(), new BattlePassSnapshot());
        quests.Show();
        performance.Apply(new PerformanceSettings { ShowOverlay = true, Enabled = true }, new PerformanceSnapshot(Running: true));
        var windows = new System.Windows.Forms.Form[] { quests, performance };
        var bounds = windows.Select(window => window.Bounds).ToArray();
        var foreground = GetForegroundWindow();
        foreach (var window in windows)
        {
            if (!SetWindowPos(window.Handle, new IntPtr(-2), 0, 0, 0, 0, 0x13))
                throw new Exception("Could not simulate loss of topmost");
            if (IsTopmost(window)) throw new Exception("Window was not demoted");
        }
        PumpUntil(() => windows.All(IsTopmost));
        if (GetForegroundWindow() != foreground) throw new Exception("Recovery changed foreground window");
        for (var i = 0; i < windows.Length; i++)
            if (windows[i].Bounds != bounds[i]) throw new Exception("Recovery changed placement");

        // Editing recreates the native handles; recovery must still target the new ones.
        quests.SetEditing(true);
        performance.Apply(new PerformanceSettings { ShowOverlay = true, Enabled = true, EditingEnabled = true }, new PerformanceSnapshot(Running: true));
        foreach (var window in windows) SetWindowPos(window.Handle, new IntPtr(-2), 0, 0, 0, 0, 0x13);
        PumpUntil(() => windows.All(IsTopmost));

        quests.Hide(); // OCR temporarily hides this form without changing the saved visibility setting.
        performance.Apply(new PerformanceSettings { ShowOverlay = false }, new PerformanceSnapshot());
        var until = Stopwatch.StartNew();
        while (until.ElapsedMilliseconds < 3300)
        {
            System.Windows.Forms.Application.DoEvents();
            if (windows.Any(window => window.Visible)) throw new Exception("Watchdog resurrected a hidden overlay");
            Thread.Sleep(15);
        }
    }

    private static bool IsTopmost(System.Windows.Forms.Form window) => (GetWindowLong(window.Handle, -20) & 8) != 0;

    private static void PumpUntil(Func<bool> condition)
    {
        var elapsed = Stopwatch.StartNew();
        while (!condition())
        {
            if (elapsed.ElapsedMilliseconds > 6000) throw new Exception("Overlay did not recover within 6 seconds");
            System.Windows.Forms.Application.DoEvents();
            Thread.Sleep(15);
        }
    }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(IntPtr window, int index);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
}
