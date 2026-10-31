using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;

namespace CrosshairMarker;

internal sealed record DayZConnection(IPEndPoint Server, IPAddress LocalAddress, string Adapter);
internal sealed record DayZNetworkSample(int ProcessId, IPAddress Local, IPAddress Remote, int LocalPort, int RemotePort, bool Sent);

internal static class DayZConnectionProbe
{
    public static async Task<DayZConnection?> DetectAsync(int pid, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (TraceEventSession.IsElevated() != true)
            throw new UnauthorizedAccessException("Для определения соединения запустите Companion от администратора.");

        // A private Windows ETW session supplies UDP endpoints and the owning PID.
        // No packet capture driver, packet payloads, or capture files are involved.
        using var trace = new TraceEventSession("DayZCompanionNetwork_" + Guid.NewGuid().ToString("N"));
        trace.EnableKernelProvider(KernelTraceEventParser.Keywords.NetworkTCPIP);
        var source = trace.Source;
        var samples = new List<DayZNetworkSample>();
        source.Kernel.UdpIpSend += data =>
        {
            if (data.ProcessID == pid)
                samples.Add(new(pid, data.saddr, data.daddr, data.sport, data.dport, true));
        };
        source.Kernel.UdpIpRecv += data =>
        {
            if (data.ProcessID == pid)
                samples.Add(new(pid, data.daddr, data.saddr, data.dport, data.sport, false));
        };
        var processing = Task.Run(() => source.Process());
        try
        {
            var interval = Task.Delay(3000, token);
            if (await Task.WhenAny(processing, interval).ConfigureAwait(false) == processing)
                await processing.ConfigureAwait(false);
            await interval.ConfigureAwait(false);
        }
        finally
        {
            // Stop only this session, and drain it before inspecting the samples.
            trace.Stop();
            source.StopProcessing();
            await processing.ConfigureAwait(false);
        }
        token.ThrowIfCancellationRequested();
        var selected = SelectConnection(samples, pid);
        if (selected is null) return null;
        var adapter = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n =>
            n.GetIPProperties().UnicastAddresses.Any(a => a.Address.Equals(selected.LocalAddress)));
        return selected with { Adapter = adapter?.Name ?? selected.LocalAddress.ToString() };
    }

    internal static DayZConnection? SelectConnection(IEnumerable<DayZNetworkSample> samples, int pid)
    {
        var winner = samples.Where(s => s.ProcessId == pid
                && s.Remote.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(s.Remote)
                && !s.Remote.Equals(s.Local) && s.Remote.GetAddressBytes()[0] < 224 && s.RemotePort is > 0 and <= 65535)
            .GroupBy(s => (s.Local, s.Remote, s.LocalPort, s.RemotePort))
            .Where(g => g.Count(s => s.Sent) >= 3 && g.Any(s => !s.Sent))
            .OrderByDescending(g => g.Count()).FirstOrDefault();
        return winner is null ? null : new DayZConnection(new IPEndPoint(winner.Key.Remote, winner.Key.RemotePort), winner.Key.Local, "");
    }

    internal static void Kill(Process? process)
    {
        try { if (process is { HasExited: false }) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }
}
