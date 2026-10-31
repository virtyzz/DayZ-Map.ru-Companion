using System.Diagnostics;
using System.Text.Json;
using CrosshairMarker;

internal static class PerformanceDiagnostics
{
    public static int Preview(string outputPath)
    {
        var thread = new Thread(() =>
        {
            System.Windows.Forms.Application.SetHighDpiMode(System.Windows.Forms.HighDpiMode.PerMonitorV2);
            using var overlay = new PerformanceOverlayForm { Text = "Performance overlay preview", ShowInTaskbar = true };
            overlay.Shown += (_, _) => overlay.BeginInvoke(() => { overlay.Hide(); overlay.Show(); });
            var settings = new PerformanceSettings { EditingEnabled = true, Width = 420, Height = 80, FontSize = 20, Left = 320, Top = 240 };
            overlay.PlacementChanged += p =>
            {
                settings.MonitorDeviceName = p.MonitorDeviceName;
                settings.Left = p.Left; settings.Top = p.Top; settings.Width = p.Width; settings.Height = p.Height;
                File.WriteAllText(outputPath, JsonSerializer.Serialize(p));
            };
            using var timer = new System.Windows.Forms.Timer { Interval = 1000 };
            var firstTick = true;
            timer.Tick += (_, _) =>
            {
                overlay.Apply(settings, new PerformanceSnapshot(Running: true, Fps: 125, Ping: 55));
                if (firstTick) { firstTick = false; overlay.Hide(); overlay.Show(); }
            };
            overlay.Apply(settings, new PerformanceSnapshot(Running: true, Fps: 125, Ping: 55));
            timer.Start();
            System.Windows.Forms.Application.Run(overlay);
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start(); thread.Join();
        return 0;
    }

    public static async Task<int> RunAsync(string outputPath)
    {
        var frames = new List<object>();
        DayZConnection? connection = null;
        string? networkError = null;
        try
        {
            using var game = Process.GetProcessesByName("DayZ_x64").First();
            using var reader = new PresentMonReader(game.Id);
            var network = Task.Run(async () =>
            {
                try { connection = await DayZConnectionProbe.DetectAsync(game.Id, CancellationToken.None); }
                catch (Exception ex) { networkError = ex.ToString(); }
            });
            for (var second = 1; second <= 8; second++)
            {
                await Task.Delay(1000);
                var sample = reader.Read();
                frames.Add(new { Second = second, sample.Fps, sample.Status });
            }
            await network;
            File.WriteAllText(outputPath, JsonSerializer.Serialize(new
            {
                ProcessId = game.Id, Frames = frames,
                Server = connection?.Server.ToString(), Local = connection?.LocalAddress.ToString(), connection?.Adapter,
                NetworkError = networkError
            }, new JsonSerializerOptions { WriteIndented = true }));
            return networkError is null && connection is not null ? 0 : 1;
        }
        catch (Exception ex)
        {
            File.WriteAllText(outputPath, ex.ToString());
            return 1;
        }
    }
}
