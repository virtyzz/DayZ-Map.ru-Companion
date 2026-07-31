using Microsoft.Win32;
using System.Windows.Forms;

namespace CrosshairMarker;

internal static class StartupManager
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
            ?? Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);

        if (enabled)
        {
            key.SetValue(AppIdentity.StartupValueName, $"\"{Application.ExecutablePath}\"");
            key.DeleteValue(AppIdentity.LegacyStartupValueName, throwOnMissingValue: false);
        }
        else
        {
            key.DeleteValue(AppIdentity.StartupValueName, throwOnMissingValue: false);
            key.DeleteValue(AppIdentity.LegacyStartupValueName, throwOnMissingValue: false);
        }
    }
}
