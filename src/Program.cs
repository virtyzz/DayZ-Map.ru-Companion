using System.Windows.Forms;

namespace CrosshairMarker;

internal static class Program
{
    private const string AppMutexName = @"Local\DayZMapRuCompanion.7E8F9DF7-7C0A-4D93-9DA8-4B4C6D40F23F";

    [STAThread]
    private static void Main()
    {
        using var mutex = new Mutex(initiallyOwned: true, AppMutexName, out var ownsMutex);
        if (!ownsMutex)
        {
            AppRuntimeLog.Info("Second app instance was started and exited.");
            return;
        }

        Application.ThreadException += (_, e) => AppRuntimeLog.Error("UI thread exception", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            AppRuntimeLog.Error("Unhandled exception", e.ExceptionObject as Exception);
        };

        try
        {
            ApplicationConfiguration.Initialize();
            using var appContext = new CrosshairApplicationContext();
            Application.Run(appContext);
        }
        finally
        {
            mutex.ReleaseMutex();
        }
    }
}
