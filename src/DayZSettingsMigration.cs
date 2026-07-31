namespace CrosshairMarker;

internal static class DayZSettingsMigration
{
    public static bool ApplyLegacyWindowBounds(AppConfig crosslayConfig, DayZCompanionSettings companionSettings)
    {
        if (companionSettings.EditorWindowBounds is not null || crosslayConfig.EditorWindowBounds is null)
        {
            return false;
        }

        companionSettings.EditorWindowBounds = crosslayConfig.EditorWindowBounds.Clone();
        companionSettings.Normalize();
        return true;
    }
}
