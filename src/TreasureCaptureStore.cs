using System.Text.Json;

namespace CrosshairMarker;

internal sealed class TreasureCaptureStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string directory;
    private readonly string queuePath;
    private readonly string imagesDirectory;

    public TreasureCaptureStore()
        : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DayZMarkerCompanion", "treasures"))
    {
    }

    internal TreasureCaptureStore(string directory)
    {
        this.directory = directory;
        queuePath = Path.Combine(directory, "queue.json");
        imagesDirectory = Path.Combine(directory, "captures");
        Directory.CreateDirectory(imagesDirectory);
    }

    public List<TreasureCapture> Load()
    {
        try
        {
            if (!File.Exists(queuePath)) return [];
            return JsonSerializer.Deserialize<List<TreasureCapture>>(File.ReadAllText(queuePath), JsonOptions) ?? [];
        }
        catch (Exception ex)
        {
            AppRuntimeLog.Error("Could not load treasure capture queue", ex);
            return [];
        }
    }

    public void Save(IEnumerable<TreasureCapture> captures)
    {
        Directory.CreateDirectory(directory);
        var temporary = queuePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(captures, JsonOptions));
        File.Move(temporary, queuePath, true);
    }

    public string CreateImagePath(string id) => Path.Combine(imagesDirectory, id + ".png");

    public string ImagesDirectory => imagesDirectory;
}
