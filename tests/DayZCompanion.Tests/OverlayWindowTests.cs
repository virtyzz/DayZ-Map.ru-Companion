using System.Diagnostics;
using System.Runtime.InteropServices;
using CrosshairMarker;

internal static class OverlayWindowTests
{
    public static void PerformanceVisibilityFollowsGame()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var game = new System.Windows.Forms.Form { Bounds = new System.Drawing.Rectangle(120, 120, 700, 450) };
                game.Show();
                var available = false;
                using var overlay = new PerformanceOverlayForm(() => available && game.Visible && game.WindowState != System.Windows.Forms.FormWindowState.Minimized
                    ? new GameWindowState(game.Handle, game.RectangleToScreen(game.ClientRectangle)) : null);
                var settings = new PerformanceSettings { Enabled = true };
                overlay.Apply(settings, new PerformanceSnapshot(Running: true));
                if (overlay.Visible) throw new Exception("Default overlay is visible outside the game");
                available = true;
                PumpUntil(() => overlay.Visible);
                using var other = new System.Windows.Forms.Form { Bounds = new System.Drawing.Rectangle(150, 150, 160, 100) };
                other.Show(); other.Activate();
                var foreground = GetForegroundWindow();
                PumpUntil(() => overlay.Visible && GetWindow(overlay.Handle, 2) == game.Handle);
                var until = Stopwatch.StartNew();
                while (until.ElapsedMilliseconds < 3300)
                {
                    System.Windows.Forms.Application.DoEvents(); Thread.Sleep(15);
                    if (!overlay.Visible || IsTopmost(overlay)) throw new Exception("Background overlay disappeared or became globally topmost");
                }
                if (GetForegroundWindow() != foreground) throw new Exception("Game attachment stole focus");
                if (GetWindow(overlay.Handle, 2) != game.Handle) throw new Exception("Overlay escaped game's Z order");
                game.Location = new System.Drawing.Point(180, 190);
                PumpUntil(() => overlay.Left == game.RectangleToScreen(game.ClientRectangle).Left + settings.Left);
                if (!game.RectangleToScreen(game.ClientRectangle).Contains(overlay.Bounds)) throw new Exception("Overlay extends beyond game client area");
                game.WindowState = System.Windows.Forms.FormWindowState.Minimized;
                PumpUntil(() => !overlay.Visible);
                game.WindowState = System.Windows.Forms.FormWindowState.Normal;
                PumpUntil(() => overlay.Visible);
                // Hidden/closed games hide it without a measurement update.
                available = false;
                PumpUntil(() => !overlay.Visible);
                settings.ShowOnlyInGame = false;
                overlay.Apply(settings, new PerformanceSnapshot(Running: false));
                if (!overlay.Visible) throw new Exception("Unchecked option hides overlay after game exits");
                settings.ShowOnlyInGame = true;
                overlay.Apply(settings, new PerformanceSnapshot(Running: false));
                if (overlay.Visible) throw new Exception("Checkbox change did not hide overlay immediately");
                settings.ShowOnlyInGame = false; settings.ShowOverlay = false;
                overlay.Apply(settings, new PerformanceSnapshot(Running: true));
                if (overlay.Visible) throw new Exception("Master show setting was ignored");
                settings.ShowOverlay = true; settings.Enabled = false;
                overlay.Apply(settings, new PerformanceSnapshot());
                if (overlay.Visible) throw new Exception("Disabled measurements show a normal overlay");
                settings.ShowOnlyInGame = true; settings.EditingEnabled = true;
                overlay.Apply(settings, new PerformanceSnapshot());
                if (!overlay.Visible) throw new Exception("Positioning preview is unavailable outside game");
                settings.EditingEnabled = false;
                overlay.Apply(settings, new PerformanceSnapshot());
                if (overlay.Visible) throw new Exception("Finishing positioning did not restore game-only policy");
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start(); thread.Join();
        if (failure is not null) throw new Exception("Performance visibility failed", failure);
    }

    public static void RecoverTopmost()
        => RunOnSta(Run, "Overlay recovery failed");

    public static void CrosshairAndQuestsFollowGame()
        => RunOnSta(TestCrosshairAndQuests, "DayZ-only visibility failed");

    private static void RunOnSta(Action action, string message)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw new Exception(message, failure);
    }

    private static void Run()
    {
        using var quests = new BattlePassOverlayForm();
        using var performance = new PerformanceOverlayForm();
        quests.Apply(new BattlePassSettings(), new BattlePassSnapshot());
        performance.Apply(new PerformanceSettings { ShowOverlay = true, Enabled = true, ShowOnlyInGame = false }, new PerformanceSnapshot(Running: true));
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
        quests.Apply(new BattlePassSettings { OverlayEditingEnabled = true }, new BattlePassSnapshot());
        performance.Apply(new PerformanceSettings { ShowOverlay = true, Enabled = true, EditingEnabled = true }, new PerformanceSnapshot(Running: true));
        foreach (var window in windows) SetWindowPos(window.Handle, new IntPtr(-2), 0, 0, 0, 0, 0x13);
        PumpUntil(() => windows.All(IsTopmost));

        quests.SuspendVisibility();
        performance.Apply(new PerformanceSettings { ShowOverlay = false }, new PerformanceSnapshot());
        var until = Stopwatch.StartNew();
        while (until.ElapsedMilliseconds < 3300)
        {
            System.Windows.Forms.Application.DoEvents();
            if (windows.Any(window => window.Visible)) throw new Exception("Watchdog resurrected a hidden overlay");
            Thread.Sleep(15);
        }
    }

    private static void TestCrosshairAndQuests()
    {
        using var game = new System.Windows.Forms.Form { Bounds = new System.Drawing.Rectangle(120, 120, 700, 450) };
        game.Show();
        var available = false;
        GameWindowState? FindGame() => available ? GameWindowActivity.GetVisibleWindowState(game.Handle) : null;
        using var crosshair = new OverlayForm(FindGame);
        using var quests = new BattlePassOverlayForm(FindGame);
        var config = new AppConfig { ShowOnlyInGame = true };
        var settings = new BattlePassSettings { ShowOnlyInGame = true };
        crosshair.ApplySettings(config.Clone());
        quests.Apply(settings.Clone(), new BattlePassSnapshot());
        if (crosshair.Visible || quests.Visible) throw new Exception("An overlay was visible without a game window");

        available = true;
        PumpUntil(() => crosshair.Visible && quests.Visible);
        using var other = new System.Windows.Forms.Form();
        other.Show(); other.Activate();
        var foreground = GetForegroundWindow();
        IntPtr Below(IntPtr window) => GetWindow(window, 2);
        PumpUntil(() => (Below(crosshair.Handle) == quests.Handle && Below(quests.Handle) == game.Handle)
            || (Below(quests.Handle) == crosshair.Handle && Below(crosshair.Handle) == game.Handle));
        var order = Below(quests.Handle);
        game.Location = new System.Drawing.Point(180, 190);
        var client = FindGame()!.ClientBounds;
        PumpUntil(() => crosshair.Left == client.Left + (client.Width - crosshair.Width) / 2
            && quests.Left == client.Left + settings.Left);
        if (Below(quests.Handle) != order || IsTopmost(crosshair) || IsTopmost(quests) || GetForegroundWindow() != foreground)
            throw new Exception("Game-bound overlays changed their order, became globally topmost or stole focus");

        quests.SuspendVisibility();
        quests.Apply(settings, new BattlePassSnapshot());
        if (quests.Visible) throw new Exception("Applying settings exposed the overlay during capture");
        quests.ResumeVisibility();
        if (!quests.Visible) throw new Exception("The quest overlay did not resume after capture");

        game.WindowState = System.Windows.Forms.FormWindowState.Minimized;
        PumpUntil(() => !crosshair.Visible && !quests.Visible);
        settings.OverlayEditingEnabled = true;
        quests.Apply(settings, new BattlePassSnapshot());
        if (!quests.Visible) throw new Exception("The quest overlay could not be positioned outside the game");
        settings.OverlayEditingEnabled = false;
        quests.Apply(settings, new BattlePassSnapshot());
        if (quests.Visible) throw new Exception("The quest overlay stayed visible after positioning ended");

        config.ShowOnlyInGame = false;
        settings.ShowOnlyInGame = false;
        crosshair.ApplySettings(config);
        quests.Apply(settings, new BattlePassSnapshot());
        if (!crosshair.Visible || !quests.Visible || !IsTopmost(crosshair) || !IsTopmost(quests))
            throw new Exception("Disabling the option did not restore desktop overlays");
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
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr window, uint command);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(IntPtr window, int index);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
}
