using System.Drawing;
using System.Windows.Forms;

namespace CrosshairMarker;

internal sealed class MonitorInfo
{
    public required string DeviceName { get; init; }
    public required string DisplayName { get; init; }
    public required Rectangle Bounds { get; init; }
    public required bool Primary { get; init; }

    public override string ToString() => DisplayName;

    public static List<MonitorInfo> GetAll()
    {
        return Screen.AllScreens.Select((screen, index) => new MonitorInfo
        {
            DeviceName = screen.DeviceName,
            DisplayName = $"{index + 1}: {screen.Bounds.Width}x{screen.Bounds.Height}"
                + (screen.Primary ? " основной" : "")
                + $" ({screen.DeviceName})",
            Bounds = screen.Bounds,
            Primary = screen.Primary
        }).ToList();
    }

    public static Screen ResolveScreen(string? deviceName)
    {
        if (!string.IsNullOrWhiteSpace(deviceName))
        {
            var selected = Screen.AllScreens.FirstOrDefault(screen => screen.DeviceName == deviceName);
            if (selected is not null)
            {
                return selected;
            }
        }

        return Screen.PrimaryScreen ?? Screen.AllScreens.First();
    }
}
