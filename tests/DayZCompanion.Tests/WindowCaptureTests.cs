using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Forms;
using CrosshairMarker;

internal static class WindowCaptureTests
{
    public static void RegionGeometryAndMigration()
    {
        var desktop = new Rectangle(-1920, 0, 3840, 1080);
        var client = new Rectangle(-1600, 100, 1280, 720);
        var settings = new PlayerPositionTrackingSettings();
        settings.SetScreenRectangle(new Rectangle(-1500, 150, 200, 40), desktop);
        settings.AttachRegionToWindow(client, desktop);
        Check(settings.RegionRelativeToWindow, "Legacy selection was not attached to game window");
        Check(settings.GetScreenRectangle(new Rectangle(0, 0, 1280, 720)) == new Rectangle(100, 50, 200, 40), "Window offset is incorrect");
        Check(settings.GetScreenRectangle(new Rectangle(0, 0, 2560, 1440)) == new Rectangle(200, 100, 400, 80), "Selection did not scale on resize");
        var saved = JsonSerializer.Deserialize<PlayerPositionTrackingSettings>(JsonSerializer.Serialize(settings))!;
        saved.AttachRegionToWindow(new Rectangle(500, 300, 1280, 720), desktop);
        Check(saved.HasSameRegion(settings), "Moving the game or reloading settings changed its relative region");
        var outside = new PlayerPositionTrackingSettings();
        outside.SetScreenRectangle(new Rectangle(1000, 50, 200, 50), desktop);
        try { outside.AttachRegionToWindow(client, desktop); throw new Exception("Unrelated desktop region accepted"); }
        catch (DayZCompanionException) { }
        var preview = ScreenRegionSelector.GetImagePreviewBounds(new Size(2560, 1440), new Rectangle(-1920, 0, 1920, 1080));
        Check(ScreenRegionSelector.GetImageSelection(new Rectangle(preview.Left + 480, preview.Top + 270, 480, 270), preview, new Size(2560, 1440))
            == new Rectangle(640, 360, 640, 360), "Preview selection is not mapped back to original image pixels");
        var crop = WindowGraphicsCapture.GetSurfaceRegion(new Rectangle(100, 200, 50, 40), new Rectangle(-990, 80, 800, 600),
            new Rectangle(-1000, 50, 820, 650), new Size(820, 650));
        Check(crop == new Rectangle(110, 230, 50, 40), "Window border/title bar offset is incorrect");
        Check(WindowGraphicsCapture.GetSurfaceRegion(new Rectangle(100, 200, 50, 40), new Rectangle(-990, 80, 800, 600),
            new Rectangle(-1000, 50, 820, 650), new Size(1640, 1300)) == new Rectangle(220, 460, 100, 80), "Surface DPI scaling is incorrect");
    }

    public static void FreshnessAndCancellation()
    {
        Check(WindowGraphicsCapture.IsFreshFrame(TimeSpan.FromSeconds(10.5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(11)), "Fresh frame rejected");
        Check(!WindowGraphicsCapture.IsFreshFrame(TimeSpan.FromSeconds(9), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(11)), "Old frame accepted");
        Check(!WindowGraphicsCapture.IsFreshFrame(TimeSpan.FromSeconds(12), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(11)), "Future frame accepted");
        Check(!WindowGraphicsCapture.IsFreshFrame(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(13)), "Stale frame accepted");
        using var source = new CancellationTokenSource();
        source.Cancel();
        try { WindowGraphicsCapture.CaptureAsync(new GameWindowState(IntPtr.Zero, Rectangle.Empty), null, source.Token).GetAwaiter().GetResult(); }
        catch (OperationCanceledException) { return; }
        throw new Exception("Capture ignored cancellation");
    }

    public static void CapturesCoveredWindowAndRecovers()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var dpi = SetThreadDpiAwarenessContext(new IntPtr(-4));
            try
            {
                using var persistent = new WindowGraphicsCapture.PersistentCapture();
                using var game = new Form { Text = "Companion capture fixture", StartPosition = FormStartPosition.Manual,
                    Bounds = new Rectangle(100, 100, 640, 400), BackColor = Color.FromArgb(20, 40, 80) };
                var frameNumber = 0;
                game.Paint += (_, e) => e.Graphics.FillRectangle(frameNumber % 2 == 0 ? Brushes.Red : Brushes.Blue, 0, 0, 4, 4);
                // A game renders continually. A static WinForms surface can legitimately
                // have no presentation newer than the request, especially after a style change.
                using var render = new System.Windows.Forms.Timer { Interval = 30 };
                render.Tick += (_, _) => { frameNumber++; game.Invalidate(new Rectangle(0, 0, 4, 4)); game.Update(); };
                render.Start();
                game.Show(); game.Refresh();
                var window = GameWindowActivity.GetVisibleWindowState(game.Handle) ?? throw new Exception("Fixture window is unavailable");
                using var cover = new Form { Text = "Companion occlusion fixture", StartPosition = FormStartPosition.Manual,
                    Bounds = game.Bounds, TopMost = true, BackColor = Color.Lime };
                cover.Show(); cover.Activate(); cover.Refresh();
                SettleWindow();
                var foreground = GetForegroundWindow();
                using (var frame = Capture(window, null, persistent))
                {
                    Check(frame.Image.Size == window.ClientBounds.Size, "Capture contains window frame instead of client area");
                    Check(Near(frame.Image.GetPixel(frame.Image.Width / 2, frame.Image.Height / 2), game.BackColor), "Covered capture read the covering window");
                }
                Check(GetForegroundWindow() == foreground, "Capturing stole keyboard focus");
                game.BackColor = Color.FromArgb(80, 20, 40); game.Refresh();
                var region = new Rectangle(80, 80, 120, 60);
                using (var frame = Capture(window, region, persistent))
                {
                    Check(frame.Image.Size == region.Size, "GPU crop has wrong dimensions");
                    Check(Near(frame.Image.GetPixel(50, 30), game.BackColor), "Updated frame is frozen");
                }
                game.Location = new Point(180, 140); game.ClientSize = new Size(700, 420); game.Refresh();
                cover.Bounds = game.Bounds; cover.Refresh(); SettleWindow();
                window = GameWindowActivity.GetVisibleWindowState(game.Handle)!;
                using (var frame = Capture(window, region, persistent)) Check(Near(frame.Image.GetPixel(50, 30), game.BackColor), "Capture failed after resize/move");
                Check(persistent.SessionStarts == 1, "Sampling and resize restarted the capture session");
                persistent.Stop();
                using (var frame = Capture(window, region, persistent)) Check(Near(frame.Image.GetPixel(50, 30), game.BackColor), "Capture did not recover after stopping");
                Check(persistent.SessionStarts == 2, "Stopping did not release the capture session");
                game.WindowState = FormWindowState.Minimized; Application.DoEvents();
                Check(GameWindowActivity.GetVisibleWindowState(game.Handle) is null, "Minimized game is considered capturable");
                try { using var frame = Capture(window, region, persistent); throw new Exception("Minimized game produced a fresh capture"); }
                catch (DayZCompanionException) { }
                game.WindowState = FormWindowState.Normal; game.Refresh(); SettleWindow();
                window = GameWindowActivity.GetVisibleWindowState(game.Handle)!;
                using (var frame = Capture(window, region, persistent)) Check(Near(frame.Image.GetPixel(50, 30), game.BackColor), "Capture did not recover after restore");
                game.FormBorderStyle = FormBorderStyle.None; game.Refresh(); SettleWindow();
                window = GameWindowActivity.GetVisibleWindowState(game.Handle)!;
                cover.Bounds = game.Bounds; cover.Refresh();
                using (var frame = Capture(window, region, persistent)) Check(Near(frame.Image.GetPixel(50, 30), game.BackColor), "Borderless window capture is offset");
            }
            catch (Exception ex) { failure = ex; }
            finally { if (dpi != IntPtr.Zero) SetThreadDpiAwarenessContext(dpi); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(25))) throw new Exception("Window capture fixture timed out");
        if (failure is not null) throw new Exception("Window capture integration failed: " + failure, failure);
    }

    private static CapturedWindowImage Capture(GameWindowState window, Rectangle? region, WindowGraphicsCapture.PersistentCapture persistent)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var task = persistent.CaptureAsync(window, region, timeout.Token, keepAlive: true, borderless: false);
        while (!task.IsCompleted) { Application.DoEvents(); Thread.Sleep(10); }
        return task.GetAwaiter().GetResult();
    }
    private static void SettleWindow()
    {
        // Persistent capture starts immediately; let DWM finish window transitions
        // before taking the geometry snapshot used by this fixture.
        var started = Stopwatch.GetTimestamp();
        while (Stopwatch.GetElapsedTime(started) < TimeSpan.FromMilliseconds(350))
        { Application.DoEvents(); Thread.Sleep(10); }
    }
    private static bool Near(Color left, Color right) => Math.Abs(left.R - right.R) <= 8 && Math.Abs(left.G - right.G) <= 8 && Math.Abs(left.B - right.B) <= 8;
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
}
