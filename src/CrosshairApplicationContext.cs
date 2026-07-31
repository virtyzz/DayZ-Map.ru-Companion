using System.Windows.Forms;

namespace CrosshairMarker;

internal sealed class CrosshairApplicationContext : ApplicationContext
{
    private readonly ConfigStore store;
    private readonly UpdateService updateService;
    private readonly OverlayForm overlay;
    private readonly TrayController tray;
    private readonly HotkeyManager hotkeys;
    private readonly DayZCompanionSettingsStore dayZSettingsStore;
    private DayZCompanionServer dayZCompanion;
    private DayZCompanionSettings dayZSettings;
    private EditorForm? editor;
    private AppConfig config;

    public CrosshairApplicationContext()
    {
        store = new ConfigStore();
        updateService = new UpdateService();
        config = store.Load();
        StartupManager.SetEnabled(config.StartWithWindows);
        overlay = new OverlayForm();
        overlay.ApplyMonitor(config.TargetMonitorDeviceName);
        overlay.ApplyWindowSize(config.OverlayWindowSize);
        overlay.ApplyProfile(config.CurrentProfile);

        if (config.OverlayVisible)
        {
            overlay.Show();
        }

        tray = new TrayController(
            onToggleOverlay: ToggleOverlay,
            onOpenEditor: OpenEditor,
            onOpenUpdates: OpenUpdates,
            onSelectProfile: SelectProfile,
            onExit: ExitApplication);
        tray.SetOverlayVisible(config.OverlayVisible);
        tray.SetProfiles(config.Profiles, config.ActiveProfileId);

        hotkeys = new HotkeyManager();
        RegisterConfiguredHotkeys();
        dayZSettingsStore = new DayZCompanionSettingsStore();
        dayZSettings = dayZSettingsStore.Load();
        if (DayZSettingsMigration.ApplyLegacyWindowBounds(config, dayZSettings))
        {
            dayZSettingsStore.Save(dayZSettings);
        }
        dayZCompanion = new DayZCompanionServer(dayZSettings);
        dayZCompanion.Start();
        _ = CheckForStartupUpdateAsync();

        if (!config.StartMinimizedToTray)
        {
            OpenEditor();
        }
    }

    private void ToggleOverlay()
    {
        config.OverlayVisible = !config.OverlayVisible;
        if (config.OverlayVisible)
        {
            overlay.Show();
        }
        else
        {
            overlay.Hide();
        }

        tray.SetOverlayVisible(config.OverlayVisible);
        store.SaveAtomic(config);
    }

    private void OpenEditor()
    {
        OpenEditor(null);
    }

    private void OpenUpdates()
    {
        OpenEditor("updates");
    }

    private void OpenEditor(string? initialTab)
    {
        if (editor is { IsDisposed: false })
        {
            if (!string.IsNullOrWhiteSpace(initialTab))
            {
                editor.OpenTab(initialTab);
            }
            editor.Activate();
            return;
        }

        editor = new EditorForm(config, updateService, dayZSettings, dayZCompanion.GetStatus(), initialTab);
        editor.ConfigChanged += nextConfig =>
        {
            var startupChanged = config.StartWithWindows != nextConfig.StartWithWindows;
            config = nextConfig;
            config.Normalize();
            if (startupChanged)
            {
                StartupManager.SetEnabled(config.StartWithWindows);
            }
            overlay.ApplyMonitor(config.TargetMonitorDeviceName);
            overlay.ApplyWindowSize(config.OverlayWindowSize);
            overlay.ApplyProfile(config.CurrentProfile);
            tray.SetProfiles(config.Profiles, config.ActiveProfileId);
            RegisterConfiguredHotkeys();
            store.SaveAtomic(config);
        };
        editor.MonitorChanged += deviceName =>
        {
            config.EditorMonitorDeviceName = deviceName;
            store.SaveAtomic(config);
        };
        editor.EditorBoundsChanged += bounds =>
        {
            dayZSettings.EditorWindowBounds = bounds;
            dayZSettingsStore.Save(dayZSettings);
            config.EditorWindowBounds = bounds;
            store.SaveAtomic(config);
        };
        editor.DayZSettingsChanged += nextSettings =>
        {
            nextSettings.Normalize();
            var restartHttp = dayZSettings.RequiresHttpRestart(nextSettings);
            dayZSettings.CopyFrom(nextSettings);
            dayZSettingsStore.Save(dayZSettings);
            if (restartHttp)
            {
                dayZCompanion.Dispose();
                dayZCompanion = new DayZCompanionServer(dayZSettings);
                dayZCompanion.Start();
            }
            editor?.ApplyDayZState(dayZSettings, dayZCompanion.GetStatus());
        };
        editor.DayZStatusRequested += () => editor?.ApplyDayZState(dayZSettings, dayZCompanion.GetStatus());
        editor.ExitRequested += ExitApplication;
        editor.Show();
    }

    private async Task CheckForStartupUpdateAsync()
    {
        var info = await updateService.GetLatestAsync();
        if (!info.IsUpdateAvailable || string.IsNullOrWhiteSpace(info.LatestVersion))
        {
            return;
        }

        if (string.Equals(config.LastPromptedUpdateVersion, info.LatestVersion, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        config.LastPromptedUpdateVersion = info.LatestVersion;
        store.SaveAtomic(config);

        var result = MessageBox.Show(
            $"Доступна новая версия {AppIdentity.DisplayName} {info.LatestVersion}.\n\nТекущая версия: {info.CurrentVersion}\n\nСкачать установщик?",
            $"Обновление {AppIdentity.DisplayName}",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Information);

        if (result == DialogResult.Yes)
        {
            UpdateService.OpenDownload(info);
        }
    }

    private void SelectProfile(string profileId)
    {
        if (config.Profiles.All(profile => profile.Id != profileId))
        {
            return;
        }

        config.ActiveProfileId = profileId;
        overlay.ApplyProfile(config.CurrentProfile);
        tray.SetProfiles(config.Profiles, config.ActiveProfileId);
        store.SaveAtomic(config);
    }

    private void RegisterConfiguredHotkeys()
    {
        hotkeys.UnregisterAll();
        RegisterHotkey(config.Hotkeys.ToggleOverlay, ToggleOverlay);
        RegisterHotkey(config.Hotkeys.NextProfile, () => SelectAdjacentProfile(1));
        RegisterHotkey(config.Hotkeys.PreviousProfile, () => SelectAdjacentProfile(-1));
        RegisterHotkey(config.Hotkeys.OpacityUp, () => AdjustOpacity(15));
        RegisterHotkey(config.Hotkeys.OpacityDown, () => AdjustOpacity(-15));
        RegisterHotkey(config.Hotkeys.SizeUp, () => AdjustSize(1));
        RegisterHotkey(config.Hotkeys.SizeDown, () => AdjustSize(-1));
    }

    private void RegisterHotkey(HotkeyBinding binding, Action action)
    {
        if (!binding.Enabled || !binding.TryGetKeys(out var key))
        {
            return;
        }

        hotkeys.Register(key, binding.ToModifiers(), action);
    }

    private void SelectAdjacentProfile(int direction)
    {
        if (config.Profiles.Count <= 1)
        {
            return;
        }

        var currentIndex = config.Profiles.FindIndex(profile => profile.Id == config.ActiveProfileId);
        if (currentIndex < 0)
        {
            currentIndex = 0;
        }

        var nextIndex = (currentIndex + direction + config.Profiles.Count) % config.Profiles.Count;
        SelectProfile(config.Profiles[nextIndex].Id);
    }

    private void AdjustOpacity(int delta)
    {
        MutateCurrentProfile(profile =>
        {
            var alpha = Math.Clamp(profile.Color.A + delta, 0, 255);
            profile.Color = profile.Color with { A = alpha };
        });
    }

    private void AdjustSize(int direction)
    {
        MutateCurrentProfile(profile =>
        {
            profile.Length = Math.Clamp(profile.Length + direction * 2, 1, 80);
            profile.Gap = Math.Clamp(profile.Gap + direction, 0, 50);
            profile.DotSize = Math.Clamp(profile.DotSize + direction, 1, 30);
        });
    }

    private void MutateCurrentProfile(Action<CrosshairProfile> mutation)
    {
        var profile = config.Profiles.FirstOrDefault(profile => profile.Id == config.ActiveProfileId);
        if (profile is null)
        {
            return;
        }

        mutation(profile);
        overlay.ApplyProfile(profile);
        store.SaveAtomic(config);

        if (editor is { IsDisposed: false })
        {
            editor.ApplyExternalConfig(config);
        }
    }

    private void ExitApplication()
    {
        store.SaveAtomic(config);
        dayZCompanion.Dispose();
        hotkeys.Dispose();
        tray.Dispose();
        overlay.Close();
        editor?.Close();
        ExitThread();
    }
}
