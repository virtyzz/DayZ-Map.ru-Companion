using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CrosshairMarker;

internal sealed record CompanionUser(string Id, string DisplayName, string? Email);
internal sealed record CompanionSession(string Token, CompanionUser User);
internal sealed record CompanionDestinations(CompanionUser User, List<TreasureMapOption> Maps, List<TreasureProfileOption> Profiles);
internal sealed record CompanionEmailChallenge(string Challenge, int ExpiresIn);
internal sealed record CompanionDiscordStart(string Url);
internal sealed record CompanionDiscordResult(bool Pending, string? Token, CompanionUser? User);
internal sealed record CompanionTreasureResult(List<TreasureDeliveryOutcome> Outcomes);
internal sealed record CompanionCreatedProfile(string Id);

internal sealed class CompanionAccount : IDisposable
{
    private static readonly JsonSerializerOptions ApiJson = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };
    private static readonly TreasureMapOption[] OfflineMaps =
    [
        new("cherno", "Chernarus", 15360, 15360),
        new("deerisle", "Deer Isle", 16384, 16384),
        new("deadfall", "Deadfall", 10240, 10240),
        new("nasdara", "Nasdara", 16384, 16384)
    ];
    private readonly string sessionPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        AppIdentity.DataDirectoryName, "companion-account.dat");
    private readonly HttpClient http = new(new HttpClientHandler { UseCookies = false, AllowAutoRedirect = false })
    {
        BaseAddress = new Uri("https://dayz-map.ru/profiles-api/"),
        Timeout = Timeout.InfiniteTimeSpan
    };
    private readonly object stateLock = new();
    private CompanionSession? session;
    private CompanionDestinations? destinations;
    private string? connectionError;
    private long destinationsRequest;
    public event Action? Changed;

    public CompanionAccount()
    {
        try
        {
            if (File.Exists(sessionPath))
            {
                var data = TransformSecret(File.ReadAllBytes(sessionPath), protect: false);
                try { session = JsonSerializer.Deserialize<CompanionSession>(data, ApiJson); }
                finally { CryptographicOperations.ZeroMemory(data); }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception or JsonException)
        {
            connectionError = "Не удалось восстановить сеанс. Войдите снова.";
            AppRuntimeLog.Error("Could not restore the Companion session", ex);
        }
    }

    public CompanionSession? Session { get { lock (stateLock) return session; } }
    public CompanionUser? User { get { lock (stateLock) return destinations?.User ?? session?.User; } }
    public string? ConnectionError { get { lock (stateLock) return connectionError; } }
    public bool IsCurrent(CompanionSession owner) { lock (stateLock) return ReferenceEquals(session, owner); }

    public TreasureDestinationSession GetDestinations(string sessionId, bool includeOfflineMaps)
    {
        lock (stateLock)
        {
            return new(sessionId, DateTimeOffset.MaxValue, destinations?.Maps ?? (includeOfflineMaps ? [.. OfflineMaps] : []),
                destinations?.Profiles ?? []);
        }
    }

    public Task<CompanionEmailChallenge> RequestEmailCodeAsync(string email, CancellationToken token) =>
        SendAsync<CompanionEmailChallenge>(HttpMethod.Post, "companion/auth/email/request", new { email }, null, token, TimeSpan.FromSeconds(40));

    public async Task SignInWithEmailAsync(string challenge, string code, CancellationToken token)
    {
        var signedIn = await SendAsync<CompanionSession>(HttpMethod.Post, "companion/auth/email/verify",
            new { challenge, code }, null, token, TimeSpan.FromSeconds(40));
        await AcceptSessionAsync(signedIn, token);
    }

    public async Task SignInWithDiscordAsync(CancellationToken token)
    {
        var verifier = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var challenge = Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))).ToLowerInvariant();
        var start = await SendAsync<CompanionDiscordStart>(HttpMethod.Post, "companion/auth/discord/start",
            new { challenge }, null, token);
        if (!Uri.TryCreate(start.Url, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps ||
            !string.Equals(url.Host, "discord.com", StringComparison.OrdinalIgnoreCase) || !url.IsDefaultPort || url.UserInfo.Length != 0 ||
            url.AbsolutePath != "/oauth2/authorize")
            throw new DayZCompanionException("Сервер вернул некорректный адрес входа через Discord.");
        Process.Start(new ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true });

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(4));
        var started = Stopwatch.GetTimestamp();
        while (Stopwatch.GetElapsedTime(started) < TimeSpan.FromMinutes(10) && await timer.WaitForNextTickAsync(token))
        {
            var result = await SendAsync<CompanionDiscordResult>(HttpMethod.Post, "companion/auth/discord/complete",
                new { verifier }, null, token);
            if (result.Pending) continue;
            if (result.Token is null || result.User is null) throw new DayZCompanionException("Discord не подтвердил сеанс Companion.");
            await AcceptSessionAsync(new(result.Token, result.User), token);
            return;
        }
        throw new DayZCompanionException("Время входа через Discord истекло. Начните вход снова.");
    }

    private async Task AcceptSessionAsync(CompanionSession next, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        lock (stateLock)
        {
            if (session is not null) throw new DayZCompanionException("Сначала выйдите из текущего аккаунта.");
            Directory.CreateDirectory(Path.GetDirectoryName(sessionPath)!);
            var data = JsonSerializer.SerializeToUtf8Bytes(next, ApiJson);
            try
            {
                File.WriteAllBytes(sessionPath + ".tmp", TransformSecret(data, protect: true));
                File.Move(sessionPath + ".tmp", sessionPath, true);
            }
            finally { CryptographicOperations.ZeroMemory(data); }
            session = next;
            destinations = null;
            connectionError = null;
        }
        Changed?.Invoke();
        await RefreshAsync(token);
    }

    public async Task RefreshAsync(CancellationToken token, bool recordUse = false)
    {
        CompanionSession owner;
        long request;
        lock (stateLock)
        {
            if (session is not { } current) return;
            owner = current;
            request = ++destinationsRequest;
        }
        CompanionDestinations? next = null;
        string? failure = null;
        var path = recordUse ? "companion/destinations?record_use=true" : "companion/destinations";
        try { next = await SendAsync<CompanionDestinations>(HttpMethod.Get, path, null, owner, token); }
        catch (DayZCompanionException ex) when (!token.IsCancellationRequested) { failure = ex.Message; }
        lock (stateLock)
        {
            if (!ReferenceEquals(session, owner) || request != destinationsRequest) return;
            if (destinations is not null && next is not null && destinations.User == next.User &&
                destinations.Maps.SequenceEqual(next.Maps) && destinations.Profiles.SequenceEqual(next.Profiles))
                next = destinations;
            if ((next is null || ReferenceEquals(destinations, next)) && connectionError == failure) return;
            if (next is not null) destinations = next;
            connectionError = failure;
        }
        Changed?.Invoke();
    }

    public async Task SignOutAsync(CompanionSession owner, CancellationToken token)
    {
        try { await SendAsync<JsonElement>(HttpMethod.Delete, "companion/auth/session", null, owner, token); }
        catch (DayZCompanionException) when (!IsCurrent(owner)) { return; }
        ClearSession(owner);
    }

    public async Task<List<TreasureDeliveryOutcome>> SendTreasuresAsync(CompanionSession owner, TreasureMapBatch batch,
        IEnumerable<TreasureCoordinate> coordinates, CancellationToken token)
    {
        var result = await SendAsync<CompanionTreasureResult>(HttpMethod.Post,
            $"companion/profiles/{Uri.EscapeDataString(batch.ProfileId)}/treasures",
            new { batch.MapId, batch.Template.Name, batch.Template.Type, batch.Template.Color, coordinates }, owner, token);
        return result.Outcomes;
    }

    public async Task<string> CreateProfileAsync(string mapId, string name, CancellationToken token)
    {
        var owner = Session ?? throw new DayZCompanionException("Войдите в аккаунт Companion.");
        var profile = await SendAsync<CompanionCreatedProfile>(HttpMethod.Post, "companion/profiles", new { mapId, name }, owner, token);
        await RefreshAsync(token);
        return profile.Id;
    }

    internal async Task<T> SendAsync<T>(HttpMethod method, string path, object? body, CompanionSession? owner,
        CancellationToken token, TimeSpan? timeout = null)
    {
        if (owner is not null && !IsCurrent(owner)) throw new DayZCompanionException("Сеанс Companion изменился.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(timeout ?? TimeSpan.FromSeconds(8));
        using var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = JsonContent.Create(body, options: ApiJson);
        if (owner is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", owner.Token);
        HttpResponseMessage response;
        try { response = await http.SendAsync(request, deadline.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new DayZCompanionException("Сервер DayZ-Map не ответил вовремя."); }
        catch (HttpRequestException)
        { throw new DayZCompanionException("Не удалось связаться с сервером DayZ-Map."); }
        using (response)
        {
            if (response.StatusCode == HttpStatusCode.NoContent) return default!;
            if (!response.IsSuccessStatusCode)
            {
                if (owner is not null && response.StatusCode == HttpStatusCode.Unauthorized) ClearSession(owner);
                throw new DayZCompanionException(await ReadErrorAsync(response, deadline.Token).ConfigureAwait(false));
            }
            try
            {
                return await response.Content.ReadFromJsonAsync<T>(ApiJson, deadline.Token).ConfigureAwait(false)
                    ?? throw new DayZCompanionException("Сервер DayZ-Map вернул пустой ответ.");
            }
            catch (JsonException) { throw new DayZCompanionException("Сервер DayZ-Map вернул некорректный ответ."); }
        }
    }

    private void ClearSession(CompanionSession owner)
    {
        lock (stateLock)
        {
            if (!ReferenceEquals(session, owner)) return;
            File.Delete(sessionPath);
            session = null;
            destinations = null;
            connectionError = null;
        }
        Changed?.Invoke();
    }

    private static async Task<string> ReadErrorAsync(HttpResponseMessage response, CancellationToken token)
    {
        try
        {
            var body = await response.Content.ReadFromJsonAsync<JsonElement>(token).ConfigureAwait(false);
            if (body.ValueKind == JsonValueKind.Object && body.TryGetProperty("detail", out var detail) && detail.ValueKind == JsonValueKind.String)
                return detail.GetString()!;
        }
        catch (JsonException) { }
        return response.StatusCode switch
        {
            HttpStatusCode.NotFound => "Сервис недоступен или выбранный профиль удалён.",
            HttpStatusCode.UnprocessableEntity => "Проверьте введённые данные.",
            _ => $"Сервер DayZ-Map вернул ошибку ({(int)response.StatusCode})."
        };
    }

    private static byte[] TransformSecret(byte[] value, bool protect)
    {
        var handle = GCHandle.Alloc(value, GCHandleType.Pinned);
        try
        {
            var input = new DataBlob { Size = value.Length, Data = handle.AddrOfPinnedObject() };
            var ok = protect
                ? CryptProtectData(ref input, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out var output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!ok) throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                var result = new byte[output.Size];
                Marshal.Copy(output.Data, result, 0, result.Length);
                return result;
            }
            finally
            {
                if (!protect) for (var index = 0; index < output.Size; index++) Marshal.WriteByte(output.Data, index, 0);
                LocalFree(output.Data);
            }
        }
        finally { handle.Free(); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob { public int Size; public IntPtr Data; }

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref DataBlob input, string? description, IntPtr entropy,
        IntPtr reserved, IntPtr prompt, uint flags, out DataBlob output);

    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DataBlob input, IntPtr description, IntPtr entropy,
        IntPtr reserved, IntPtr prompt, uint flags, out DataBlob output);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr address);

    public void Dispose() => http.Dispose();
}
