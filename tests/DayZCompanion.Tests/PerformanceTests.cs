using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using CrosshairMarker;

internal static class PerformanceTests
{
    private static byte[] Reply(int port = 2206) => [255, 255, 255, 255, 0x49, 17,
        .. Encoding.UTF8.GetBytes("Test DayZ\0ChernarusPlus\0dayz\0DayZ\0"),
        0x4c, 0x56, 1, 40, 0, (byte)'d', (byte)'w', 0, 1,
        .. Encoding.UTF8.GetBytes("1.28\0"), 0x80, (byte)(port & 255), (byte)(port >> 8)];

    public static void ParseRejectsWrongGameAndMalformedResponses()
    {
        var reply = A2sQueryClient.Parse(Reply(), 55);
        Check(reply.GamePort == 2206 && reply.Name == "Test DayZ", "Game port/name parsed incorrectly");
        Check(reply.MapName == "ChernarusPlus", "A2S map name was discarded");
        foreach (var bytes in new[] { new byte[] { 255, 255, 255, 255, 0x41 }, Reply()[..^1],
                     Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(Reply()).Replace("dayz", "rust")) })
        {
            try { A2sQueryClient.Parse(bytes, 55); }
            catch (InvalidDataException) { continue; }
            throw new Exception("Malformed/foreign response accepted");
        }
    }

    public static void SteamSelectsMatchingGamePort()
    {
        using var json = JsonDocument.Parse("""
            {"response":{"servers":[
              {"addr":"192.0.2.1:2201","gameport":2200},
              {"addr":"192.0.2.1:2207","gameport":2206},
              {"addr":"192.0.2.2:2207","gameport":2206}]}}
            """);
        var ports = A2sQueryClient.PortsFromSteam(json.RootElement, new IPEndPoint(IPAddress.Parse("192.0.2.1"), 2206));
        Check(ports.SequenceEqual(new[] { 2207 }), "Neighbour's query port selected");
    }

    public static void UdpChallengeAndTimeout() => UdpChallengeAndTimeoutAsync().GetAwaiter().GetResult();
    private static async Task UdpChallengeAndTimeoutAsync()
    {
        using var server = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var endpoint = (IPEndPoint)server.Client.LocalEndPoint!;
        var responder = Task.Run(async () =>
        {
            var first = await server.ReceiveAsync(deadline.Token);
            byte[] challenge = [255, 255, 255, 255, 0x41, 1, 2, 3, 4];
            await server.SendAsync(challenge, first.RemoteEndPoint, deadline.Token);
            var second = await server.ReceiveAsync(deadline.Token);
            Check(second.Buffer.TakeLast(4).SequenceEqual(new byte[] { 1, 2, 3, 4 }), "Challenge was not echoed");
            await server.SendAsync(Reply(), second.RemoteEndPoint, deadline.Token);
        });
        var reply = await A2sQueryClient.QueryAsync(endpoint, IPAddress.Loopback, deadline.Token);
        await responder;
        Check(reply.Name == "Test DayZ" && reply.Milliseconds >= 0, "UDP measurement failed");
        // The same socket stays open and deliberately stops answering.
        try { await A2sQueryClient.QueryAsync(endpoint, IPAddress.Loopback, deadline.Token); }
        catch (OperationCanceledException) when (!deadline.IsCancellationRequested) { return; }
        throw new Exception("Unanswered query did not time out within its own deadline");
    }

    public static void PresentMonV1AndV2Csv()
    {
        var v1 = new PresentMonCsv();
        // PresentMon's actual v1 schema uses a lowercase 'ms' prefix.
        v1.Read("Application,ProcessID,SwapChainAddress,Runtime,SyncInterval,PresentFlags,Dropped,TimeInSeconds,msInPresentAPI,msBetweenPresents");
        var frame1 = v1.Read("DayZ_x64.exe,5564,0x123,DXGI,0,0,0,1.250,0.1,20.0");
        Check(frame1 is { Time: 1250, Interval: 20, Chain: "0x123" }, "PresentMon v1 rows were discarded or scaled incorrectly");

        var v2 = new PresentMonCsv();
        v2.Read("Application,ProcessID,SwapChainAddress,PresentRuntime,TimeInMs,MsBetweenPresents");
        var frame2 = v2.Read("DayZ_x64.exe,5564,0x123,DXGI,1250,20.0");
        Check(frame2 == frame1, "PresentMon v2 does not produce the same frame as v1");
        Check(v2.Read("DayZ_x64.exe,5564,0x123,DXGI,1250,NA") is null, "Missing metrics accepted");
        Check(v2.Read("DayZ_x64.exe,5564,0x123,DXGI,1250,NaN") is null, "Nonfinite metrics accepted");
    }

    public static void ConnectionSelectionIsProcessSpecific()
    {
        var local = IPAddress.Parse("192.0.2.10");
        var remote = IPAddress.Parse("198.51.100.1");
        var data = new List<DayZNetworkSample>();
        for (var i = 0; i < 5; i++) data.Add(new(123, local, remote, 50001, 2206, true));
        data.Add(new(123, local, remote, 50001, 2206, false));
        for (var i = 0; i < 100; i++) data.Add(new(999, local, remote, 50002, 2212, true));
        data.Add(new(999, local, remote, 50002, 2212, false));
        // A one-way stream owned by the correct PID is insufficient.
        for (var i = 0; i < 100; i++) data.Add(new(123, local, remote, 50003, 2218, true));
        var selected = DayZConnectionProbe.SelectConnection(data, 123);
        Check(selected?.Server.Port == 2206 && selected.LocalAddress.Equals(local), "Unrelated or one-way UDP flow selected");
        Check(DayZConnectionProbe.SelectConnection(data, 456) is null, "Disconnected process has a server");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    public static void OverlaySettingsSurviveSaveAndMonitorChanges()
    {
        var original = new DayZCompanionSettings
        {
            Performance = new PerformanceSettings
            {
                EditingEnabled = true, MonitorDeviceName = "secondary", Left = 5000, Top = 4000,
                Width = 400, Height = 100, Opacity = 65, FontFamily = "Arial", FontSize = 22,
                TextColor = "#abcdef", BackgroundColor = "#123456", Bold = false, Italic = true,
                Alignment = "Right", VerticalLayout = true, ShowFps = false, ShowPing = true
            }
        };
        original.Normalize();
        var loaded = JsonSerializer.Deserialize<DayZCompanionSettings>(JsonSerializer.Serialize(original))!;
        var copy = new DayZCompanionSettings();
        copy.CopyFrom(loaded);
        Check(JsonSerializer.Serialize(copy.Performance) == JsonSerializer.Serialize(original.Performance), "Overlay preferences lost during save/copy");
        loaded.Performance.FontSize = 12;
        Check(copy.Performance.FontSize == 22, "Cloned preferences share state");
        var area = new System.Drawing.Rectangle(-1920, 0, 1920, 1080);
        var bounds = copy.Performance.GetBounds(area);
        Check(area.Contains(bounds) && bounds.Right == area.Right && bounds.Bottom == area.Bottom, "Overlay is inaccessible after a monitor change");
        Check(bounds.Bottom == 1080 && bounds.Bottom > 1040, "Overlay is constrained by the taskbar instead of the screen edge");
        var invalid = new PerformanceSettings { Width = 0, Height = int.MaxValue, FontSize = 100, Opacity = 0,
            TextColor = "invalid", BackgroundColor = null!, FontFamily = null!, Alignment = "invalid", ShowFps = false, ShowPing = false };
        invalid.Normalize();
        Check(invalid.Width == 120 && invalid.Height == 500 && invalid.Opacity == 10 && invalid.FontSize == 72
            && invalid.ShowFps && invalid.TextColor == "#EEB15B" && invalid.BackgroundColor == "#141416"
            && invalid.FontFamily == "Segoe UI" && invalid.Alignment == "Center", "Invalid preferences can break the overlay");
    }

    public static void FrameStatisticsHandleStuttersAndSwapChains()
    {
        var window = new FrameStatisticsWindow();
        window.Add((10, 10, "main"));
        Check(window.Read().OnePercentLow is null, "1% low is displayed before enough frames exist");
        var time = 10d;
        for (var i = 1; i < 1000; i++)
        {
            var interval = i >= 990 ? 100 : 10;
            time += interval;
            window.Add((time, interval, "main"));
            if (i % 100 == 0) window.Add((time, 1, "secondary"));
        }
        var data = window.Read();
        Check(data.OnePercentLow == 10 && data.FrameTime >= 90 && data.Fps < 12 && data.PeakFrameTime == 100, "Stutters or secondary swap chain distorted frame metrics");
        for (var i = 0; i < 200; i++) window.Add((50000 + i * 10, 10, "main"));
        Check(window.Read().OnePercentLow == 100 && window.Read().Fps == 100, "Old stutters were retained past the 30 second window");
    }

    public static void JitterAndGraphExpireOldSamples()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1000);
        var jitter = new PingStatisticsWindow();
        Check(jitter.Add(now, 40) is null, "One sample produces jitter");
        jitter.Add(now.AddSeconds(1), 50);
        Check(jitter.Add(now.AddSeconds(2), 45) == 7.5, "Jitter does not reflect adjacent RTT differences");
        Check(jitter.Add(now.AddSeconds(40), 80) is null, "Expired pings affect jitter");
        jitter.Clear();
        Check(jitter.Add(now.AddSeconds(41), 20) is null, "Jitter spans a failed query or server switch");
        var graph = new FrameGraphHistory();
        graph.Add(now, 100);
        var first = graph.Add(now.AddMilliseconds(50), 10);
        Check(first[^1] == 100, "Graph lost a short stutter within the same second");
        var next = graph.Add(now.AddSeconds(61), 8);
        Check(next.Length == 60 && next[^1] == 8 && next.Take(59).All(v => v is null), "Graph connects stale history across missing samples");
    }

    public static void OverlayAlphaAndThresholds()
    {
        var legacy = JsonSerializer.Deserialize<PerformanceSettings>("{\"Opacity\":64}")!;
        legacy.Normalize();
        Check(legacy.TextOpacity == 64 && legacy.BackgroundOpacity == 64, "Existing transparency was not migrated");
        var settings = new PerformanceSettings { TextOpacity = 100, BackgroundOpacity = 0, ColorByThresholds = true };
        settings.Normalize();
        Check(settings.GetMetricColor(20, true) == settings.CriticalColor && settings.GetMetricColor(45, true) == settings.WarningColor
            && settings.GetMetricColor(90, true) == settings.GoodColor, "FPS thresholds have the wrong direction");
        Check(settings.GetMetricColor(99, false) == settings.GoodColor && settings.GetMetricColor(100, false) == settings.WarningColor
            && settings.GetMetricColor(200, false) == settings.CriticalColor, "Ping thresholds have the wrong boundaries");
        using (var transparent = PerformanceOverlayRenderer.Render(new System.Drawing.Size(400, 60), settings, new(Fps: 72, Ping: 118)))
        {
            Check(transparent.GetPixel(1, 1).A == 0, "Transparent background still covers the game");
            var maxAlpha = 0;
            for (var y = 0; y < transparent.Height; y++) for (var x = 0; x < transparent.Width; x++) maxAlpha = Math.Max(maxAlpha, transparent.GetPixel(x, y).A);
            Check(maxAlpha > 240, "Text becomes transparent together with the background");
        }
        settings.TextOpacity = 0; settings.BackgroundOpacity = 40;
        using (var background = PerformanceOverlayRenderer.Render(new System.Drawing.Size(400, 60), settings, new(Fps: 72, Ping: 118)))
            Check(background.GetPixel(1, 1).A == 102 && background.GetPixel(100, 25).A == 102, "Text and background opacity cannot be controlled independently");

        settings.TextOpacity = 100; settings.BackgroundOpacity = 70;
        settings.ShowFrameTime = settings.ShowOnePercentLow = settings.ShowJitter = settings.ShowFrameGraph = true;
        PerformanceOverlayRenderer.Fit(settings);
        using var preview = PerformanceOverlayRenderer.Render(new(settings.Width, settings.Height), settings,
            new(Fps: 72, Ping: 118, FrameTime: 13.9, OnePercentLow: 45, Jitter: 5.5,
                FrameTimeHistory: Enumerable.Range(0, 60).Select(i => (double?)(i % 17 == 0 ? 75 : 14 + i % 5)).ToArray()));
        preview.Save(System.IO.Path.Combine(AppContext.BaseDirectory, "performance-metrics-preview.png"));
    }

    public static void GameOnlySettingRoundTrips()
    {
        var legacy = JsonSerializer.Deserialize<PerformanceSettings>("{}")!;
        Check(legacy.ShowOnlyInGame, "Existing settings must default to game-only visibility");
        legacy.ShowOnlyInGame = false;
        var restored = JsonSerializer.Deserialize<PerformanceSettings>(JsonSerializer.Serialize(legacy.Clone()))!;
        restored.Normalize();
        Check(!restored.ShowOnlyInGame, "Always-visible preference was not saved");
    }
}
