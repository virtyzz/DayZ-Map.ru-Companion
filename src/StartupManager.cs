using Microsoft.Win32;
using System.Windows.Forms;

namespace CrosshairMarker;

internal static class StartupManager
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static void SetEnabled(bool enabled)
    {
#if MICROSOFT_STORE
        _ = SetPackagedStartupAsync(enabled);
#else
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
#endif
    }

#if MICROSOFT_STORE
    private static async Task SetPackagedStartupAsync(bool enabled)
    {
        try
        {
            var startup = await Windows.ApplicationModel.StartupTask.GetAsync("DayZMapCompanionStartup");
            if (!enabled) startup.Disable();
            else
            {
                var state = await startup.RequestEnableAsync();
                if (state != Windows.ApplicationModel.StartupTaskState.Enabled)
                    MessageBox.Show("Разрешите автозапуск Companion в параметрах Windows: Приложения → Автозагрузка.", AppIdentity.DisplayName);
            }
        }
        catch (Exception ex)
        {
            AppRuntimeLog.Error("Could not change packaged startup task", ex);
            MessageBox.Show("Не удалось изменить автозапуск. Проверьте параметры Windows: Приложения → Автозагрузка.", AppIdentity.DisplayName);
        }
    }
#endif
}
