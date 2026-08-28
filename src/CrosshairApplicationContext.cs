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
    private readonly DayZEventNotifications eventNotifications;
    private readonly BattlePassStore battlePassStore;
    private readonly BattlePassTracker battlePassTracker;
    private readonly BattlePassOverlayForm battlePassOverlay;
    private readonly GlobalMouseClickInterceptor battlePassClickInterceptor;
    private BattlePassSettings battlePassSettings;
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
        battlePassStore = new BattlePassStore();
        battlePassSettings = battlePassStore.LoadSettings();
        battlePassTracker = new BattlePassTracker(battlePassStore);
        battlePassOverlay = new BattlePassOverlayForm();
        battlePassClickInterceptor = new GlobalMouseClickInterceptor(
            battlePassOverlay.TryInterceptActionMouseDown,
            battlePassOverlay.TryInterceptActionMouseUp);
        battlePassOverlay.SettingsChanged += SaveBattlePassSettings;
        battlePassOverlay.SettingsPersistRequested += PersistBattlePassSettings;
        battlePassOverlay.SnapshotChanged += snapshot =>
        {
            battlePassStore.SaveSnapshot(snapshot);
            editor?.ApplyBattlePassState(battlePassSettings, snapshot);
        };
        battlePassOverlay.ManualTasksChanged += tasks => battlePassStore.SaveManualTasks(tasks);
        battlePassOverlay.ScanRequested += page => ScanBattlePass(page);
        ApplyBattlePassOverlay();
        battlePassOverlay.SetEditing(battlePassSettings.OverlayEditingEnabled);
        if (battlePassSettings.OverlayVisible) battlePassOverlay.Show();
        RegisterConfiguredHotkeys();
        dayZSettingsStore = new DayZCompanionSettingsStore();
        dayZSettings = dayZSettingsStore.Load();
        if (DayZSettingsMigration.ApplyLegacyWindowBounds(config, dayZSettings))
        {
            dayZSettingsStore.Save(dayZSettings);
        }
        eventNotifications = new DayZEventNotifications(dayZSettings.EventNotifications);
        eventNotifications.Changed += OnEventNotificationsChanged;
        dayZCompanion = new DayZCompanionServer(dayZSettings, eventNotifications);
        dayZCompanion.Start();
        eventNotifications.Start();
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

    private void OpenBattlePassTasks()
    {
        using var form = new BattlePassTasksForm(battlePassStore);
        form.Changed += () =>
        {
            ApplyBattlePassOverlay();
            editor?.ApplyBattlePassState(battlePassSettings, battlePassStore.LoadSnapshot());
        };
        form.ShowDialog();
    }

    private void ClearBattlePass()
    {
        if (MessageBox.Show("Удалить все сохранённые задания Battle Pass?", "Battle Pass", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
        {
            return;
        }
        battlePassStore.SaveSnapshot(new BattlePassSnapshot());
        ApplyBattlePassOverlay();
        editor?.ApplyBattlePassState(battlePassSettings, battlePassStore.LoadSnapshot());
    }

    private void OpenBattlePassDebugScreenshot()
    {
        var path = battlePassStore.DebugScreenshotPath;
        if (path is null)
        {
            MessageBox.Show("Отладочный снимок ещё не создан. Включите его сохранение и выполните сканирование.", "Battle Pass", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
    }

    private void ToggleBattlePassOverlay()
    {
        battlePassSettings.OverlayVisible = !battlePassSettings.OverlayVisible;
        SaveBattlePassSettings(battlePassSettings);
    }

    private void ToggleBattlePassEditing()
    {
        if (!battlePassOverlay.Visible)
        {
            battlePassSettings.OverlayVisible = true;
            SaveBattlePassSettings(battlePassSettings);
        }
        battlePassSettings.OverlayEditingEnabled = !battlePassSettings.OverlayEditingEnabled;
        SaveBattlePassSettings(battlePassSettings);
    }

    private async void ScanBattlePass(BattlePassPage? requestedPage = null)
    {
        var wasVisible = battlePassOverlay.Visible;
        var originalBounds = battlePassOverlay.Bounds;
        if (wasVisible) battlePassOverlay.Hide();
        try
        {
            await Task.Delay(120);
            var result = await battlePassTracker.ScanAsync(requestedPage ?? battlePassSettings.CapturePage, battlePassSettings);
            ApplyBattlePassOverlay();
            battlePassOverlay.RestoreOverlayBounds(originalBounds);
            if (!result.Success)
            {
                MessageBox.Show(result.Message, "Battle Pass", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
        catch (Exception ex)
        {
            AppRuntimeLog.Error("Battle Pass scan failed", ex);
            MessageBox.Show(ex.Message, "Battle Pass", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            if (wasVisible && battlePassSettings.OverlayVisible)
            {
                battlePassOverlay.Show();
                battlePassOverlay.Refresh();
            }
        }
    }

    private void SaveBattlePassSettings(BattlePassSettings next)
    {
        battlePassSettings = next.Clone();
        battlePassSettings.Normalize();
        battlePassStore.SaveSettings(battlePassSettings);
        ApplyBattlePassOverlay();
        battlePassOverlay.SetEditing(battlePassSettings.OverlayEditingEnabled);
        if (battlePassSettings.OverlayVisible && !battlePassOverlay.Visible) battlePassOverlay.Show();
        if (!battlePassSettings.OverlayVisible && battlePassOverlay.Visible) battlePassOverlay.Hide();
        editor?.ApplyBattlePassState(battlePassSettings, battlePassStore.LoadSnapshot());
    }

    private void PersistBattlePassSettings(BattlePassSettings next)
    {
        battlePassSettings = next.Clone();
        battlePassSettings.Normalize();
        battlePassStore.SaveSettings(battlePassSettings);
        editor?.ApplyBattlePassState(battlePassSettings, battlePassStore.LoadSnapshot());
    }

    private void HandleBattlePassCommand(string command)
    {
        switch (command)
        {
            case "scanBattlePass": ScanBattlePass(); break;
            case "editBattlePassTasks": OpenBattlePassTasks(); break;
            case "clearBattlePass": ClearBattlePass(); break;
            case "showBattlePassDebug": OpenBattlePassDebugScreenshot(); break;
            case "previewBattlePassZones": PreviewBattlePassZones(); break;
            case "calibrateBattlePassZones": CalibrateBattlePassZones(); break;
            case "resetBattlePassOverlayBounds": ResetBattlePassOverlayBounds(); break;
        }
    }

    private void ResetBattlePassOverlayBounds()
    {
        battlePassSettings.ResetOverlayBounds();
        SaveBattlePassSettings(battlePassSettings);
    }

    private void CalibrateBattlePassZones()
    {
        var wasVisible = battlePassOverlay.Visible;
        if (wasVisible) battlePassOverlay.Hide();
        try
        {
            using var screenshot = battlePassTracker.CaptureCalibrationImage(battlePassSettings);
            using var form = new BattlePassCalibrationForm((Bitmap)screenshot.Clone(), battlePassSettings);
            form.Saved += SaveBattlePassSettings;
            form.ShowDialog();
        }
        catch (Exception ex)
        {
            AppRuntimeLog.Error("Battle Pass zones calibration failed", ex);
            MessageBox.Show(ex.Message, "Battle Pass", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            if (wasVisible && battlePassSettings.OverlayVisible) battlePassOverlay.Show();
        }
    }

    private void PreviewBattlePassZones()
    {
        var wasVisible = battlePassOverlay.Visible;
        if (wasVisible) battlePassOverlay.Hide();
        try
        {
            battlePassTracker.CreateZonesPreview(battlePassSettings);
            OpenBattlePassDebugScreenshot();
        }
        catch (Exception ex)
        {
            AppRuntimeLog.Error("Battle Pass zones preview failed", ex);
            MessageBox.Show(ex.Message, "Battle Pass", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            if (wasVisible && battlePassSettings.OverlayVisible) battlePassOverlay.Show();
        }
    }

    private void ApplyBattlePassOverlay() => battlePassOverlay.Apply(battlePassSettings, battlePassStore.LoadSnapshot(), battlePassStore.LoadManualTasks());

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

        editor = new EditorForm(config, updateService, dayZSettings, dayZCompanion.GetStatus(), eventNotifications, battlePassSettings, battlePassStore.LoadSnapshot(), initialTab);
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
            editor?.ApplyExternalConfig(config);
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
            if (dayZSettings.EventNotifications.Enabled) eventNotifications.Start(); else eventNotifications.Stop();
            if (restartHttp)
            {
                dayZCompanion.Dispose();
                dayZCompanion = new DayZCompanionServer(dayZSettings, eventNotifications);
                dayZCompanion.Start();
            }
            editor?.ApplyDayZState(dayZSettings, dayZCompanion.GetStatus());
        };
        editor.DayZStatusRequested += () => editor?.ApplyDayZState(dayZSettings, dayZCompanion.GetStatus());
        editor.BattlePassSettingsChanged += SaveBattlePassSettings;
        editor.BattlePassCommandRequested += HandleBattlePassCommand;
        editor.ExitRequested += ExitApplication;
        editor.Show();
    }

    private void OnEventNotificationsChanged()
    {
        dayZSettingsStore.Save(dayZSettings);
        editor?.ApplyEventNotificationState(eventNotifications.Settings, eventNotifications.IsMonitoring);
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
        config.HotkeyRegistrationErrors.Clear();
        var signatures = new HashSet<string>(StringComparer.Ordinal);
        RegisterHotkey(nameof(HotkeyBindings.ToggleOverlay), config.Hotkeys.ToggleOverlay, ToggleOverlay, signatures);
        RegisterHotkey(nameof(HotkeyBindings.NextProfile), config.Hotkeys.NextProfile, () => SelectAdjacentProfile(1), signatures);
        RegisterHotkey(nameof(HotkeyBindings.PreviousProfile), config.Hotkeys.PreviousProfile, () => SelectAdjacentProfile(-1), signatures);
        RegisterHotkey(nameof(HotkeyBindings.OpacityUp), config.Hotkeys.OpacityUp, () => AdjustOpacity(15), signatures);
        RegisterHotkey(nameof(HotkeyBindings.OpacityDown), config.Hotkeys.OpacityDown, () => AdjustOpacity(-15), signatures);
        RegisterHotkey(nameof(HotkeyBindings.SizeUp), config.Hotkeys.SizeUp, () => AdjustSize(1), signatures);
        RegisterHotkey(nameof(HotkeyBindings.SizeDown), config.Hotkeys.SizeDown, () => AdjustSize(-1), signatures);
        RegisterHotkey(nameof(HotkeyBindings.ToggleBattlePassOverlay), config.Hotkeys.ToggleBattlePassOverlay, ToggleBattlePassOverlay, signatures);
        RegisterHotkey(nameof(HotkeyBindings.ScanBattlePass), config.Hotkeys.ScanBattlePass, () => ScanBattlePass(), signatures);
        RegisterHotkey(nameof(HotkeyBindings.ToggleBattlePassDescriptions), config.Hotkeys.ToggleBattlePassDescriptions, ToggleBattlePassDescriptions, signatures);
    }

    private void ToggleBattlePassDescriptions()
    {
        battlePassSettings.ShowTaskDescriptions = !battlePassSettings.ShowTaskDescriptions;
        SaveBattlePassSettings(battlePassSettings);
    }

    private void RegisterHotkey(string name, HotkeyBinding binding, Action action, ISet<string> signatures)
    {
        if (!binding.Enabled || !binding.TryGetKeys(out var key))
        {
            return;
        }

        if (!signatures.Add(binding.Signature))
        {
            config.HotkeyRegistrationErrors[name] = "Занята другой горячей клавишей Companion.";
            return;
        }
        if (!hotkeys.Register(key, binding.ToModifiers(), action))
        {
            config.HotkeyRegistrationErrors[name] = "Недоступна: занята Windows или другим приложением.";
        }
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
        battlePassStore.SaveSettings(battlePassSettings);
        dayZCompanion.Dispose();
        eventNotifications.Dispose();
        battlePassClickInterceptor.Dispose();
        hotkeys.Dispose();
        tray.Dispose();
        overlay.Close();
        battlePassOverlay.Close();
        editor?.Close();
        ExitThread();
    }
}
