using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace CrosshairMarker;

internal sealed record UpdatePlan(int ParentId, long ParentStartTicks, string Installer, string Sha256,
    string OriginalExecutable, string RestartExecutable, string ReadyEvent);

internal static class AppUpdater
{
    public const string HostArgument = "--companion-update-host";
    public const string ReadyArgument = "--updated-ready";
    private const string LeaseFile = "active.lock";
    private static string UpdatesDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DayZMarkerCompanion", "updates");

    public static string CreateDirectory()
    {
        var directory = Path.Combine(UpdatesDirectory, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    // Parent and host overlap their leases during the ready handshake. Readers
    // can coexist, but cleanup cannot open the file exclusively or delete it.
    internal static FileStream AcquireDirectory(string directory) => new(
        Path.Combine(directory, LeaseFile), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);

    internal static void CleanupCompletedAttempts(string? updatesDirectory = null)
    {
        var root = Path.GetFullPath(updatesDirectory ?? UpdatesDirectory);
        try
        {
            if (!Directory.Exists(root) || (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) return;
            foreach (var directory in Directory.EnumerateDirectories(root))
            {
                // Only our immediate GUID directories are eligible. Attempts
                // contain top-level files only; never traverse links or subfolders.
                if (!Guid.TryParseExact(Path.GetFileName(directory), "N", out _)) continue;
                try
                {
                    if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0
                        || Directory.EnumerateDirectories(directory).Any()) continue;
                    var files = Directory.GetFiles(directory);
                    if (files.Any(file => (File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)) continue;
                    var leasePath = Path.Combine(directory, LeaseFile);
                    using (var lease = new FileStream(leasePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
                    {
                        foreach (var file in files)
                            if (!string.Equals(file, leasePath, StringComparison.OrdinalIgnoreCase)) File.Delete(file);
                    }
                    File.Delete(leasePath);
                    Directory.Delete(directory);
                }
                catch (IOException) { } // Active host or locked file: retry on the next launch.
                catch (UnauthorizedAccessException) { }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public static async Task StartHostAsync(string installer, string sha256)
    {
        var directory = Path.GetDirectoryName(installer)!;
        var original = Environment.ProcessPath ?? throw new IOException("Не найден путь Companion.");
        var host = Path.Combine(directory, "CompanionUpdateHost.exe");
        // Published builds are single-file. Framework-dependent development
        // builds also need the original apphost's DLL and runtime metadata.
        foreach (var file in Directory.EnumerateFiles(AppContext.BaseDirectory))
        {
            if (Path.GetExtension(file) is ".dll" or ".json")
                File.Copy(file, Path.Combine(directory, Path.GetFileName(file)));
        }
        File.Copy(original, host);
        using var parent = Process.GetCurrentProcess();
        var eventName = @"Local\DayZCompanionUpdate." + Guid.NewGuid().ToString("N");
        using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, eventName);
        var plan = new UpdatePlan(parent.Id, parent.StartTime.ToUniversalTime().Ticks, installer, sha256, original,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), AppIdentity.DisplayName, "DayZMapCompanion.exe"), eventName);
        var planPath = Path.Combine(directory, "update.json");
        await File.WriteAllTextAsync(planPath, JsonSerializer.Serialize(plan));
        var start = new ProcessStartInfo(host) { UseShellExecute = false, WorkingDirectory = directory };
        start.ArgumentList.Add(HostArgument);
        start.ArgumentList.Add(planPath);
        using var process = Process.Start(start) ?? throw new IOException("Не удалось открыть окно обновления.");
        if (!await Task.Run(() => ready.WaitOne(TimeSpan.FromSeconds(20))))
            throw new IOException("Окно обновления не ответило. Companion продолжает работать.");
    }

    internal static ProcessStartInfo InstallerStart(string installer)
    {
        var start = new ProcessStartInfo(installer) { UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden };
        foreach (var argument in new[] { "/VERYSILENT", "/SP-", "/SUPPRESSMSGBOXES", "/NORESTART", "/RESTARTEXITCODE=3010", "/NORESTARTAPPLICATIONS", "/LOG" })
            start.ArgumentList.Add(argument);
        return start;
    }

    internal static async Task VerifyInstallerAsync(string path, string expected)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream);
        if (!Convert.ToHexString(hash).Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Загруженное обновление изменилось. Установка отменена.");
    }

    internal static async Task WaitForParentAsync(UpdatePlan plan, CancellationToken token)
    {
        Process parent;
        try { parent = Process.GetProcessById(plan.ParentId); }
        catch (ArgumentException) { return; }
        using (parent)
        {
            if (parent.StartTime.ToUniversalTime().Ticks != plan.ParentStartTicks) return;
            await parent.WaitForExitAsync(token);
        }
    }

    public static int RunHost(string planPath)
    {
        try
        {
            var plan = JsonSerializer.Deserialize<UpdatePlan>(File.ReadAllText(planPath))
                ?? throw new InvalidDataException("Не найдены параметры обновления.");
            using var lease = AcquireDirectory(Path.GetDirectoryName(Path.GetFullPath(planPath))!);
            ApplicationConfiguration.Initialize();
            Application.Run(new UpdateHostForm(plan));
            return 0;
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Обновление Companion", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }

    public static void SignalRestartReady(string eventName)
    {
        if (!eventName.StartsWith(@"Local\DayZCompanionRestart.", StringComparison.Ordinal)) return;
        try { using var ready = EventWaitHandle.OpenExisting(eventName); ready.Set(); }
        catch (WaitHandleCannotBeOpenedException) { }
    }
}
