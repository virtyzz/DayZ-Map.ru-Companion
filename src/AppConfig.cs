using System.Drawing;
using System.Text.Json.Serialization;

namespace CrosshairMarker;

internal sealed class AppConfig
{
    public bool OverlayVisible { get; set; } = true;
    public bool StartWithWindows { get; set; }
    public bool StartMinimizedToTray { get; set; } = true;
    public string? TargetMonitorDeviceName { get; set; }
    public OverlayWindowSize OverlayWindowSize { get; set; } = OverlayWindowSize.Compact200;
    public string? EditorMonitorDeviceName { get; set; }
    public EditorWindowBounds? EditorWindowBounds { get; set; }
    public string ActiveProfileId { get; set; } = "default";
    public string? LastPromptedUpdateVersion { get; set; }
    public bool ScanBattlePassHotkeyResetApplied { get; set; }
    public List<CrosshairProfile> Profiles { get; set; } = [CrosshairProfile.Default()];
    public HotkeyBindings Hotkeys { get; set; } = new();

    [JsonIgnore]
    public Dictionary<string, string> HotkeyRegistrationErrors { get; set; } = [];

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CrosshairProfile? ActiveProfile { get; set; }

    [JsonIgnore]
    public CrosshairProfile CurrentProfile => Profiles.FirstOrDefault(profile => profile.Id == ActiveProfileId)
        ?? Profiles.FirstOrDefault()
        ?? CrosshairProfile.Default();

    public void Normalize()
    {
        if (ActiveProfile is not null && Profiles.All(profile => profile.Id != ActiveProfile.Id))
        {
            Profiles = [ActiveProfile.Clone()];
            ActiveProfileId = ActiveProfile.Id;
        }

        ActiveProfile = null;

        if (Profiles.Count == 0)
        {
            Profiles.Add(CrosshairProfile.Default());
        }

        Hotkeys ??= new HotkeyBindings();
        Hotkeys.Normalize();
        if (!ScanBattlePassHotkeyResetApplied)
        {
            Hotkeys.ScanBattlePass = HotkeyBindings.DefaultScanBattlePass();
            ScanBattlePassHotkeyResetApplied = true;
        }

        if (!Enum.IsDefined(OverlayWindowSize))
        {
            OverlayWindowSize = OverlayWindowSize.Compact200;
        }

        EditorWindowBounds?.Normalize();

        foreach (var profile in Profiles)
        {
            if (string.IsNullOrWhiteSpace(profile.Id))
            {
                profile.Id = Guid.NewGuid().ToString("N");
            }
            if (string.IsNullOrWhiteSpace(profile.Name))
            {
                profile.Name = "Прицел";
            }
            profile.CrosshairRotation = NormalizeAngle(profile.CrosshairRotation);
            if (!Enum.IsDefined(profile.DotShape))
            {
                profile.DotShape = DotShape.Circle;
            }
            if (!Enum.IsDefined(profile.CrosshairShape))
            {
                profile.CrosshairShape = CrosshairShape.Classic;
            }
            profile.DotOpacity = Math.Clamp(profile.DotOpacity, 0, 255);
            if (profile.DotColor == default)
            {
                profile.DotColor = profile.Color with { A = profile.DotOpacity };
            }
            profile.ImageLayer ??= new ImageLayer();
            profile.ImageLayer.Rotation = NormalizeAngle(profile.ImageLayer.Rotation);
        }

        if (Profiles.All(profile => profile.Id != ActiveProfileId))
        {
            ActiveProfileId = Profiles[0].Id;
        }
    }

    public AppConfig Clone() => new()
    {
        OverlayVisible = OverlayVisible,
        StartWithWindows = StartWithWindows,
        StartMinimizedToTray = StartMinimizedToTray,
        TargetMonitorDeviceName = TargetMonitorDeviceName,
        OverlayWindowSize = OverlayWindowSize,
        EditorMonitorDeviceName = EditorMonitorDeviceName,
        EditorWindowBounds = EditorWindowBounds?.Clone(),
        ActiveProfileId = ActiveProfileId,
        LastPromptedUpdateVersion = LastPromptedUpdateVersion,
        ScanBattlePassHotkeyResetApplied = ScanBattlePassHotkeyResetApplied,
        Profiles = Profiles.Select(profile => profile.Clone()).ToList(),
        Hotkeys = Hotkeys.Clone(),
        HotkeyRegistrationErrors = new Dictionary<string, string>(HotkeyRegistrationErrors)
    };

    private static int NormalizeAngle(int angle)
    {
        var normalized = angle % 360;
        return normalized < 0 ? normalized + 360 : normalized;
    }
}

internal enum OverlayWindowSize
{
    Compact200,
    QuarterScreen,
    HalfScreen,
    ThreeQuartersScreen,
    FullScreen
}

internal sealed class CrosshairProfile
{
    public string Id { get; set; } = "default";
    public string Name { get; set; } = "По умолчанию";
    public int Length { get; set; } = 18;
    public int Gap { get; set; } = 7;
    public int Thickness { get; set; } = 3;
    public int DotSize { get; set; } = 3;
    public int DotOpacity { get; set; } = 230;
    public int CrosshairRotation { get; set; }
    public CrosshairShape CrosshairShape { get; set; } = CrosshairShape.Classic;
    public bool ProceduralEnabled { get; set; } = true;
    public bool CrosshairEnabled { get; set; } = true;
    public bool DotEnabled { get; set; } = true;
    public DotShape DotShape { get; set; } = DotShape.Circle;
    public bool TShape { get; set; }
    public bool OutlineEnabled { get; set; } = true;
    public int OutlineThickness { get; set; } = 1;
    public ColorRgba Color { get; set; } = new(0, 255, 120, 230);
    public ColorRgba DotColor { get; set; } = new(0, 255, 120, 230);
    public ColorRgba OutlineColor { get; set; } = new(0, 0, 0, 180);
    public ImageLayer ImageLayer { get; set; } = new();

    [JsonIgnore]
    public float Opacity => Color.A / 255f;

    public static CrosshairProfile Default() => new();

    public static CrosshairProfile Create(string name) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Name = name
    };

    public CrosshairProfile Clone() => new()
    {
        Id = Id,
        Name = Name,
        Length = Length,
        Gap = Gap,
        Thickness = Thickness,
        DotSize = DotSize,
        DotOpacity = DotOpacity,
        CrosshairRotation = CrosshairRotation,
        CrosshairShape = CrosshairShape,
        ProceduralEnabled = ProceduralEnabled,
        CrosshairEnabled = CrosshairEnabled,
        DotEnabled = DotEnabled,
        DotShape = DotShape,
        TShape = TShape,
        OutlineEnabled = OutlineEnabled,
        OutlineThickness = OutlineThickness,
        Color = Color,
        DotColor = DotColor,
        OutlineColor = OutlineColor,
        ImageLayer = ImageLayer.Clone()
    };
}

internal sealed class ImageLayer
{
    public bool Enabled { get; set; }
    public string? Path { get; set; }
    public int ScalePercent { get; set; } = 100;
    public int Opacity { get; set; } = 255;
    public int Rotation { get; set; }
    public int OffsetX { get; set; }
    public int OffsetY { get; set; }
    public int? AnchorX { get; set; }
    public int? AnchorY { get; set; }

    [JsonIgnore]
    public bool HasImage => !string.IsNullOrWhiteSpace(Path) && File.Exists(Path);

    public ImageLayer Clone() => new()
    {
        Enabled = Enabled,
        Path = Path,
        ScalePercent = ScalePercent,
        Opacity = Opacity,
        Rotation = Rotation,
        OffsetX = OffsetX,
        OffsetY = OffsetY,
        AnchorX = AnchorX,
        AnchorY = AnchorY
    };
}

internal readonly record struct ColorRgba(int R, int G, int B, int A)
{
    public Color ToColor() => Color.FromArgb(A, R, G, B);
}

internal enum DotShape
{
    Circle,
    Square,
    Diamond,
    Triangle,
    Heart,
    Cross,
    Star,
    Hexagon,
    Pentagon,
    X,
    Paw
}

internal enum CrosshairShape
{
    Classic,
    TightPlus,
    OpenPlus,
    Corners,
    Box,
    Circle,
    Arcs,
    Chevron,
    Brackets,
    DotCorners,
    Sniper
}
