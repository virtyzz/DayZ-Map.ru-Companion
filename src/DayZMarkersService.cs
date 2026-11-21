using System.Diagnostics;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace CrosshairMarker;

internal sealed class DayZMarkersService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
    private static readonly object WriteSync = new();
    private readonly DayZCompanionSettings settings;

    public DayZMarkersService(DayZCompanionSettings settings) => this.settings = settings;

    public string? ResolvePrivateMarkersPath()
    {
        if (IsMarkersFile(settings.PrivateMarkersPath)) return settings.PrivateMarkersPath;

        var relative = Path.Combine("Users", Environment.UserName, "AppData", "Local", "DayZ", "LBmaster", "Config", "LBGroup", "PrivateMarkers.json");
        var candidates = new List<string>();
        var local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DayZ", "LBmaster", "Config", "LBGroup", "PrivateMarkers.json");
        if (File.Exists(local)) candidates.Add(local);
        foreach (var drive in DriveInfo.GetDrives().Where(drive => drive.IsReady && drive.DriveType == DriveType.Fixed))
        {
            var candidate = Path.Combine(drive.RootDirectory.FullName, relative);
            if (File.Exists(candidate)) candidates.Add(candidate);
        }
        return candidates.OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
    }

    public List<JsonElement> ReadBlocks() => ReadBlocks(RequireMarkersFile());

    internal static List<JsonElement> ReadBlocks(string path)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8));
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                throw new DayZCompanionException("PrivateMarkers.json должен содержать корневой JSON-массив.");
            return document.RootElement.EnumerateArray().Select(element => element.Clone()).ToList();
        }
        catch (DayZCompanionException)
        {
            throw;
        }
        catch (JsonException ex)
        {
            throw new DayZCompanionException("PrivateMarkers.json содержит некорректный JSON: " + ex.Message);
        }
        catch (IOException ex)
        {
            throw new DayZCompanionException("Не удалось прочитать PrivateMarkers.json: " + ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new DayZCompanionException("Нет доступа к PrivateMarkers.json: " + ex.Message);
        }
    }

    public DayZMarkersFileStatus GetFileStatus(out List<JsonElement> blocks)
    {
        blocks = [];
        var path = ResolvePrivateMarkersPath();
        if (path is null) return new DayZMarkersFileStatus(null, false, "Файл PrivateMarkers.json не найден.");
        try
        {
            blocks = ReadBlocks(path);
            var probe = DayZFileAccessProbe.Probe(Path.GetDirectoryName(path)!);
            return new DayZMarkersFileStatus(path, probe.Writable, probe.Error);
        }
        catch (DayZCompanionException ex)
        {
            return new DayZMarkersFileStatus(path, false, ex.Message);
        }
    }

    public List<DayZBackupInfo> GetBackups()
    {
        var path = ResolvePrivateMarkersPath();
        if (path is null) return [];
        var directory = Path.GetDirectoryName(path)!;
        var prefix = Path.GetFileName(path) + ".";
        return Directory.EnumerateFiles(directory, prefix + "*.bak")
            .Select(file => new FileInfo(file))
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .Select(file => new DayZBackupInfo(file.FullName, file.LastWriteTime, file.Length))
            .ToList();
    }

    internal string BuildExport(string? currentServer)
    {
        var blocks = ReadBlocks();
        if (!string.IsNullOrWhiteSpace(currentServer))
        {
            var current = blocks.Where(block => IsServerBlock(block, currentServer)).ToList();
            if (current.Any(block => GetMarkers(block).Count > 0)) blocks = current;
        }

        if (!blocks.Any(block => GetMarkers(block).Count > 0))
            throw new DayZCompanionException("В файле игры пока нет меток.");
        return JsonSerializer.Serialize(blocks, JsonOptions);
    }

    internal static List<ImportRequest> ReadImportRequests(string path)
    {
        var grouped = new Dictionary<string, List<JsonElement>>(StringComparer.Ordinal);
        foreach (var block in ReadBlocks(path))
        {
            if (block.ValueKind != JsonValueKind.Object || !block.TryGetProperty("param2", out var entries)
                || entries.ValueKind != JsonValueKind.Array)
                throw new DayZCompanionException("В файле меток обнаружен некорректный серверный блок.");
            if (entries.GetArrayLength() == 0) continue;
            if (!block.TryGetProperty("param1", out var address) || address.ValueKind != JsonValueKind.String
                || address.GetString() is not { } server || !ServerAddress.TryParse(server, out _))
                throw new DayZCompanionException("У одного из блоков меток отсутствует корректный адрес сервера.");

            if (!grouped.TryGetValue(server, out var markers))
                grouped[server] = markers = [];
            markers.AddRange(entries.EnumerateArray());
        }

        var requests = grouped.Select(item => new ImportRequest(item.Key, "merge", item.Value)).ToList();
        ValidateImportRequests(requests);
        return requests;
    }

    public ImportResult Import(ImportRequest request) => ImportBatch([request]);

    internal ImportResult ImportBatch(IReadOnlyList<ImportRequest> requests, Action? beforeWrite = null)
    {
        ValidateImportRequests(requests);
        lock (WriteSync)
        {
            if (settings.BlockWritesWhenDayZRunning && IsDayZRunning())
                throw new DayZCompanionException("DayZ_x64.exe запущен: запись меток запрещена настройкой Companion.");
            var path = RequireMarkersFile();
            var blocks = ReadBlocks(path);
            var updated = 0;
            foreach (var request in requests) updated += ApplyImport(blocks, request);

            beforeWrite?.Invoke();
            var backup = BackupAndWrite(path, blocks);
            var imported = requests.Sum(request => request.Markers.Count);
            var target = requests.Count == 1 ? $"server={requests[0].Server}" : $"servers={requests.Count}";
            AppRuntimeLog.Info($"DayZ markers imported: {target}, imported={imported}, updated={updated}.");
            return new ImportResult(imported, updated, backup);
        }
    }

    private static void ValidateImportRequests(IReadOnlyList<ImportRequest> requests)
    {
        if (requests.Count == 0) throw new DayZCompanionException("В выбранном файле нет меток.");
        foreach (var request in requests) ValidateRequest(request);
    }

    private static int ApplyImport(List<JsonElement> blocks, ImportRequest request)
    {
        var matching = blocks.Where(block => IsServerBlock(block, request.Server)).ToList();
        var target = matching.Count == 0 ? CreateBlock(request.Server, []) : matching[0];
        if (matching.Count == 0) blocks.Add(target);
        foreach (var duplicate in matching.Skip(1)) blocks.Remove(duplicate);

        var existing = GetMarkers(target);
        List<JsonElement> finalMarkers;
        var updated = 0;
        if (request.Mode == "replace")
        {
            finalMarkers = request.Markers;
            updated = existing.Count(marker => request.Markers.Any(input => Uid(input) == Uid(marker)));
        }
        else
        {
            var incomingByUid = request.Markers.ToDictionary(Uid, StringComparer.Ordinal);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            finalMarkers = [];
            foreach (var marker in existing)
            {
                var uid = Uid(marker);
                if (!seen.Add(uid)) continue;
                if (incomingByUid.Remove(uid, out var replacement)) { finalMarkers.Add(replacement); updated++; }
                else finalMarkers.Add(marker);
            }
            finalMarkers.AddRange(incomingByUid.Values);
        }

        var index = blocks.FindIndex(block => IsServerBlock(block, request.Server));
        blocks[index] = ReplaceParam2(target, request.Server, finalMarkers);
        return updated;
    }

    internal static string? GetOfflineImportBlockReason(bool gameRunning, bool blockWhileRunning, PerformanceSnapshot snapshot, DateTimeOffset now)
    {
        if (!gameRunning) return null;
        if (!string.IsNullOrWhiteSpace(snapshot.Server)) return "Выйдите с сервера DayZ, чтобы загрузить метки.";
        if (blockWhileRunning) return "Закройте DayZ. Запись при запущенной игре запрещена в настройках.";
        if (!snapshot.Running && !snapshot.HelperStarting)
            return "Закройте DayZ или включите измерения в «Пинг и FPS», чтобы проверить подключение.";
        if (snapshot.ConnectionMeasuredAt is not { } measured || now - measured > TimeSpan.FromSeconds(12))
            return "Companion пока не может подтвердить выход с сервера. Дождитесь проверки подключения или закройте DayZ.";
        return null;
    }

    private string RequireMarkersFile()
    {
        var path = ResolvePrivateMarkersPath();
        if (path is null) throw new DayZCompanionException("Файл PrivateMarkers.json не найден. Укажите путь в настройках Companion.");
        return path;
    }

    private string BackupAndWrite(string path, List<JsonElement> blocks)
    {
        var backup = CreateBackupPath(path);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(blocks, JsonOptions), new UTF8Encoding(false));
            File.Replace(temp, path, backup, ignoreMetadataErrors: true);
            TrimBackups(path);
            return backup;
        }
        catch
        {
            if (File.Exists(temp)) File.Delete(temp);
            throw;
        }
    }

    private void TrimBackups(string path)
    {
        var directory = Path.GetDirectoryName(path)!;
        var prefix = Path.GetFileName(path) + ".";
        var cutoff = DateTime.UtcNow.AddDays(-settings.BackupMaxAgeDays);
        var backups = Directory.EnumerateFiles(directory, prefix + "*.bak").Select(file => new FileInfo(file)).OrderByDescending(file => file.LastWriteTimeUtc).ToList();
        foreach (var backup in backups.Where((file, index) => index >= settings.BackupLimit || file.LastWriteTimeUtc < cutoff)) backup.Delete();
    }

    private static string CreateBackupPath(string path)
    {
        var basePath = path + "." + DateTime.Now.ToString("yyyyMMddHHmmssfff");
        var candidate = basePath + ".bak";
        var suffix = 1;
        while (File.Exists(candidate))
        {
            candidate = basePath + "." + suffix++ + ".bak";
        }
        return candidate;
    }

    private static bool IsMarkersFile(string? path) => !string.IsNullOrWhiteSpace(path) && File.Exists(path) && string.Equals(Path.GetFileName(path), "PrivateMarkers.json", StringComparison.OrdinalIgnoreCase);
    internal static bool IsDayZRunning()
    {
        var processes = Process.GetProcessesByName("DayZ_x64");
        foreach (var process in processes) process.Dispose();
        return processes.Length > 0;
    }
    internal static bool IsServerBlock(JsonElement block, string server) => block.ValueKind == JsonValueKind.Object && block.TryGetProperty("param1", out var value) && value.ValueKind == JsonValueKind.String && value.GetString() == server;
    internal static List<JsonElement> GetMarkers(JsonElement block) => block.TryGetProperty("param2", out var markers) && markers.ValueKind == JsonValueKind.Array ? markers.EnumerateArray().Select(marker => marker.Clone()).ToList() : [];
    private static string Uid(JsonElement marker) => marker.GetProperty("uid").GetRawText();
    private static JsonElement CreateBlock(string server, List<JsonElement> markers) => JsonSerializer.SerializeToElement(new { param1 = server, param2 = markers }, JsonOptions);
    private static JsonElement ReplaceParam2(JsonElement source, string server, List<JsonElement> markers)
    {
        var map = source.EnumerateObject().ToDictionary(property => property.Name, property => property.Value.Clone());
        map["param1"] = JsonSerializer.SerializeToElement(server);
        map["param2"] = JsonSerializer.SerializeToElement(markers, JsonOptions);
        return JsonSerializer.SerializeToElement(map, JsonOptions);
    }

    private static void ValidateRequest(ImportRequest request)
    {
        if (!ServerAddress.TryParse(request.Server, out _)) throw new DayZCompanionException("server должен быть в формате host:port (порт 1–65535).");
        if (request.Mode is not ("merge" or "replace")) throw new DayZCompanionException("mode должен быть merge или replace.");
        if (request.Markers.Count == 0) throw new DayZCompanionException("markers должен быть непустым массивом JSON-объектов.");
        var uids = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < request.Markers.Count; index++)
        {
            var marker = request.Markers[index];
            if (marker.ValueKind != JsonValueKind.Object)
                throw new DayZCompanionException($"Метка markers[{index}] должна быть JSON-объектом.");
            if (!marker.TryGetProperty("uid", out var uid) || uid.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                throw new DayZCompanionException($"У метки markers[{index}] отсутствует uid.");
            if (!uids.Add(uid.GetRawText()))
                throw new DayZCompanionException($"uid меток не должны повторяться (повтор в markers[{index}]).");
        }
    }
}

internal sealed record ImportRequest(string Server, string Mode, List<JsonElement> Markers);
internal sealed record ImportResult(int Imported, int Updated, string Backup);
internal sealed record DayZBackupInfo(string Path, DateTime LastWriteTime, long Size);
internal sealed record DayZMarkersFileStatus(string? Path, bool Writable, string? Error);
internal sealed class DayZCompanionException(string message) : Exception(message);

internal static class ServerAddress
{
    public static bool TryParse(string? value, out string host)
    {
        host = "";
        if (string.IsNullOrWhiteSpace(value)) return false;
        var colon = value.LastIndexOf(':');
        if (colon <= 0 || colon == value.Length - 1 || value.IndexOf(' ') >= 0) return false;
        host = value[..colon];
        return !string.IsNullOrWhiteSpace(host) && int.TryParse(value[(colon + 1)..], out var port) && port is >= 1 and <= 65535;
    }
}
