using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;
using CrosshairMarker;

internal static class UpdateTests
{
    private const string Url = "https://github.com/virtyzz/DayZ-Map.ru-Companion/releases/download/v99.0.0/Companion-Setup.exe";
    private static readonly byte[] Payload = Encoding.UTF8.GetBytes("verified installer fixture");
    private static string Digest => Convert.ToHexString(SHA256.HashData(Payload));
    private static UpdateInfo Info => new("0.7.1", "99.0.0", "v99.0.0", true, "", Url, "", null, null, Digest, Payload.Length);

    public static void DownloadsAreVerified() => DownloadsAsync().GetAwaiter().GetResult();
    public static void CleanupPreservesActiveAttempts()
    {
        var root = Path.Combine(Path.GetTempPath(), "companion-cleanup-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string Attempt()
        {
            var path = Path.Combine(root, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            foreach (var file in new[] { "installer.exe", "CompanionUpdateHost.exe", "update.json", "installer.download" })
                File.WriteAllText(Path.Combine(path, file), "fixture");
            return path;
        }
        try
        {
            var legacy = Attempt(); // Existing attempts have no lease file.
            var completed = Attempt();
            using (AppUpdater.AcquireDirectory(completed)) { }
            var active = Attempt();
            var unrelated = Path.Combine(root, "keep-me");
            Directory.CreateDirectory(unrelated);
            File.WriteAllText(Path.Combine(unrelated, "notes.txt"), "keep");
            var nested = Attempt();
            Directory.CreateDirectory(Path.Combine(nested, "unexpected-subfolder"));
            using (var parent = AppUpdater.AcquireDirectory(active))
            {
                AppUpdater.CleanupCompletedAttempts(root);
                Check(!Directory.Exists(legacy) && !Directory.Exists(completed), "Completed/legacy attempt was not removed");
                Check(File.Exists(Path.Combine(active, "installer.exe")), "Active download was removed");
                // The host takes its lease before signalling ready; cleanup
                // must still skip the attempt after the parent releases it.
                using var host = AppUpdater.AcquireDirectory(active);
                parent.Dispose();
                AppUpdater.CleanupCompletedAttempts(root);
                Check(File.Exists(Path.Combine(active, "installer.exe")), "Host handoff lost cleanup protection");
            }
            AppUpdater.CleanupCompletedAttempts(root);
            Check(!Directory.Exists(active), "Released attempt was not removed");
            Check(File.Exists(Path.Combine(unrelated, "notes.txt")) && Directory.Exists(nested), "Cleanup touched an unrelated directory");
            AppUpdater.CleanupCompletedAttempts(root); // Repeated cleanup is harmless.
        }
        finally { Directory.Delete(root, true); }
    }

    private static async Task DownloadsAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "companion-update-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var http = new HttpClient(new Handler((request, token) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Payload) })));
            var service = new UpdateService(http);
            var states = new List<UpdateProgress>();
            service.ProgressChanged += () => states.Add(service.Progress);
            var good = Path.Combine(directory, "good");
            var file = await service.DownloadInstallerAsync(Info, good, CancellationToken.None);
            Check(File.ReadAllBytes(file).SequenceEqual(Payload), "Downloaded data changed");
            Check(states.Any(s => s.Percent == 100) && states.Any(s => s.Message.Contains("Проверка")), "Download/verification stages missing");
            await AppUpdater.VerifyInstallerAsync(file, Digest);
            File.AppendAllText(file, "tampered");
            await Reject<InvalidDataException>(() => AppUpdater.VerifyInstallerAsync(file, Digest));
            foreach (var bad in new[] { Info with { InstallerSha256 = new string('0', 64) }, Info with { InstallerSize = Payload.Length + 1 } })
            {
                var folder = Path.Combine(directory, Guid.NewGuid().ToString("N"));
                await Reject<InvalidDataException>(() => service.DownloadInstallerAsync(bad, folder, CancellationToken.None));
                Check(!Directory.EnumerateFiles(folder).Any(), "Rejected download left executable/partial files");
            }
            Check(!(Info with { InstallerUrl = "https://github.com.evil.example/installer.exe" }).CanInstall, "Foreign source accepted");
            Check(!(Info with { InstallerUrl = Url.Replace("https:", "http:") }).CanInstall, "HTTP source accepted");
            Check(!(Info with { InstallerSha256 = null }).CanInstall, "Missing digest accepted");
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            await Reject<OperationCanceledException>(() => service.DownloadInstallerAsync(Info, Path.Combine(directory, "cancelled"), cancelled.Token));
            Check(!Directory.EnumerateFiles(Path.Combine(directory, "cancelled")).Any(), "Cancellation left files");
            using var slow = new HttpClient(new Handler(async (_, token) => { await Task.Delay(Timeout.Infinite, token); return new(HttpStatusCode.OK); }));
            using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
            await Reject<OperationCanceledException>(() => new UpdateService(slow).GetLatestAsync(true, stop.Token));
        }
        finally { Directory.Delete(directory, true); }
    }

    public static void ReleaseMetadata()
    {
        var release = new { tag_name = "v99.0.0", html_url = "https://github.com/virtyzz/DayZ-Map.ru-Companion/releases/tag/v99.0.0",
            assets = new[] {
                new { name = "Companion-Setup-latest.exe", browser_download_url = Url.Replace("Setup.exe", "Setup-latest.exe"), size = Payload.Length, digest = "sha256:" + Digest },
                new { name = "Companion-Setup.exe", browser_download_url = Url, size = Payload.Length, digest = "sha256:" + Digest }
            } };
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(release)) })));
        var info = new UpdateService(http).GetLatestAsync().GetAwaiter().GetResult();
        Check(info.CanInstall && info.InstallerUrl == Url && info.InstallerSha256 == Digest.ToLowerInvariant(), "Release digest/size/installer not parsed");
        Check(UpdateService.ParseDigest("sha256:bad") is null, "Invalid digest accepted");
        var start = AppUpdater.InstallerStart(@"C:\path with spaces\installer.exe");
        Check(start.ArgumentList.Contains("/NORESTART") && start.ArgumentList.Contains("/VERYSILENT"), "Silent installer can reboot Windows");
        Check(start.ArgumentList.Contains("/RESTARTEXITCODE=3010"), "Reboot requirement is not distinguishable from success");
        Check(start.UseShellExecute, "Installer must support normal UAC consent");
    }

    public static void HostShowsUntilRestart()
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { RunHostFixture(); } catch (Exception ex) { failure = ex; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start(); thread.Join();
        if (failure is not null) throw new Exception("Update host failed", failure);
    }

    private static void RunHostFixture()
    {
        var directory = Path.Combine(Path.GetTempPath(), "companion-host-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var previous = Environment.GetEnvironmentVariable("COMPANION_TEST_INSTALL_EXIT");
        try
        {
            var executable = Environment.ProcessPath!;
            var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(executable)));
            foreach (var exitCode in new[] { 0, 5, 3010 })
            {
                Environment.SetEnvironmentVariable("COMPANION_TEST_INSTALL_EXIT", exitCode.ToString());
                var name = @"Local\DayZCompanionUpdate." + Guid.NewGuid().ToString("N");
                using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, name);
                var plan = new UpdatePlan(Environment.ProcessId, 0, executable, hash, executable, executable, name);
                using var form = new UpdateHostForm(plan);
                form.Show();
                var elapsed = Stopwatch.StartNew();
                while (!form.IsDisposed && !form.Controls.OfType<Button>().Any(b => b.Visible))
                {
                    Application.DoEvents(); Thread.Sleep(10);
                    if (elapsed.Elapsed > TimeSpan.FromSeconds(20)) throw new Exception("Updater did not reach a final state");
                }
                Check(ready.WaitOne(0), "Parent was not told when host became ready");
                if (exitCode == 0) Check(form.IsDisposed, "Host did not close after restart acknowledgement");
                else
                {
                    Check(!form.IsDisposed, "Failure disappeared without user feedback");
                    Check(form.Controls.OfType<Label>().Any(l => l.Text.Contains(exitCode == 3010 ? "перезагрузка" : "код 5")), "Installation error was not displayed");
                    form.Close();
                }
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("COMPANION_TEST_INSTALL_EXIT", previous);
            Directory.Delete(directory, true);
        }
    }

    private static async Task Reject<T>(Func<Task> action) where T : Exception
    {
        try { await action(); } catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name);
    }
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token);
    }
}
