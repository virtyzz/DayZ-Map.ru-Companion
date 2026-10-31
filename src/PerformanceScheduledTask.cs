using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Security.AccessControl;
using System.Text;

namespace CrosshairMarker;

internal static class PerformanceScheduledTask
{
    public const string SetupArgument = "--configure-performance-task";
    public const string RemoveArgument = "--remove-performance-tasks";
    private const string Prefix = "DayZMapCompanion.Performance.";
    private const string ActionArguments = "--performance-worker $(Arg0) $(Arg1) $(Arg2)";
    private const string Source = "DayZ-Map.ru Companion performance helper";

    internal static string TaskName(string executable, string sid) => Prefix + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(executable).ToUpperInvariant() + "|" + sid)))[..24];

    internal static bool IsProtectedInstall(string executable)
    {
        var path = Path.GetFullPath(executable);
        var root = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return false;
        using var identity = WindowsIdentity.GetCurrent();
        var subjects = identity.Groups!.Select(g => g.Value).Append(identity.User!.Value).ToHashSet();
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null).Value;
        subjects.Remove(administrators);
        const FileSystemRights writable = FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.WriteExtendedAttributes
            | FileSystemRights.WriteAttributes | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
        bool Protected(FileSystemSecurity security)
        {
            if (((SecurityIdentifier)security.GetOwner(typeof(SecurityIdentifier))!).Value == identity.User.Value) return false;
            return !security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>()
                .Any(rule => rule.AccessControlType == AccessControlType.Allow && subjects.Contains(rule.IdentityReference.Value) && (rule.FileSystemRights & writable) != 0);
        }
        if (!Protected(new FileInfo(path).GetAccessControl())) return false;
        for (var directory = Path.GetDirectoryName(path); directory is not null && directory.Length >= root.Length; directory = Path.GetDirectoryName(directory))
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0 || !Protected(new DirectoryInfo(directory).GetAccessControl())) return false;
        return true;
    }

    public static async Task<bool> LaunchAsync(string pipe, int parentPid, long ticks, Func<ProcessStartInfo, Process> elevate, CancellationToken token)
    {
        var executable = Environment.ProcessPath!;
        if (!IsProtectedInstall(executable)) return false; // Portable/development copies still use an explicit UAC launch.
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User!.Value;
        if (!await Task.Run(() => IsRegistered(executable, sid), token))
        {
            var setup = new ProcessStartInfo(executable) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
            foreach (var arg in new[] { SetupArgument, parentPid.ToString(), ticks.ToString() }) setup.ArgumentList.Add(arg);
            using var process = await Task.Run(() => elevate(setup));
            await process.WaitForExitAsync(token);
            if (process.ExitCode != 0) throw new IOException("Не удалось настроить автоматический запуск измерений.");
        }
        token.ThrowIfCancellationRequested();
        await Task.Run(() => RunRegistered(executable, sid, new[] { pipe, parentPid.ToString(), ticks.ToString() }), token);
        return true;
    }

    private static object Connect()
    {
        dynamic service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service", throwOnError: true)!)!;
        service.Connect();
        return service;
    }

    private static bool Matches(dynamic task, string executable, bool requireEnabled = true)
    {
        dynamic definition = task.Definition;
        return (!requireEnabled || task.Enabled) && definition.RegistrationInfo.Source == Source && definition.Actions.Count == 1
            && string.Equals((string)definition.Actions.Item(1).Path, executable, StringComparison.OrdinalIgnoreCase)
            && definition.Actions.Item(1).Arguments == ActionArguments && definition.Triggers.Count == 0
            && definition.Principal.RunLevel == 1;
    }

    internal static bool HasProtectedSecurity(string sddl)
    {
        var descriptor = new RawSecurityDescriptor(sddl);
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        if (descriptor.Owner != administrators && descriptor.Owner != system) return false;
        if (descriptor.DiscretionaryAcl is null) return false;
        const uint modify = 0x40000000 | 0x10000000 | 0x000D0116; // Generic write/all, task writes, delete, WRITE_DAC/OWNER.
        return !descriptor.DiscretionaryAcl.Cast<GenericAce>().OfType<CommonAce>()
            .Any(ace => ace.AceQualifier == AceQualifier.AccessAllowed && ace.SecurityIdentifier != administrators
                && ace.SecurityIdentifier != system && ((uint)ace.AccessMask & modify) != 0);
    }

    private static bool IsRegistered(string executable, string sid)
    {
        object? service = null, folder = null, task = null;
        try
        {
            service = Connect(); folder = ((dynamic)service).GetFolder(@"\");
            task = ((dynamic)folder).GetTask(TaskName(executable, sid));
            return Matches(task, executable) && HasProtectedSecurity((string)((dynamic)task).GetSecurityDescriptor(5));
        }
        catch (Exception ex) when (ex is COMException or FileNotFoundException && (uint)ex.HResult is 0x80070002 or 0x8004130F) { return false; }
        finally { Release(task); Release(folder); Release(service); }
    }

    private static void RunRegistered(string executable, string sid, string[] arguments)
    {
        object? service = null, folder = null, task = null, running = null;
        try
        {
            service = Connect(); folder = ((dynamic)service).GetFolder(@"\"); task = ((dynamic)folder).GetTask(TaskName(executable, sid));
            if (!Matches(task, executable) || !HasProtectedSecurity((string)((dynamic)task).GetSecurityDescriptor(5))) throw new UnauthorizedAccessException("Настройки задачи помощника изменены.");
            running = ((dynamic)task).Run(arguments);
        }
        finally { Release(running); Release(task); Release(folder); Release(service); }
    }

    public static int Configure(string[] args)
    {
        try
        {
            if (args.Length != 2 || !int.TryParse(args[0], out var pid) || !long.TryParse(args[1], out var ticks)) return 2;
            if (!PerformancePipe.IsElevated() || !IsProtectedInstall(Environment.ProcessPath!)) return 3;
            using var parent = PerformancePipe.ValidateParent(pid, ticks);
            var sid = PerformanceProcessIdentity.UserSid(parent.Id);
            using var identity = WindowsIdentity.GetCurrent();
            var sameUser = identity.User!.Value == sid;
            object? service = null, folder = null, definition = null, action = null, registered = null;
            try
            {
                service = Connect(); folder = ((dynamic)service).GetFolder(@"\"); definition = ((dynamic)service).NewTask(0);
                dynamic task = definition;
                task.RegistrationInfo.Source = Source;
                task.RegistrationInfo.Description = "Сбор FPS и сетевой статистики по запросу Companion. Без самостоятельного автозапуска.";
                task.Principal.UserId = sameUser ? sid : "S-1-5-18";
                task.Principal.LogonType = sameUser ? 3 : 5; // Interactive token; SYSTEM only for over-the-shoulder administrator consent.
                task.Principal.RunLevel = 1;
                task.Settings.Enabled = true; task.Settings.Hidden = true; task.Settings.AllowDemandStart = true;
                task.Settings.DisallowStartIfOnBatteries = false; task.Settings.StopIfGoingOnBatteries = false;
                task.Settings.ExecutionTimeLimit = "PT0S"; task.Settings.MultipleInstances = 0;
                action = task.Actions.Create(0);
                ((dynamic)action).Path = Environment.ProcessPath!;
                ((dynamic)action).Arguments = ActionArguments;
                ((dynamic)action).WorkingDirectory = AppContext.BaseDirectory;
                var security = $"O:BAG:BAD:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;GRGX;;;{sid})";
                registered = ((dynamic)folder).RegisterTaskDefinition(TaskName(Environment.ProcessPath!, sid), task, 6,
                    sameUser ? sid : "S-1-5-18", null, sameUser ? 3 : 5, security);
            }
            finally { Release(registered); Release(action); Release(definition); Release(folder); Release(service); }
            return 0;
        }
        catch (Exception ex) { AppRuntimeLog.Error("Could not configure performance task", ex); return 1; }
    }

    public static int RemoveForCurrentInstallation()
    {
        if (!PerformancePipe.IsElevated()) return 3;
        object? service = null, folder = null, tasks = null;
        try
        {
            service = Connect(); folder = ((dynamic)service).GetFolder(@"\"); tasks = ((dynamic)folder).GetTasks(1);
            for (var i = ((dynamic)tasks).Count; i >= 1; i--)
            {
                object task = ((dynamic)tasks).Item(i);
                try
                {
                    if (!((string)((dynamic)task).Name).StartsWith(Prefix, StringComparison.Ordinal) || !Matches(task, Environment.ProcessPath!, false)) continue;
                    ((dynamic)task).Stop(0);
                    ((dynamic)folder).DeleteTask(((dynamic)task).Name, 0);
                }
                finally { Release(task); }
            }
            return 0;
        }
        catch (Exception ex) { AppRuntimeLog.Error("Could not remove performance tasks", ex); return 1; }
        finally { Release(tasks); Release(folder); Release(service); }
    }

    private static void Release(object? value) { if (value is not null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value); }
}
