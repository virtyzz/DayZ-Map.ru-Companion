using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Diagnostics;

namespace CrosshairMarker;

internal sealed class DayZCompanionServer : IDisposable
{
    private readonly DayZCompanionSettings settings;
    private static readonly JsonSerializerOptions ResponseJsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
    private readonly DayZMarkersService markers;
    private readonly TreasureMapBridge treasureBridge;
    private readonly CancellationTokenSource cancellation = new();
    private HttpListener? listener;
    private Task? worker;

    public DayZCompanionServer(DayZCompanionSettings settings, TreasureMapBridge? treasureBridge = null)
    {
        this.settings = settings;
        markers = new DayZMarkersService(settings);
        this.treasureBridge = treasureBridge ?? new TreasureMapBridge();
    }

    public int? Port { get; private set; }
    public string Status { get; private set; } = "Не запущен";
    public string LastOperation { get; private set; } = "Операций с метками пока не было.";

    public DayZCompanionStatus GetStatus()
    {
        var file = markers.GetFileStatus();
        return new DayZCompanionStatus(Status, Port, file.Path, file.Writable, file.Error, markers.GetBackups(), LastOperation, Process.GetProcessesByName("DayZ_x64").Length > 0);
    }

    public void Start()
    {
        if (listener is not null) return;
        if (!settings.AutoPort && settings.Port is < 1 or > 65535)
        {
            Status = "Ручной порт должен быть в диапазоне 1–65535.";
            AppRuntimeLog.Error("DayZ Companion HTTP service was not started: " + Status);
            return;
        }
        foreach (var port in DayZPortSelection.Candidates(settings))
        {
            if (!CanBind(port)) continue;
            try
            {
                var next = new HttpListener();
                next.Prefixes.Add($"http://127.0.0.1:{port}/");
                next.Start();
                listener = next;
                Port = port;
                Status = $"Работает на 127.0.0.1:{port}";
                worker = Task.Run(ListenLoopAsync);
                AppRuntimeLog.Info($"DayZ Companion HTTP service started on 127.0.0.1:{port}.");
                return;
            }
            catch (HttpListenerException ex)
            {
                Status = $"Порт {port} недоступен: {ex.Message}";
                AppRuntimeLog.Error($"Could not start DayZ Companion on port {port}", ex);
            }
        }
        Status = settings.AutoPort ? "Нет свободного порта в диапазоне 49950–49999" : $"Порт {settings.Port} занят или недоступен";
        AppRuntimeLog.Error("DayZ Companion HTTP service was not started: " + Status);
    }

    private async Task ListenLoopAsync()
    {
        while (!cancellation.IsCancellationRequested && listener is { IsListening: true })
        {
            try
            {
                var context = await listener.GetContextAsync();
                _ = Task.Run(() => HandleAsync(context), cancellation.Token);
            }
            catch (HttpListenerException) when (cancellation.IsCancellationRequested) { }
            catch (ObjectDisposedException) when (cancellation.IsCancellationRequested) { }
            catch (Exception ex) { AppRuntimeLog.Error("DayZ Companion HTTP listener error", ex); }
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        try
        {
            var request = context.Request;
            if (!request.Url!.AbsolutePath.StartsWith("/api/", StringComparison.Ordinal))
            {
                context.Response.StatusCode = 404;
                return;
            }
            var origin = request.Headers["Origin"];
            var isPrivateNetworkRequest = string.Equals(request.Headers["Access-Control-Request-Private-Network"], "true", StringComparison.OrdinalIgnoreCase);
            if (!DayZCorsPolicy.TryCreate(origin, settings.AllowDevelopmentOrigin, isPrivateNetworkRequest, out var cors))
            {
                context.Response.StatusCode = 403;
                return;
            }
            ApplyCors(context.Response, cors!);
            if (request.HttpMethod == "OPTIONS")
            {
                context.Response.StatusCode = 204;
                return;
            }

            switch (request.HttpMethod, request.Url.AbsolutePath)
            {
                case ("GET", "/api/v1/health"):
                    await WriteJsonAsync(context.Response, 200, new { ok = true, version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown" });
                    break;
                case ("GET", "/api/v1/markers"):
                    var blocks = markers.ReadBlocks();
                    LastOperation = $"{DateTime.Now:HH:mm:ss}: загружено блоков DayZ: {blocks.Count}.";
                    await WriteJsonAsync(context.Response, 200, new { ok = true, blocks });
                    break;
                case ("POST", "/api/v1/import"):
                    await ImportAsync(context);
                    break;
                case ("POST", "/api/v1/treasures/session"):
                    await SetTreasureSessionAsync(context);
                    break;
                case ("GET", "/api/v1/treasures/pending"):
                    await GetTreasureBatchAsync(context);
                    break;
                case ("POST", "/api/v1/treasures/ack"):
                    await AcknowledgeTreasureBatchAsync(context);
                    break;
                default:
                    await WriteJsonAsync(context.Response, 404, new { ok = false, message = "Маршрут не найден." });
                    break;
            }
        }
        catch (DayZCompanionException ex)
        {
            LastOperation = $"{DateTime.Now:HH:mm:ss}: {ex.Message}";
            await WriteJsonAsync(context.Response, 400, new { ok = false, message = ex.Message });
        }
        catch (JsonException)
        {
            LastOperation = $"{DateTime.Now:HH:mm:ss}: тело запроса не является корректным JSON.";
            await WriteJsonAsync(context.Response, 400, new { ok = false, message = "Тело запроса должно быть корректным JSON." });
        }
        catch (Exception ex)
        {
            AppRuntimeLog.Error("DayZ Companion request failed", ex);
            LastOperation = $"{DateTime.Now:HH:mm:ss}: внутренняя ошибка Companion.";
            await WriteJsonAsync(context.Response, 500, new { ok = false, message = "Внутренняя ошибка Companion." });
        }
        finally
        {
            try { context.Response.Close(); } catch { }
        }
    }

    private async Task ImportAsync(HttpListenerContext context)
    {
        using var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding ?? Encoding.UTF8, false, 1024, leaveOpen: false);
        var json = await reader.ReadToEndAsync();
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new DayZCompanionException("Тело запроса должно быть JSON-объектом.");
        var request = new ImportRequest(
            GetString(root, "server"),
            GetString(root, "mode"),
            GetMarkers(root));
        var result = markers.Import(request);
        LastOperation = $"{DateTime.Now:HH:mm:ss}: импортировано {result.Imported}, обновлено {result.Updated}.";
        await WriteJsonAsync(context.Response, 200, new { ok = true, imported = result.Imported, updated = result.Updated, backup = result.Backup });
    }

    private async Task SetTreasureSessionAsync(HttpListenerContext context)
    {
        using var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding ?? Encoding.UTF8, false, 1024, leaveOpen: false);
        using var document = JsonDocument.Parse(await reader.ReadToEndAsync());
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new DayZCompanionException("Сеанс карты должен быть JSON-объектом.");
        var maps = root.TryGetProperty("maps", out var mapsElement) && mapsElement.ValueKind == JsonValueKind.Array
            ? mapsElement.EnumerateArray().Select(item => new TreasureMapOption(GetString(item, "id"), GetString(item, "name"), GetInt(item, "width"), GetInt(item, "height"))).Where(item => !string.IsNullOrWhiteSpace(item.Id)).ToList() : [];
        var profiles = root.TryGetProperty("profiles", out var profilesElement) && profilesElement.ValueKind == JsonValueKind.Array
            ? profilesElement.EnumerateArray().Select(item => new TreasureProfileOption(GetString(item, "mapId"), GetString(item, "id"), GetString(item, "name"), !item.TryGetProperty("writable", out var writable) || writable.ValueKind != JsonValueKind.False, GetString(item, "markerName"), GetString(item, "markerType"), GetString(item, "markerColor"))).Where(item => !string.IsNullOrWhiteSpace(item.MapId) && !string.IsNullOrWhiteSpace(item.Id)).ToList() : [];
        treasureBridge.SetSession(new TreasureDestinationSession(GetString(root, "sessionId"), DateTimeOffset.UtcNow, maps, profiles));
        await WriteJsonAsync(context.Response, 200, new { ok = true, expiresInSeconds = 300 });
    }

    private async Task GetTreasureBatchAsync(HttpListenerContext context)
    {
        var sessionId = context.Request.QueryString["session"] ?? "";
        var batch = treasureBridge.GetPending(sessionId);
        await WriteJsonAsync(context.Response, 200, new { ok = true, batch });
    }

    private async Task AcknowledgeTreasureBatchAsync(HttpListenerContext context)
    {
        using var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding ?? Encoding.UTF8, false, 1024, leaveOpen: false);
        using var document = JsonDocument.Parse(await reader.ReadToEndAsync());
        var root = document.RootElement;
        var outcomes = root.TryGetProperty("outcomes", out var outcomesElement) && outcomesElement.ValueKind == JsonValueKind.Array
            ? outcomesElement.EnumerateArray().Select(item => new TreasureDeliveryOutcome(GetString(item, "captureId"), GetString(item, "result"), GetString(item, "message"))).Where(item => !string.IsNullOrWhiteSpace(item.CaptureId)).ToList()
            : null;
        treasureBridge.Acknowledge(GetString(root, "sessionId"), root.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.True, GetString(root, "message"), outcomes);
        await WriteJsonAsync(context.Response, 200, new { ok = true });
    }

    private static string GetString(JsonElement root, string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
    private static int GetInt(JsonElement root, string name) => root.TryGetProperty(name, out var value) && value.TryGetInt32(out var result) ? result : 0;
    private static List<JsonElement> GetMarkers(JsonElement root) => root.TryGetProperty("markers", out var value) && value.ValueKind == JsonValueKind.Array ? value.EnumerateArray().Select(marker => marker.Clone()).ToList() : [];
    private static void ApplyCors(HttpListenerResponse response, DayZCorsHeaders cors)
    {
        response.Headers["Access-Control-Allow-Origin"] = cors.Origin;
        response.Headers["Vary"] = "Origin";
        response.Headers["Access-Control-Allow-Methods"] = cors.Methods;
        response.Headers["Access-Control-Allow-Headers"] = cors.AllowedHeaders;
        if (cors.AllowPrivateNetwork)
            response.Headers["Access-Control-Allow-Private-Network"] = "true";
    }

    private static async Task WriteJsonAsync(HttpListenerResponse response, int status, object body)
    {
        response.StatusCode = status;
        response.ContentType = "application/json; charset=utf-8";
        var bytes = JsonSerializer.SerializeToUtf8Bytes(body, ResponseJsonOptions);
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes);
    }

    private static bool CanBind(int port)
    {
        try
        {
            using var probe = new TcpListener(IPAddress.Loopback, port);
            probe.Start();
            return true;
        }
        catch (SocketException) { return false; }
    }

    public void Dispose()
    {
        cancellation.Cancel();
        listener?.Close();
        cancellation.Dispose();
    }
}

internal sealed record DayZCompanionStatus(string ServiceStatus, int? Port, string? PrivateMarkersPath, bool FileWritable, string? FileError, List<DayZBackupInfo> Backups, string LastOperation, bool DayZRunning);
