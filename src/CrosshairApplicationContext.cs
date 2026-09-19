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
    private readonly BattlePassStore battlePassStore;
    private readonly BattlePassTracker battlePassTracker;
    private readonly BattlePassOverlayForm battlePassOverlay;
    private readonly GlobalMouseClickInterceptor battlePassClickInterceptor;
    private BattlePassSettings battlePassSettings;
    private EditorForm? editor;
    private readonly TreasureCaptureStore treasureStore;
    private readonly TreasureCaptureService treasureCaptureService;
    private readonly TreasureMapBridge treasureMapBridge;
    private readonly PlayerPositionMapBridge playerPositionMapBridge;
    private readonly PlayerPositionCaptureService playerPositionCaptureService;
    private readonly PlayerPositionTrackingController playerPositionTracker;
    private readonly object playerPositionSettingsSync = new();
    private readonly System.Windows.Forms.Timer updateTimer = new() { Interval = 60 * 60 * 1000 };
    private bool updateCheckInProgress;
    private bool exitInProgress;
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
            onOpenGeneral: OpenGeneral,
            onOpenTreasures: OpenTreasureCaptures,
            onOpenPlayerPosition: OpenPlayerPosition,
            onOpenTasks: OpenTasks,
            onOpenCrosshair: OpenCrosshair,
            onExit: ExitApplication);

        hotkeys = new HotkeyManager();
        treasureStore = new TreasureCaptureStore();
        treasureCaptureService = new TreasureCaptureService(treasureStore);
        treasureMapBridge = new TreasureMapBridge();
        playerPositionMapBridge = new PlayerPositionMapBridge();
        playerPositionCaptureService = new PlayerPositionCaptureService();
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
        playerPositionMapBridge.SetTrackingEnabled(dayZSettings.PlayerPositionTracking.Enabled);
        playerPositionMapBridge.SessionChanged += session =>
        {
            lock (playerPositionSettingsSync)
            {
                var tracking = dayZSettings.PlayerPositionTracking;
                var map = session.Maps.SingleOrDefault(item => string.Equals(item.Id, tracking.MapId, StringComparison.Ordinal)
                    || string.Equals(item.Id, tracking.MapId, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(item.Name, tracking.MapId, StringComparison.OrdinalIgnoreCase));
                if (map is null && session.Maps.Count == 1) map = session.Maps[0];
                if (map is null || string.Equals(tracking.MapId, map.Id, StringComparison.Ordinal)) return;
                tracking.MapId = map.Id;
                tracking.LastError = "";
                dayZSettingsStore.Save(dayZSettings);
                AppRuntimeLog.Info($"Player position map destination synchronized: {map.Id}.");
            }
        };
        playerPositionMapBridge.Delivered += update =>
        {
            lock (playerPositionSettingsSync)
            {
                if (!string.Equals(dayZSettings.PlayerPositionTracking.TrackingId, update.TrackingId, StringComparison.Ordinal)) return;
                dayZSettings.PlayerPositionTracking.LastSentAt = DateTimeOffset.Now;
                dayZSettings.PlayerPositionTracking.LastError = "";
                dayZSettingsStore.Save(dayZSettings);
            }
        };
        playerPositionTracker = new PlayerPositionTrackingController(playerPositionCaptureService, playerPositionMapBridge, () => dayZSettings.PlayerPositionTracking.Clone(), settings =>
        {
            lock (playerPositionSettingsSync)
            {
                var current = dayZSettings.PlayerPositionTracking;
                // Delivery can be acknowledged while OCR is running. Do not let
                // the older OCR snapshot erase that acknowledgement.
                if (current.LastSentAt > settings.LastSentAt) settings.LastSentAt = current.LastSentAt;
                dayZSettings.PlayerPositionTracking = settings.Clone();
                dayZSettingsStore.Save(dayZSettings);
            }
        });
        if (DayZSettingsMigration.ApplyLegacyWindowBounds(config, dayZSettings))
        {
            dayZSettingsStore.Save(dayZSettings);
        }
        dayZCompanion = new DayZCompanionServer(dayZSettings, treasureMapBridge, playerPositionMapBridge);
        dayZCompanion.Start();
        playerPositionTracker.Refresh();
        updateTimer.Tick += async (_, _) => await CheckForUpdateAsync();
        updateTimer.Start();
        _ = CheckForUpdateAsync();

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

    private void OpenGeneral()
    {
        OpenEditor("general");
    }

    private void OpenTasks() => OpenEditor("tasks");

    private void OpenCrosshair() => OpenEditor("crosshair");

    private void OpenTreasureCaptures()
    {
        OpenEditor("treasures");
    }

    private void OpenPlayerPosition() => OpenEditor("player-position");

    private void CaptureTreasure()
    {
        // The frame must be captured while DayZ still owns the foreground.
        // Opening the editor first makes fullscreen DayZ show its pause menu
        // before the screen snapshot can be taken.
        var capture = treasureCaptureService.CaptureImage();
        if (capture is null) return;
        var captures = treasureStore.Load();
        captures.Insert(0, capture);
        treasureStore.Save(captures);

        var editorWasOpen = editor is { IsDisposed: false };
        OpenEditor("treasures");
        if (editorWasOpen && editor is not null) editor.QueueTreasureRecognition(capture.Id);
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

        editor = new EditorForm(config, updateService, dayZSettings, dayZCompanion.GetStatus(), battlePassSettings, battlePassStore.LoadSnapshot(), treasureStore, treasureCaptureService, treasureMapBridge, playerPositionMapBridge, playerPositionCaptureService, initialTab);
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
            bool restartHttp;
            lock (playerPositionSettingsSync)
            {
                var runtime = dayZSettings.PlayerPositionTracking.Clone();
                restartHttp = dayZSettings.RequiresHttpRestart(nextSettings);
                dayZSettings.CopyFrom(nextSettings);
                // The editor changes configuration, not telemetry reported by
                // the OCR worker or map acknowledgement.
                dayZSettings.PlayerPositionTracking.LastX = runtime.LastX;
                dayZSettings.PlayerPositionTracking.LastY = runtime.LastY;
                dayZSettings.PlayerPositionTracking.LastZ = runtime.LastZ;
                dayZSettings.PlayerPositionTracking.LastSentAt = runtime.LastSentAt;
                dayZSettings.PlayerPositionTracking.ConsecutiveErrors = runtime.ConsecutiveErrors;
                dayZSettings.PlayerPositionTracking.LastError = runtime.LastError;
                playerPositionMapBridge.SetTrackingEnabled(dayZSettings.PlayerPositionTracking.Enabled);
                dayZSettingsStore.Save(dayZSettings);
            }
            if (restartHttp)
            {
                dayZCompanion.Dispose();
                dayZCompanion = new DayZCompanionServer(dayZSettings, treasureMapBridge, playerPositionMapBridge);
                dayZCompanion.Start();
            }
            playerPositionTracker.Refresh();
            editor?.ApplyDayZState(dayZSettings, dayZCompanion.GetStatus());
        };
        editor.DayZStatusRequested += () => editor?.ApplyDayZState(dayZSettings, dayZCompanion.GetStatus());
        editor.PlayerPositionRegionRequested += () => editor?.SelectPlayerPositionRegion();
        editor.BattlePassSettingsChanged += SaveBattlePassSettings;
        editor.BattlePassCommandRequested += HandleBattlePassCommand;
        editor.ExitRequested += ExitApplication;
        editor.Show();
    }

    private async Task CheckForUpdateAsync()
    {
        if (updateCheckInProgress) return;
        updateCheckInProgress = true;
        try
        {
            var info = await updateService.GetLatestAsync(forceRefresh: true);
            var now = DateTimeOffset.Now;
            if (!UpdateService.ShouldShowReminder(info, config.LastPromptedUpdateVersion, config.LastUpdatePromptAt, now)) return;

            config.LastPromptedUpdateVersion = info.LatestVersion;
            config.LastUpdatePromptAt = now;
            store.SaveAtomic(config);

            var result = MessageBox.Show(
                $"Доступна новая версия {AppIdentity.DisplayName} {info.LatestVersion}.\n\nТекущая версия: {info.CurrentVersion}\n\nСкачать установщик?",
                $"Обновление {AppIdentity.DisplayName}",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Information);

            if (result == DialogResult.Yes) UpdateService.OpenDownload(info);
        }
        finally
        {
            updateCheckInProgress = false;
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
        RegisterHotkey(nameof(HotkeyBindings.CaptureTreasure), config.Hotkeys.CaptureTreasure, CaptureTreasure, signatures);
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
        if (exitInProgress)
        {
            return;
        }

        exitInProgress = true;
        AppRuntimeLog.Info("Application shutdown requested.");
        try
        {
            ShutdownStep("player position tracker", () => playerPositionTracker.Dispose());
            ShutdownStep("update timer", () => { updateTimer.Stop(); updateTimer.Dispose(); });
            ShutdownStep("application settings", () => store.SaveAtomic(config));
            ShutdownStep("Battle Pass settings", () => battlePassStore.SaveSettings(battlePassSettings));
            ShutdownStep("DayZ Companion HTTP service", () => dayZCompanion.Dispose());
            ShutdownStep("Battle Pass mouse interceptor", () => battlePassClickInterceptor.Dispose());
            ShutdownStep("hotkeys", () => hotkeys.Dispose());
            ShutdownStep("tray icon", () => tray.Dispose());
            ShutdownStep("crosshair overlay", () => overlay.Close());
            ShutdownStep("Battle Pass overlay", () => battlePassOverlay.Close());
            ShutdownStep("editor window", () => editor?.Close());
        }
        finally
        {
            AppRuntimeLog.Info("Application shutdown completed; stopping UI message loop.");
            ExitThread();
        }
    }

    private static void ShutdownStep(string name, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            AppRuntimeLog.Error($"Could not shut down {name}", ex);
        }
    }
}
