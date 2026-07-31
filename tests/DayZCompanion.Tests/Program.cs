using System.Text.Json;
using System.Net;
using CrosshairMarker;

var tests = new (string Name, Action Run)[]
{
    ("merge удаляет дубликаты и обновляет UID", MergeDeduplicatesAndUpdates),
    ("replace сохраняет другие серверы", ReplacePreservesOtherServers),
    ("создаётся backup", CreatesBackup),
    ("очистка backup учитывает количество и возраст", BackupCleanupAppliesBothPolicies),
    ("проверка файла подтверждает чтение и запись", FileStatusChecksReadAndWrite),
    ("повреждённый JSON даёт понятную ошибку", CorruptJsonFailsClearly),
    ("некорректный импорт не меняет файл", InvalidImportLeavesFileUntouched),
    ("числовой uid принимается без преобразования", NumericUidIsAccepted),
    ("кириллица сохраняется в UTF-8 без unicode-экранирования", CyrillicIsWrittenAsUtf8),
    ("параллельные импорты не теряют метки", ConcurrentImportsPreserveMarkers),
    ("некорректный ручной порт отклоняется", InvalidManualPortIsRejected),
    ("положение окна нормализуется для доступного экрана", WindowBoundsAreNormalized),
    ("CORS разрешает только точные Origin", CorsPolicyUsesExactOrigins),
    ("рестарт API требуется только при смене порта", SettingsRestartOnlyForPortChanges),
    ("размер окна хранится в настройках Companion", CompanionSettingsKeepWindowBounds),
    ("настройки Companion сохраняются и восстанавливаются", CompanionSettingsStoreRoundTrips),
    ("выбор портов соблюдает auto и manual режимы", PortSelectionHonorsModes),
    ("ошибка записи в каталог файла диагностируется", FileAccessProbeReportsWriteFailure),
    ("старая позиция окна переносится в настройки Companion", LegacyWindowBoundsAreMigrated),
    ("API проверяет Origin и PNA preflight", ApiChecksOriginAndPreflight)
};

var failures = new List<string>();
var skipped = new List<string>();
foreach (var test in tests)
{
    try
    {
        test.Run();
        Console.WriteLine($"PASS: {test.Name}");
    }
    catch (TestSkippedException ex)
    {
        skipped.Add($"SKIP: {test.Name}: {ex.Message}");
    }
    catch (Exception ex)
    {
        failures.Add($"FAIL: {test.Name}: {ex.Message}");
    }
}

foreach (var failure in failures) Console.Error.WriteLine(failure);
foreach (var skip in skipped) Console.WriteLine(skip);
return failures.Count == 0 ? 0 : 1;

static void MergeDeduplicatesAndUpdates()
{
    using var fixture = new MarkersFixture("""
        [
          {"param1":"203.0.113.10:2302","param2":[{"uid":"a","value":"old"},{"uid":"a","value":"duplicate"},{"uid":"keep"}]},
          {"param1":"203.0.113.10:2302","param2":[{"uid":"discard"}]}
        ]
        """);

    var result = fixture.Service.Import(Request("203.0.113.10:2302", "merge", """[{"uid":"a","value":"new"},{"uid":"added"}]"""));
    var blocks = fixture.Service.ReadBlocks();
    Equal(1, blocks.Count, "дублирующий серверный блок не удалён");
    var markers = blocks[0].GetProperty("param2").EnumerateArray().ToList();
    Equal(3, markers.Count, "неверное число меток после merge");
    Equal("new", markers.Single(marker => marker.GetProperty("uid").GetString() == "a").GetProperty("value").GetString(), "метка не обновлена");
    Equal(2, result.Imported, "неверно посчитаны импортированные метки");
    Equal(1, result.Updated, "неверно посчитаны обновлённые метки");
}

static void ReplacePreservesOtherServers()
{
    using var fixture = new MarkersFixture("""
        [
          {"param1":"one.example:2302","param2":[{"uid":"old"}]},
          {"param1":"two.example:2302","param2":[{"uid":"other"}]}
        ]
        """);

    fixture.Service.Import(Request("one.example:2302", "replace", """[{"uid":"replacement"}]"""));
    var blocks = fixture.Service.ReadBlocks();
    Equal("replacement", blocks.Single(block => block.GetProperty("param1").GetString() == "one.example:2302").GetProperty("param2")[0].GetProperty("uid").GetString(), "replace не заменил метки сервера");
    Equal("other", blocks.Single(block => block.GetProperty("param1").GetString() == "two.example:2302").GetProperty("param2")[0].GetProperty("uid").GetString(), "replace затронул другой сервер");
}

static void CreatesBackup()
{
    using var fixture = new MarkersFixture("[]");
    var result = fixture.Service.Import(Request("server.example:2302", "replace", """[{"uid":"one"}]"""));
    True(File.Exists(result.Backup), "backup не создан");
    True(File.ReadAllText(result.Backup).Trim() == "[]", "backup не содержит исходный файл");
}

static void BackupCleanupAppliesBothPolicies()
{
    using var fixture = new MarkersFixture("[]", backupLimit: 2, backupMaxAgeDays: 1);
    var oldBackup = System.IO.Path.Combine(fixture.DirectoryPath, "PrivateMarkers.json.20000101000000000.bak");
    File.WriteAllText(oldBackup, "[]");
    File.SetLastWriteTimeUtc(oldBackup, DateTime.UtcNow.AddDays(-2));

    fixture.Service.Import(Request("server.example:2302", "replace", """[{"uid":"one"}]"""));
    fixture.Service.Import(Request("server.example:2302", "replace", """[{"uid":"two"}]"""));
    fixture.Service.Import(Request("server.example:2302", "replace", """[{"uid":"three"}]"""));

    var backups = fixture.Service.GetBackups();
    Equal(2, backups.Count, "очистка по количеству backup не сработала");
    True(backups.All(backup => backup.LastWriteTime.ToUniversalTime() >= DateTime.UtcNow.AddDays(-1)), "очистка backup по возрасту не сработала");
}

static void FileStatusChecksReadAndWrite()
{
    using var fixture = new MarkersFixture("[]");
    var status = fixture.Service.GetFileStatus();
    Equal(fixture.Path, status.Path, "проверка вернула другой файл");
    True(status.Writable, "доступный тестовый файл не отмечен доступным для записи");
    True(status.Error is null, "для доступного файла вернулась ошибка");
}

static void CorruptJsonFailsClearly()
{
    using var fixture = new MarkersFixture("{ not json");
    var error = Throws<DayZCompanionException>(() => fixture.Service.ReadBlocks());
    True(error.Message.Contains("некорректный JSON", StringComparison.OrdinalIgnoreCase), "ошибка повреждённого файла непонятна");
}

static void InvalidImportLeavesFileUntouched()
{
    const string initial = """[{"param1":"server.example:2302","param2":[]}]""";
    using var fixture = new MarkersFixture(initial);
    var request = Request("server.example:2302", "merge", """[{"uid":"same"},{"uid":"same"}]""");
    _ = Throws<DayZCompanionException>(() => fixture.Service.Import(request));
    Equal(initial, File.ReadAllText(fixture.Path), "некорректный импорт изменил исходный файл");
}

static void NumericUidIsAccepted()
{
    using var fixture = new MarkersFixture("[]");
    fixture.Service.Import(Request("server.example:2302", "replace", """[{"uid":42,"title":"numeric"}]"""));
    var marker = fixture.Service.ReadBlocks().Single().GetProperty("param2")[0];
    Equal(42, marker.GetProperty("uid").GetInt32(), "числовой uid был изменён");
}

static void CyrillicIsWrittenAsUtf8()
{
    using var fixture = new MarkersFixture("[]");
    fixture.Service.Import(Request("server.example:2302", "replace", """[{"uid":"cyrillic","name":"Зенит (хелик)"}]"""));
    var json = File.ReadAllText(fixture.Path);
    True(json.Contains("Зенит (хелик)", StringComparison.Ordinal), "кириллица не записана в UTF-8");
    True(!json.Contains("\\u0417", StringComparison.Ordinal), "кириллица записана как unicode escape");
}

static void ConcurrentImportsPreserveMarkers()
{
    using var fixture = new MarkersFixture("[]");
    Parallel.For(0, 12, index =>
    {
        fixture.Service.Import(Request("server.example:2302", "merge", $$"""[{"uid":"parallel-{{index}}"}]"""));
    });
    var markers = fixture.Service.ReadBlocks().Single().GetProperty("param2").EnumerateArray().ToList();
    Equal(12, markers.Count, "часть параллельных импортов потеряна");
}

static void InvalidManualPortIsRejected()
{
    using var server = new DayZCompanionServer(new DayZCompanionSettings { AutoPort = false, Port = 70000 });
    server.Start();
    True(!server.Port.HasValue, "некорректный ручной порт был использован");
    True(server.Status.Contains("1–65535", StringComparison.Ordinal), "ошибка ручного порта непонятна");
}

static void WindowBoundsAreNormalized()
{
    var first = new System.Drawing.Rectangle(0, 0, 1920, 1080);
    var second = new System.Drawing.Rectangle(1920, 0, 1280, 1024);
    var normalized = EditorWindowPlacement.Normalize(
        new EditorWindowBounds { Left = 9000, Top = -500, Width = 20000, Height = 10 },
        first,
        [first, second]);
    True(first.Contains(normalized), "окно вне доступной рабочей области");
    Equal(1920, normalized.Width, "ширина окна не ограничена экраном");
    Equal(700, normalized.Height, "минимальная высота окна не восстановлена");
}

static void CorsPolicyUsesExactOrigins()
{
    True(DayZCorsPolicy.TryCreate("https://dayz-map.ru", false, true, out var production), "основной Origin отклонён");
    Equal("https://dayz-map.ru", production!.Origin, "Origin не отражён точно");
    Equal("GET, POST, OPTIONS", production.Methods, "неверные разрешённые методы");
    True(production.AllowPrivateNetwork, "PNA не отражён в политике");
    True(!DayZCorsPolicy.TryCreate("https://evil.dayz-map.ru", false, false, out _), "частично совпадающий Origin разрешён");
    True(!DayZCorsPolicy.TryCreate("http://localhost:8000", false, false, out _), "dev Origin разрешён без режима разработки");
    True(DayZCorsPolicy.TryCreate("http://localhost:8000", true, false, out var development) && !development!.AllowPrivateNetwork, "dev Origin не обрабатывается корректно");
}

static void SettingsRestartOnlyForPortChanges()
{
    var current = new DayZCompanionSettings { AutoPort = true, Port = 49950, BackupLimit = 20 };
    var backupChanged = new DayZCompanionSettings { AutoPort = true, Port = 49950, BackupLimit = 5 };
    var portChanged = new DayZCompanionSettings { AutoPort = false, Port = 49960, BackupLimit = 20 };
    True(!current.RequiresHttpRestart(backupChanged), "изменение политики backup перезапускает API");
    True(current.RequiresHttpRestart(portChanged), "смена режима/порта не перезапускает API");
    current.CopyFrom(backupChanged);
    Equal(5, current.BackupLimit, "настройка backup не применена без рестарта");
}

static void CompanionSettingsKeepWindowBounds()
{
    var source = new DayZCompanionSettings
    {
        EditorWindowBounds = new EditorWindowBounds { Left = 10, Top = 20, Width = 1240, Height = 780 }
    };
    var target = new DayZCompanionSettings();
    target.CopyFrom(source);
    True(target.EditorWindowBounds is not null, "координаты окна не скопированы в настройки Companion");
    Equal(1240, target.EditorWindowBounds!.Width, "ширина окна потеряна при сохранении настроек");
}

static void CompanionSettingsStoreRoundTrips()
{
    var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Crosslay-DayZSettings-" + Guid.NewGuid().ToString("N"));
    try
    {
        var store = new DayZCompanionSettingsStore(directory);
        store.Save(new DayZCompanionSettings
        {
            PrivateMarkersPath = System.IO.Path.Combine(directory, "PrivateMarkers.json"),
            AutoPort = false,
            Port = 49980,
            AllowDevelopmentOrigin = true,
            BackupLimit = 7,
            EditorWindowBounds = new EditorWindowBounds { Left = 1, Top = 2, Width = 1200, Height = 800 }
        });
        var loaded = store.Load();
        Equal(49980, loaded.Port, "ручной порт не восстановлен");
        True(loaded.AllowDevelopmentOrigin, "dev Origin не восстановлен");
        Equal(7, loaded.BackupLimit, "лимит backup не восстановлен");
        Equal(1200, loaded.EditorWindowBounds!.Width, "размер окна не восстановлен");

        File.WriteAllText(store.SettingsPath, "{ broken");
        var fallback = store.Load();
        True(fallback.AutoPort, "повреждённые настройки не заменены безопасными значениями");
    }
    finally
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}

static void PortSelectionHonorsModes()
{
    var automatic = new DayZCompanionSettings { AutoPort = true };
    Equal(49952, DayZPortSelection.FirstAvailable(automatic, port => port == 49952), "auto режим не выбрал первый свободный порт");
    Equal(49950, DayZPortSelection.Candidates(automatic).First(), "auto режим начинается не с 49950");
    Equal(49999, DayZPortSelection.Candidates(automatic).Last(), "auto режим заканчивается не на 49999");

    var manual = new DayZCompanionSettings { AutoPort = false, Port = 2302 };
    Equal(2302, DayZPortSelection.FirstAvailable(manual, port => port == 2302), "ручной порт не выбран");
    True(DayZPortSelection.FirstAvailable(manual, _ => false) is null, "занятый ручной порт заменён автоматически");
    Equal(1, DayZPortSelection.Candidates(manual).Count(), "ручной режим пробует больше одного порта");
}

static void FileAccessProbeReportsWriteFailure()
{
    var result = DayZFileAccessProbe.Probe(
        System.IO.Path.GetTempPath(),
        _ => throw new UnauthorizedAccessException("access denied"),
        _ => { });
    True(!result.Writable, "ошибка записи не обнаружена");
    True(result.Error?.Contains("Нет доступа для записи", StringComparison.Ordinal) == true, "ошибка записи сформулирована непонятно");
}

static void LegacyWindowBoundsAreMigrated()
{
    var config = new AppConfig { EditorWindowBounds = new EditorWindowBounds { Left = 100, Top = 200, Width = 1200, Height = 800 } };
    var settings = new DayZCompanionSettings();
    True(DayZSettingsMigration.ApplyLegacyWindowBounds(config, settings), "старая позиция окна не перенесена");
    Equal(100, settings.EditorWindowBounds!.Left, "координата окна перенесена неверно");
    True(!DayZSettingsMigration.ApplyLegacyWindowBounds(config, settings), "миграция повторно перезаписала отдельные настройки Companion");
}

static void ApiChecksOriginAndPreflight()
{
    using var server = new DayZCompanionServer(new DayZCompanionSettings { AutoPort = true });
    server.Start();
    if (!server.Port.HasValue) throw new TestSkippedException("среда не разрешает привязку к 127.0.0.1: " + server.Status);
    var port = server.Port.GetValueOrDefault();

    using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
    var url = $"http://127.0.0.1:{port}/api/v1/health";
    var blocked = client.GetAsync(url).GetAwaiter().GetResult();
    Equal(HttpStatusCode.Forbidden, blocked.StatusCode, "запрос без Origin не был запрещён");

    using var healthRequest = new HttpRequestMessage(HttpMethod.Get, url);
    healthRequest.Headers.Add("Origin", "https://dayz-map.ru");
    var health = client.Send(healthRequest);
    Equal(HttpStatusCode.OK, health.StatusCode, "разрешённый Origin не получил health");
    Equal("https://dayz-map.ru", health.Headers.GetValues("Access-Control-Allow-Origin").Single(), "CORS вернул неверный Origin");

    using var optionsRequest = new HttpRequestMessage(HttpMethod.Options, url);
    optionsRequest.Headers.Add("Origin", "https://www.dayz-map.ru");
    optionsRequest.Headers.Add("Access-Control-Request-Private-Network", "true");
    var options = client.Send(optionsRequest);
    Equal(HttpStatusCode.NoContent, options.StatusCode, "PNA preflight не вернул 204");
    Equal("true", options.Headers.GetValues("Access-Control-Allow-Private-Network").Single(), "PNA заголовок не выдан");
}

static ImportRequest Request(string server, string mode, string markers)
{
    using var document = JsonDocument.Parse(markers);
    return new ImportRequest(server, mode, document.RootElement.EnumerateArray().Select(marker => marker.Clone()).ToList());
}

static void Equal<T>(T expected, T actual, string message)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"{message}: ожидалось {expected}, получено {actual}");
}

static void True(bool value, string message)
{
    if (!value) throw new InvalidOperationException(message);
}

static T Throws<T>(Action action) where T : Exception
{
    try { action(); }
    catch (T exception) { return exception; }
    throw new InvalidOperationException($"Ожидалось исключение {typeof(T).Name}.");
}

sealed class MarkersFixture : IDisposable
{
    private readonly string directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Crosslay-DayZTests-" + Guid.NewGuid().ToString("N"));
    public DayZMarkersService Service { get; }
    public string Path { get; }
    public string DirectoryPath => directory;

    public MarkersFixture(string json, int backupLimit = 10, int backupMaxAgeDays = 30)
    {
        Directory.CreateDirectory(directory);
        Path = System.IO.Path.Combine(directory, "PrivateMarkers.json");
        File.WriteAllText(Path, json);
        Service = new DayZMarkersService(new DayZCompanionSettings { PrivateMarkersPath = Path, BackupLimit = backupLimit, BackupMaxAgeDays = backupMaxAgeDays });
    }

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}

sealed class TestSkippedException(string message) : Exception(message);
