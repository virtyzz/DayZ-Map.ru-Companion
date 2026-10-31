using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;

namespace CrosshairMarker;

internal sealed class PresentMonReader : IDisposable
{
    private readonly object sync = new();
    private readonly FrameStatisticsWindow frames = new();
    private Process? process;
    private Task? readerTask;
    private readonly string sessionName = "DayZCompanionFPS_" + Guid.NewGuid().ToString("N");
    private readonly string executable = Path.Combine(AppContext.BaseDirectory, "tools", "PresentMon-2.6.0-x64.exe");
    private long lastFrame;
    private string error = "Ожидание кадров DayZ";
    private bool stopped;
    private Task? stopTask;

    public bool HasExited => readerTask?.IsCompleted != false;

    public PresentMonReader(int pid)
    {
        try
        {
            using (var file = File.OpenRead(executable))
                if (Convert.ToHexString(SHA256.HashData(file)) != "B2A706BC6AD475749E3B7E3409263AA1E6906D45BDCF993F6DBC0F660188F1AF")
                    throw new IOException("Контрольная сумма PresentMon не совпадает");
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            foreach (var arg in new[] { "--process_id", pid.ToString(CultureInfo.InvariantCulture), "--output_stdout", "--v1_metrics", "--no_console_stats",
                         "--no_track_input", "--no_track_gpu", "--terminate_on_proc_exit", "--session_name", sessionName })
                start.ArgumentList.Add(arg);
            process = Process.Start(start) ?? throw new IOException("Не удалось запустить PresentMon");
            readerTask = ReadAsync(process);
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        {
            error = "PresentMon: " + ex.Message;
        }
    }

    private async Task ReadAsync(Process child)
    {
        var stderr = child.StandardError.ReadToEndAsync();
        var csv = new PresentMonCsv();
        var loggedHeader = false;
        try
        {
            while (await child.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                var parsed = csv.Read(line);
                if (csv.HeaderSeen && !loggedHeader)
                {
                    loggedHeader = true;
                    AppRuntimeLog.Info("PresentMon CSV header recognized.");
                }
                if (parsed is not { } frame) continue;
                lock (sync)
                {
                    if (stopped) return;
                    frames.Add(frame);
                    if (lastFrame == 0) AppRuntimeLog.Info("PresentMon is receiving DayZ frames.");
                    lastFrame = Stopwatch.GetTimestamp();
                }
            }
            var details = (await stderr.ConfigureAwait(false)).Trim();
            await child.WaitForExitAsync().ConfigureAwait(false);
            AppRuntimeLog.Info($"PresentMon output ended (PID {child.Id}, exit {child.ExitCode}): " + details);
            lock (sync)
                if (!stopped) error = "Помощник не получает кадры DayZ." + (details.Length > 0 ? " " + details[..Math.Min(details.Length, 180)] : "");
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            lock (sync) if (!stopped) error = "PresentMon: " + ex.Message;
        }
    }

    public FrameStatistics Read()
    {
        lock (sync)
        {
            if (lastFrame == 0 || Stopwatch.GetElapsedTime(lastFrame).TotalSeconds > 3)
                return new(null, null, null, null, error);
            return frames.Read();
        }
    }

    public void Dispose()
    {
        _ = StopAsync();
    }

    public Task StopAsync()
    {
        lock (sync)
        {
            stopped = true;
            return stopTask ??= process is { } child ? StopAsync(child) : Task.CompletedTask;
        }
    }

    private async Task StopAsync(Process child)
    {
        try
        {
            if (!child.HasExited)
            {
                // Let PresentMon close its own named ETW session before killing a stuck reader.
                var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
                foreach (var arg in new[] { "--session_name", sessionName, "--terminate_existing_session" }) start.ArgumentList.Add(arg);
                using var stopper = Process.Start(start);
                if (stopper is not null)
                {
                    try { await stopper.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)); }
                    catch (TimeoutException) { DayZConnectionProbe.Kill(stopper); }
                }
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            AppRuntimeLog.Error("Could not stop FPS trace gracefully", ex);
        }
        finally
        {
            DayZConnectionProbe.Kill(child);
            try { if (readerTask is not null) await readerTask; }
            finally { child.Dispose(); }
        }
    }
}
