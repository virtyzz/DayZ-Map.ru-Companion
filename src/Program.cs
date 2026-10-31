using System.Windows.Forms;

namespace CrosshairMarker;

internal static class Program
{
    private const string AppMutexName = @"Local\DayZMapRuCompanion.7E8F9DF7-7C0A-4D93-9DA8-4B4C6D40F23F";

    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length == 1 && args[0] is LegacyInstallMigration.PrepareArgument or LegacyInstallMigration.FinishArgument)
        {
            Environment.ExitCode = args[0] == LegacyInstallMigration.PrepareArgument ? LegacyInstallMigration.Prepare() : LegacyInstallMigration.Finish();
            return;
        }
        if (args.Length > 0 && args[0] == PerformanceScheduledTask.SetupArgument)
        {
            Environment.ExitCode = PerformanceScheduledTask.Configure(args[1..]);
            return;
        }
        if (args.Length == 1 && args[0] == PerformanceScheduledTask.RemoveArgument)
        {
            Environment.ExitCode = PerformanceScheduledTask.RemoveForCurrentInstallation();
            return;
        }
        if (args.Length > 0 && args[0] == PerformanceWorker.Argument)
        {
            Environment.ExitCode = PerformanceWorker.Run(args[1..]);
            return;
        }
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
