using System.Text.Json;
using System.Text.Json.Serialization;

namespace CrosshairMarker;

internal enum BattlePassPage
{
    Daily,
    WeeklyPage1,
    WeeklyPage2,
    Seasonal
}

internal sealed class BattlePassSnapshot
{
    public DateTimeOffset? UpdatedAt { get; set; }
    public int? Level { get; set; }
    public string? Experience { get; set; }
    public List<BattlePassTask> Tasks { get; set; } = [];
}

internal sealed class BattlePassTask
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public BattlePassPage Page { get; set; }
    public int Slot { get; set; }
    public string Title { get; set; } = "Задание не распознано";
    public string Description { get; set; } = "";
    public int? ExperienceReward { get; set; }
    public int Current { get; set; }
    public int Target { get; set; }
    public bool Completed { get; set; }
    public bool Pinned { get; set; }
    public bool DescriptionExpanded { get; set; } = true;
    public DateTimeOffset UpdatedAt { get; set; }

    public string ProgressText => Target > 0 ? $"{Current}/{Target}" : "—";
}

internal sealed class ManualTask
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public int Current { get; set; }
    public int Target { get; set; }
    [JsonIgnore] public bool IsEditing { get; set; } = true;

    public void Normalize()
    {
        Title = (Title ?? "").Trim();
        Description ??= "";
        Current = Math.Max(0, Current);
        Target = Math.Max(0, Target);
    }
}

internal sealed class BattlePassSettings
{
    public bool OverlayVisible { get; set; } = true;
    public string? MonitorDeviceName { get; set; }
    public int Left { get; set; } = 24;
    public int Top { get; set; } = 160;
    public int Width { get; set; } = 360;
    public int Height { get; set; } = 470;
    public int Opacity { get; set; } = 230;
    public int FontSize { get; set; } = 14;
    public bool OverlayCollapsed { get; set; }
    public bool ShowCompleted { get; set; }
    public bool ShowDaily { get; set; } = true;
    public bool ShowWeekly { get; set; } = true;
    public bool ShowSeasonal { get; set; } = true;
    public bool DailyCollapsed { get; set; }
    public bool WeeklyCollapsed { get; set; }
    public bool SeasonalCollapsed { get; set; }
    public BattlePassPage CapturePage { get; set; } = BattlePassPage.Daily;
    public bool SaveDebugScreenshot { get; set; }
    public bool ShowTaskDescriptions { get; set; } = true;
    public bool OverlayEditingEnabled { get; set; }
    public double TitleX { get; set; } = .135;
    public double TitleWidth { get; set; } = .43;
    public double ProgressX { get; set; } = .885;
    public double FirstRowY { get; set; } = .245;
    public double RowStep { get; set; } = .137;
    public BattlePassOcrLayout? OcrLayout { get; set; }

    public void Normalize()
    {
        Left = Math.Max(0, Left);
        Top = Math.Max(0, Top);
        Width = Math.Clamp(Width, 220, 900);
        Height = Math.Clamp(Height, 92, 850);
        Opacity = Math.Clamp(Opacity, 40, 255);
        FontSize = Math.Clamp(FontSize, 9, 28);
        if (!ShowDaily && !ShowWeekly && !ShowSeasonal) ShowDaily = true;
        if (!Enum.IsDefined(CapturePage)) CapturePage = BattlePassPage.Daily;
        TitleX = Math.Clamp(TitleX, 0, .8);
        TitleWidth = Math.Clamp(TitleWidth, .1, 1 - TitleX);
        ProgressX = Math.Clamp(ProgressX, 0, .95);
        FirstRowY = Math.Clamp(FirstRowY, .05, .8);
        RowStep = Math.Clamp(RowStep, .05, .2);
        OcrLayout ??= BattlePassOcrLayout.FromLegacy(this);
        OcrLayout.Normalize();
    }

    public void ResetOverlayBounds()
    {
        Left = 24;
        Top = 160;
        Width = 360;
        Height = 470;
        OverlayCollapsed = false;
    }

    public BattlePassSettings Clone() => (BattlePassSettings)MemberwiseClone();
}

internal sealed class BattlePassOcrZone
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public BattlePassOcrZone() { }
    public BattlePassOcrZone(double x, double y, double width, double height) { X = x; Y = y; Width = width; Height = height; Normalize(); }
    public BattlePassOcrZone Clone() => new(X, Y, Width, Height);
    public void Normalize() { X = Math.Clamp(X, 0, .95); Y = Math.Clamp(Y, 0, .95); Width = Math.Clamp(Width, .02, 1 - X); Height = Math.Clamp(Height, .02, 1 - Y); }
}

internal sealed class BattlePassOcrLayout
{
    // Base layout matching the current Battle Pass screen at normal UI scale.
    public BattlePassOcrZone Title { get; set; } = new(.1318354430379747, .2352370990237099, .43, .047);
    public BattlePassOcrZone Description { get; set; } = new(.135, .297, .46401898734177216, .06557880055788005);
    public BattlePassOcrZone Progress { get; set; } = new(.885, .29231520223152024, .06784810126582279, .03942119944211995);
    public BattlePassOcrZone Experience { get; set; } = new(.6344620253164557, .2619470013947001, .09, .035);
    public BattlePassOcrZone Status { get; set; } = new(.7820886075949367, .2605523012552301, .15, .035);
    public double RowStep { get; set; } = .137;
    public void Normalize() { Title ??= new(); Description ??= new(); Progress ??= new(); Experience ??= new(); Status ??= new(); Title.Normalize(); Description.Normalize(); Progress.Normalize(); Experience.Normalize(); Status.Normalize(); RowStep = Math.Clamp(RowStep, .05, .2); }
    public BattlePassOcrLayout Clone() => new() { Title = Title.Clone(), Description = Description.Clone(), Progress = Progress.Clone(), Experience = Experience.Clone(), Status = Status.Clone(), RowStep = RowStep };
    public static BattlePassOcrLayout Default() => new();
    public static BattlePassOcrLayout FromLegacy(BattlePassSettings settings) => new() { Title = new(settings.TitleX, settings.FirstRowY, settings.TitleWidth, .047), Description = new(settings.TitleX, settings.FirstRowY + .052, settings.TitleWidth, .06), Progress = new(settings.ProgressX, settings.FirstRowY + .025, .09, .045), Experience = new(.64, settings.FirstRowY + .003, .09, .035), Status = new(.79, settings.FirstRowY + .003, .15, .035), RowStep = settings.RowStep };
}

internal sealed class BattlePassStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };
    private readonly string directory;
    private readonly string snapshotPath;
    private readonly string settingsPath;
    private readonly string manualTasksPath;
    private readonly string debugPath;

    public BattlePassStore()
    {
        directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppIdentity.DataDirectoryName);
        Directory.CreateDirectory(directory);
        snapshotPath = Path.Combine(directory, "battle-pass.json");
        settingsPath = Path.Combine(directory, "battle-pass-settings.json");
        manualTasksPath = Path.Combine(directory, "manual-tasks.json");
        debugPath = Path.Combine(directory, "battle-pass-debug.png");
    }

    public BattlePassSnapshot LoadSnapshot() => Load(snapshotPath, new BattlePassSnapshot());
    public BattlePassSettings LoadSettings()
    {
        var settings = Load(settingsPath, new BattlePassSettings());
        settings.Normalize();
        return settings;
    }

    public void SaveSnapshot(BattlePassSnapshot snapshot) => Save(snapshotPath, snapshot);
    public void SaveSettings(BattlePassSettings settings) { settings.Normalize(); Save(settingsPath, settings); }
    public List<ManualTask> LoadManualTasks()
    {
        var tasks = Load(manualTasksPath, new List<ManualTask>());
        foreach (var task in tasks) { task.Normalize(); task.IsEditing = false; }
        return tasks;
    }
    public void SaveManualTasks(IEnumerable<ManualTask> tasks)
    {
        var result = tasks.Select(task => { task.Normalize(); return task; }).ToList();
        Save(manualTasksPath, result);
    }
    public string? DebugScreenshotPath => File.Exists(debugPath) ? debugPath : null;
    public void SaveDebugScreenshot(System.Drawing.Bitmap bitmap)
    {
        bitmap.Save(debugPath, System.Drawing.Imaging.ImageFormat.Png);
    }

    private static T Load<T>(string path, T fallback) where T : class
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options) ?? fallback : fallback; }
        catch (Exception ex) { AppRuntimeLog.Error($"Could not load {Path.GetFileName(path)}", ex); return fallback; }
    }

    private static void Save<T>(string path, T value)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(value, Options));
        File.Move(temp, path, true);
    }
}
