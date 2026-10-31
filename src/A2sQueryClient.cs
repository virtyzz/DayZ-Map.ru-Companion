using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace CrosshairMarker;

internal sealed record A2sReply(double Milliseconds, string Name, int? GamePort);

internal static class A2sQueryClient
{
    private static readonly byte[] Request = [255, 255, 255, 255, 0x54, .. Encoding.ASCII.GetBytes("Source Engine Query\0")];
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(4) };

    public static async Task<A2sReply> QueryAsync(IPEndPoint endpoint, IPAddress local, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(1400);
        using var udp = new UdpClient(new IPEndPoint(local, 0));
        udp.Connect(endpoint);
        byte[] request = Request;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var start = Stopwatch.GetTimestamp();
            await udp.SendAsync(request, deadline.Token);
            var packet = (await udp.ReceiveAsync(deadline.Token)).Buffer;
            var elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            if (packet.Length == 9 && packet.AsSpan(0, 5).SequenceEqual(new byte[] { 255, 255, 255, 255, 0x41 }))
            {
                request = [.. Request, .. packet.AsSpan(5, 4)];
                continue;
            }
            return Parse(packet, elapsed);
        }
        throw new InvalidDataException("Повторяющийся challenge A2S");
    }

    internal static A2sReply Parse(byte[] packet, double elapsed)
    {
        if (packet.Length < 7 || !packet.AsSpan(0, 5).SequenceEqual(new byte[] { 255, 255, 255, 255, 0x49 }))
            throw new InvalidDataException("Не поддерживаемый или разделённый ответ A2S_INFO");
        var position = 6;
        string ReadString()
        {
            var end = Array.IndexOf(packet, (byte)0, position);
            if (end < 0) throw new InvalidDataException("Неполный ответ A2S_INFO");
            var value = Encoding.UTF8.GetString(packet, position, end - position);
            position = end + 1;
            return value;
        }
        var name = ReadString();
        ReadString();
        var folder = ReadString();
        ReadString();
        if (!string.Equals(folder, "dayz", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Query-порт принадлежит другой игре");
        position += 9; // AppID, players, max players, bots, type, environment, visibility, VAC
        if (position >= packet.Length) throw new InvalidDataException("Неполный ответ A2S_INFO");
        ReadString(); // version
        int? gamePort = null;
        if (position < packet.Length && (packet[position++] & 0x80) != 0)
        {
            if (position + 2 > packet.Length) throw new InvalidDataException("Неполный EDF-порт");
            gamePort = BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(position, 2));
        }
        return new A2sReply(elapsed, name, gamePort);
    }

    internal static List<int> PortsFromSteam(JsonElement root, IPEndPoint game)
    {
        var ports = new List<int>();
        if (!root.TryGetProperty("response", out var response) || !response.TryGetProperty("servers", out var servers)) return ports;
        foreach (var server in servers.EnumerateArray())
        {
            if (!server.TryGetProperty("gameport", out var gamePort) || gamePort.GetInt32() != game.Port) continue;
            if (server.TryGetProperty("addr", out var address) && address.GetString() is { } addressText && IPEndPoint.TryParse(addressText, out var ep)
                && ep.Address.Equals(game.Address)) ports.Add(ep.Port);
        }
        return ports.Distinct().ToList();
    }

    public static async Task<List<int>> FindPortsAsync(IPEndPoint game, CancellationToken token)
    {
        try
        {
            using var reply = await Http.GetAsync("https://api.steampowered.com/ISteamApps/GetServersAtAddress/v1/?addr=" + game.Address, token);
            reply.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await reply.Content.ReadAsStringAsync(token));
            return PortsFromSteam(json.RootElement, game);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException or FormatException)
        {
            token.ThrowIfCancellationRequested();
            return [];
        }
    }
}
