using System.Reflection;

namespace CrosshairMarker;

internal static class AppIcons
{
    public static Icon MainIcon() => Load("DayZMapCompanion.MainIcon");

    public static Icon Tray() => Load("DayZMapCompanion.TrayIcon");

    private static Icon Load(string resourceName)
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Icon resource '{resourceName}' was not found.");
        using var icon = new Icon(stream);
        return (Icon)icon.Clone();
    }
}
