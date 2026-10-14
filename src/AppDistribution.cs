using System.Diagnostics;

namespace CrosshairMarker;

internal static class AppDistribution
{
#if MICROSOFT_STORE
    public static bool IsMicrosoftStore => true;
#else
    public static bool IsMicrosoftStore => false;
#endif
    public const string StoreProductId = "9PDWXCKJWZFG";

    public static void OpenStore() => Process.Start(new ProcessStartInfo(
        "ms-windows-store://pdp/?ProductId=" + StoreProductId) { UseShellExecute = true });
}
