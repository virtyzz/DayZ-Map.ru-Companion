using System.Diagnostics;
using System.Text.Json;
using System.Windows.Forms;
using CrosshairMarker;

internal static class PerformanceHelperDiagnostics
{
    public static int Run(string path, bool verifyRecovery = false)
    {
        var result = 1;
        var thread = new Thread(() =>
        {
            try
            {
                using var context = new ApplicationContext();
                using var sync = new WindowsFormsSynchronizationContext();
                SynchronizationContext.SetSynchronizationContext(sync);
                var elevationRequests = 0;
                using var helper = new PerformanceHelperClient(start => { elevationRequests++; return Process.Start(start)!; });
                using var timer = new System.Windows.Forms.Timer { Interval = 100 };
                var watch = Stopwatch.StartNew();
                var samples = new List<PerformanceSnapshot>();
                PerformanceSnapshot? last = null;
                int? workerPid = null;
                var stopping = false;
                double stopAt = 0;
                var workerStopped = false;
                var hadFrames = false;
                var frameGap = false;
                var recovered = false;
                helper.Changed += snapshot =>
                {
                    if (stopping) return;
                    last = snapshot; samples.Add(snapshot);
                    if (snapshot.Fps.HasValue) { if (frameGap) recovered = true; hadFrames = true; }
                    else if (hadFrames) frameGap = true;
                };
                timer.Tick += (_, _) =>
                {
                    var completed = verifyRecovery ? recovered : last?.Fps.HasValue == true && ((!string.IsNullOrEmpty(last.Server) && last.Ping.HasValue) || watch.Elapsed.TotalSeconds > 8);
                    if (!stopping && (completed || watch.Elapsed.TotalSeconds > (verifyRecovery ? 60 : 25)))
                    {
                        workerPid = helper.WorkerProcessId;
                        stopping = true; stopAt = watch.Elapsed.TotalSeconds;
                        helper.Apply(new PerformanceSettings());
                    }
                    if (!stopping) return;
                    try { using var worker = Process.GetProcessById(workerPid ?? -1); workerStopped = worker.HasExited; }
                    catch (ArgumentException) { workerStopped = true; }
                    if (!workerStopped && watch.Elapsed.TotalSeconds - stopAt < 8) return;
                    result = last?.Fps.HasValue == true && workerStopped && (!verifyRecovery || recovered) ? 0 : 1;
                    File.WriteAllText(path, JsonSerializer.Serialize(new { ParentPid = Environment.ProcessId, ParentElevated = PerformancePipe.IsElevated(), ElevationRequests = elevationRequests, WorkerPid = workerPid, WorkerStopped = workerStopped,
                        RecoveryObserved = recovered, NetworkAvailable = !string.IsNullOrEmpty(last?.Server), Last = last, Samples = samples }, new JsonSerializerOptions { WriteIndented = true }));
                    context.ExitThread();
                };
                helper.Apply(new PerformanceSettings { Enabled = true }, allowElevation: true);
                timer.Start(); Application.Run(context);
            }
            catch (Exception ex) { File.WriteAllText(path, ex.ToString()); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        return result;
    }
}
