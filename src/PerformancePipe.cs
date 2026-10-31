using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace CrosshairMarker;

internal sealed record PerformanceWorkerCommand(int Version, int QueryPort)
{
    public void Validate()
    {
        if (Version != 1 || QueryPort is < 0 or > 65535) throw new InvalidDataException("Неверная команда помощника измерений.");
    }
}
internal sealed record PerformanceWorkerUpdate(int Version, PerformanceSnapshot Snapshot);

internal static class PerformancePipe
{
    internal const int MaximumMessageSize = 65536;
    private static readonly JsonSerializerOptions Json = new() { MaxDepth = 16 };

    public static NamedPipeServerStream CreateServer(string name)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var owner = identity.User ?? throw new UnauthorizedAccessException("Не удалось определить пользователя.");
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(true, false);
        security.SetOwner(owner);
        security.AddAccessRule(new(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        security.AddAccessRule(new(owner, PipeAccessRights.FullControl, AccessControlType.Allow));
        // Also supports UAC consent supplied with a different administrator account.
        security.AddAccessRule(new(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous, 4096, 4096, security);
    }

    public static async Task WriteAsync<T>(Stream stream, T value, CancellationToken token)
    {
        var data = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        if (data.Length is <= 0 or > MaximumMessageSize) throw new InvalidDataException("Слишком большое сообщение помощника.");
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, data.Length);
        await stream.WriteAsync(header, token).ConfigureAwait(false);
        await stream.WriteAsync(data, token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);
    }

    public static async Task<T> ReadAsync<T>(Stream stream, CancellationToken token)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, token).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is <= 0 or > MaximumMessageSize) throw new InvalidDataException("Неверная длина сообщения помощника.");
        var data = new byte[length];
        await stream.ReadExactlyAsync(data, token).ConfigureAwait(false);
        return JsonSerializer.Deserialize<T>(data, Json) ?? throw new InvalidDataException("Пустое сообщение помощника.");
    }

    public static void ValidateClient(NamedPipeServerStream pipe, int expectedPid)
    {
        if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var actual) || actual != expectedPid)
            throw new UnauthorizedAccessException("К каналу подключился другой процесс.");
    }

    public static int ClientProcessId(NamedPipeServerStream pipe)
    {
        if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var pid)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        return pid;
    }

    public static void ValidateServer(NamedPipeClientStream pipe, int expectedPid)
    {
        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var actual) || actual != expectedPid)
            throw new UnauthorizedAccessException("Канал принадлежит другому процессу.");
    }

    public static Process ValidateParent(int pid, long startTicks)
    {
        var parent = Process.GetProcessById(pid);
        try
        {
            if (parent.HasExited || parent.StartTime.ToUniversalTime().Ticks != startTicks
                || !string.Equals(parent.MainModule?.FileName, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase))
                throw new UnauthorizedAccessException("Не удалось подтвердить основной процесс Companion.");
            return parent;
        }
        catch { parent.Dispose(); throw; }
    }

    public static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out int processId);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out int processId);
}
