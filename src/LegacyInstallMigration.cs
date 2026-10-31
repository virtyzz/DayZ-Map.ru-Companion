using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;

namespace CrosshairMarker;

internal static class LegacyInstallMigration
{
    public const string PrepareArgument = "--prepare-legacy-upgrade";
    public const string FinishArgument = "--finish-legacy-upgrade";
    private sealed record State(bool DesktopIcon, bool Startup);
    private static string[] LegacyDirectories => new[] { AppIdentity.DisplayName, "Crosslay" }
        .Select(name => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", name)).ToArray();
    private static string StatePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DayZMarkerCompanion", "legacy-upgrade-state.json");

    public static int Prepare()
    {
        try
        {
            var directories = LegacyDirectories.Where(Directory.Exists).ToArray();
            if (directories.Length == 0) return 0;
            // Never execute a user-writable legacy uninstaller with the installer's elevated token.
            if (PerformancePipe.IsElevated()) throw new UnauthorizedAccessException("Для переноса запустите установщик обычным двойным щелчком.");
            foreach (var directory in directories)
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) throw new IOException("Папка прежней установки является ссылкой.");
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            var hadDesktopIcon = File.Exists(Path.Combine(desktop, AppIdentity.DisplayName + ".lnk")) || File.Exists(Path.Combine(desktop, "Crosslay.lnk"));
            using var startup = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            var state = new State(hadDesktopIcon, startup?.GetValue(AppIdentity.StartupValueName) is not null || startup?.GetValue(AppIdentity.LegacyStartupValueName) is not null);
            if (File.Exists(StatePath))
            {
                var previous = JsonSerializer.Deserialize<State>(File.ReadAllText(StatePath));
                if (previous is not null) state = new(state.DesktopIcon || previous.DesktopIcon, state.Startup || previous.Startup);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
            File.WriteAllText(StatePath, JsonSerializer.Serialize(state));
            foreach (var directory in directories) UninstallLegacyDirectory(directory);
            return 0;
        }
        catch (Exception ex) { AppRuntimeLog.Error("Legacy install migration failed", ex); return 1; }
    }

    private static void UninstallLegacyDirectory(string directory)
    {
        var uninstaller = Directory.EnumerateFiles(directory, "unins*.exe", SearchOption.TopDirectoryOnly).Order(StringComparer.OrdinalIgnoreCase).FirstOrDefault();
        if (uninstaller is null)
        {
            if (File.Exists(Path.Combine(directory, "DayZMapCompanion.exe")) || File.Exists(Path.Combine(directory, "Crosslay.exe")))
                throw new IOException("Не найден деинсталлятор прежней версии Companion.");
            return;
        }
        var start = new ProcessStartInfo(uninstaller) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in new[] { "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART" }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new IOException("Не удалось запустить удаление прежней версии.");
        process.WaitForExit();
        if (process.ExitCode != 0) throw new IOException("Удаление прежней версии завершилось с кодом " + process.ExitCode);
    }

    public static int Finish()
    {
        try
        {
            var state = File.Exists(StatePath) ? JsonSerializer.Deserialize<State>(File.ReadAllText(StatePath)) : null;
            using var startup = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            if (state?.Startup == true || startup?.GetValue(AppIdentity.StartupValueName) is not null || startup?.GetValue(AppIdentity.LegacyStartupValueName) is not null) StartupManager.SetEnabled(true);
            var commonShortcut = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), AppIdentity.DisplayName + ".lnk");
            if (state?.DesktopIcon == true && !File.Exists(commonShortcut))
                UpdateShortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), AppIdentity.DisplayName + ".lnk"));
            var pinned = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar");
            if (Directory.Exists(pinned))
            {
                foreach (var path in Directory.EnumerateFiles(pinned, "*.lnk"))
                {
                    try { UpdateShortcut(path, onlyLegacy: true); }
                    catch (Exception ex) { AppRuntimeLog.Error("Could not update pinned Companion shortcut", ex); }
                }
            }
            File.Delete(StatePath);
            return 0;
        }
        catch (Exception ex) { AppRuntimeLog.Error("Could not finish legacy install migration", ex); return 1; }
    }

    private static void UpdateShortcut(string path, bool onlyLegacy = false)
    {
        object? shell = null, shortcut = null;
        try
        {
            shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell", true)!)!;
            shortcut = ((dynamic)shell).CreateShortcut(path);
            if (onlyLegacy)
            {
                var target = (string)((dynamic)shortcut).TargetPath;
                if (!LegacyDirectories.Any(directory =>
                    string.Equals(target, Path.Combine(directory, "DayZMapCompanion.exe"), StringComparison.OrdinalIgnoreCase)
                    || string.Equals(target, Path.Combine(directory, "Crosslay.exe"), StringComparison.OrdinalIgnoreCase))) return;
            }
            ((dynamic)shortcut).TargetPath = Environment.ProcessPath!;
            ((dynamic)shortcut).WorkingDirectory = AppContext.BaseDirectory;
            ((dynamic)shortcut).IconLocation = Environment.ProcessPath! + ",0";
            ((dynamic)shortcut).Save();
        }
        finally
        {
            if (shortcut is not null) Marshal.FinalReleaseComObject(shortcut);
            if (shell is not null) Marshal.FinalReleaseComObject(shell);
        }
    }
}
