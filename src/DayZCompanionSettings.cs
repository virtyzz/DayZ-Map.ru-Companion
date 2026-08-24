using System.Text.Json;

namespace CrosshairMarker;

internal sealed class DayZCompanionSettings
{
    public string? PrivateMarkersPath { get; set; }
    public bool AutoPort { get; set; } = true;
    public int Port { get; set; } = 49950;
    public bool AllowDevelopmentOrigin { get; set; }
    public bool BlockWritesWhenDayZRunning { get; set; }
    public int BackupLimit { get; set; } = 20;
    public int BackupMaxAgeDays { get; set; } = 90;
    public EditorWindowBounds? EditorWindowBounds { get; set; }
    public DayZEventNotificationSettings EventNotifications { get; set; } = new();

    public void Normalize()
    {
        BackupLimit = Math.Clamp(BackupLimit, 1, 100);
        BackupMaxAgeDays = Math.Clamp(BackupMaxAgeDays, 1, 3650);
        EventNotifications ??= new DayZEventNotificationSettings();
        EventNotifications.Normalize();
        EditorWindowBounds?.Normalize();
        if (!string.IsNullOrWhiteSpace(PrivateMarkersPath))
        {
            PrivateMarkersPath = Path.GetFullPath(PrivateMarkersPath);
        }
    }

    public bool RequiresHttpRestart(DayZCompanionSettings next) =>
        AutoPort != next.AutoPort || Port != next.Port;

    public void CopyFrom(DayZCompanionSettings source)
    {
        PrivateMarkersPath = source.PrivateMarkersPath;
        AutoPort = source.AutoPort;
        Port = source.Port;
        AllowDevelopmentOrigin = source.AllowDevelopmentOrigin;
        BlockWritesWhenDayZRunning = source.BlockWritesWhenDayZRunning;
        BackupLimit = source.BackupLimit;
        BackupMaxAgeDays = source.BackupMaxAgeDays;
        EditorWindowBounds = source.EditorWindowBounds?.Clone();
        EventNotifications.CopyFrom(source.EventNotifications);
        Normalize();
    }
}

internal sealed class DayZCompanionSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string path;

    public DayZCompanionSettingsStore()
        : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DayZMarkerCompanion"))
    {
    }

    internal DayZCompanionSettingsStore(string directory)
    {
        Directory.CreateDirectory(directory);
        path = Path.Combine(directory, "settings.json");
    }

    internal string SettingsPath => path;

    public DayZCompanionSettings Load()
    {
        try
        {
            var settings = File.Exists(path)
                ? JsonSerializer.Deserialize<DayZCompanionSettings>(File.ReadAllText(path), JsonOptions) ?? new DayZCompanionSettings()
                : new DayZCompanionSettings();
            settings.Normalize();
            if (!File.Exists(path)) Save(settings);
            return settings;
        }
        catch (Exception ex)
        {
            AppRuntimeLog.Error("Could not load DayZ Companion settings", ex);
            var fallback = new DayZCompanionSettings();
            fallback.Normalize();
            try
            {
                Save(fallback);
            }
            catch (Exception saveEx)
            {
                AppRuntimeLog.Error("Could not restore default DayZ Companion settings", saveEx);
            }
            return fallback;
        }
    }

    public void Save(DayZCompanionSettings settings)
    {
        settings.Normalize();
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings, JsonOptions));
        File.Move(temp, path, true);
    }
}
