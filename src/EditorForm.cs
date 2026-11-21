using System.Drawing;
using System.Drawing.Imaging;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace CrosshairMarker;

internal sealed class EditorForm : Form
{
    private const int CaptionStyle = 0x00C00000;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly AssetStore assetStore = new();
    private readonly UpdateService updateService;
    private readonly TreasureCaptureStore treasureStore;
    private readonly TreasureCaptureService treasureCaptureService;
    private readonly TreasureMapBridge treasureMapBridge;
    private readonly PlayerPositionMapBridge playerPositionMapBridge;
    private readonly PlayerPositionCaptureService playerPositionCaptureService;
    private readonly WebView2 webView = new();
    private bool integratedCaption;
    private bool captionMaximized;
    private WebView2? sessionMapView;
    private Task? sessionMapInitialization;
    private string? sessionMapAddress;
    private string? requestedSessionMap;
    private bool sessionMapLoaded;
    private readonly System.Windows.Forms.Timer dayZStatusTimer = new() { Interval = 2500 };
    private AppConfig config;
    private DayZCompanionSettings dayZSettings;
    private DayZCompanionStatus dayZStatus;
    private PerformanceSnapshot performanceSnapshot = new();
    private OcrStatus? ocrStatus;
    private readonly System.Windows.Forms.Timer ocrInstallPollTimer = new() { Interval = 5000 };
    private int ocrInstallPollAttempts;
    private Size previewSize = new(720, 420);
    private BattlePassSettings battlePassSettings;
    private BattlePassSnapshot battlePassSnapshot;
    private readonly BattlePassStore battlePassStore = new();
    private UpdateInfo? updateInfo;
    private string? pendingTab;
    private bool webReady;
    private bool treasureCaptureInProgress;
    private readonly Queue<string> treasureOcrQueue = new();
    private bool treasureOcrWorkerRunning;
    private string? treasureFeedback;

    public event Action<AppConfig>? ConfigChanged;
    public event Action<string?>? MonitorChanged;
    public event Action<EditorWindowBounds>? EditorBoundsChanged;
    public event Action? ExitRequested;
    public event Action? UpdateRequested;
    public event Action? UpdateCancelRequested;
    public event Action<DayZCompanionSettings>? DayZSettingsChanged;
    public event Func<Task>? PlayerPositionRegionRequested;
    private bool selectingPlayerPositionRegion;
    public event Action? DayZStatusRequested;
    public event Action<BattlePassSettings>? BattlePassSettingsChanged;
    public event Action<string>? BattlePassCommandRequested;
    public event Action<string, ManualTask>? ManualTaskCommandRequested;

    public EditorForm(AppConfig source, UpdateService updateService, DayZCompanionSettings dayZSettings, DayZCompanionStatus dayZStatus, BattlePassSettings battlePassSettings, BattlePassSnapshot battlePassSnapshot, TreasureCaptureStore treasureStore, TreasureCaptureService treasureCaptureService, TreasureMapBridge treasureMapBridge, PlayerPositionMapBridge playerPositionMapBridge, PlayerPositionCaptureService playerPositionCaptureService, string? initialTab = null)
    {
        this.updateService = updateService;
        updateService.ProgressChanged += OnUpdateProgressChanged;
        this.treasureStore = treasureStore;
        this.treasureCaptureService = treasureCaptureService;
        this.treasureMapBridge = treasureMapBridge;
        this.playerPositionMapBridge = playerPositionMapBridge;
        this.playerPositionCaptureService = playerPositionCaptureService;
        foreach (var capture in treasureStore.Load().Where(item => item.Status is TreasureRecognitionStatus.Queued or TreasureRecognitionStatus.Recognizing)) treasureOcrQueue.Enqueue(capture.Id);
        _ = ProcessTreasureQueueAsync();
        pendingTab = initialTab;
        config = source.Clone();
        config.Normalize();
        this.dayZSettings = dayZSettings;
        this.dayZSettings.Normalize();
        this.dayZStatus = dayZStatus;
        ocrInstallPollTimer.Tick += async (_, _) => await RefreshOcrAfterInstallAsync();
        this.battlePassSettings = battlePassSettings.Clone();
        this.battlePassSnapshot = battlePassSnapshot;

        Text = AppIdentity.DisplayName;
        Icon = AppIcons.MainIcon();
        ShowIcon = false;
        StartPosition = FormStartPosition.Manual;
        MinimumSize = new Size(1060, 700);
        Size = new Size(1240, 780);
        BackColor = Color.FromArgb(20, 24, 27);

        var screen = MonitorInfo.ResolveScreen(config.EditorMonitorDeviceName);
        var bounds = EditorWindowPlacement.Normalize(
            dayZSettings.EditorWindowBounds ?? config.EditorWindowBounds,
            screen.WorkingArea,
            Screen.AllScreens.Select(item => item.WorkingArea));
        Size = bounds.Size;
        Location = bounds.Location;

        webView.Dock = DockStyle.Fill;
        Controls.Add(webView);

        Shown += async (_, _) =>
        {
            await InitializeWebViewAsync();
            dayZStatusTimer.Start();
        };
        FormClosed += (_, _) =>
        {
            updateService.ProgressChanged -= OnUpdateProgressChanged;
            dayZStatusTimer.Stop();
            dayZStatusTimer.Dispose();
            ocrInstallPollTimer.Stop();
            ocrInstallPollTimer.Dispose();
            var savedBounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
            EditorBoundsChanged?.Invoke(EditorWindowBounds.FromRectangle(savedBounds));
            MonitorChanged?.Invoke(Screen.FromRectangle(savedBounds).DeviceName);
        };
        dayZStatusTimer.Tick += (_, _) =>
        {
            if (webReady && !IsDisposed) DayZStatusRequested?.Invoke();
        };
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            if (integratedCaption) parameters.Style &= ~CaptionStyle;
            return parameters;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        WindowAppearance.ApplyDarkCaption(Handle, BackColor);
    }

    protected override void WndProc(ref Message message)
    {
        var proposedWindow = integratedCaption ? WindowAppearance.GetProposedWindowBounds(message) : null;
        base.WndProc(ref message);
        if (!integratedCaption) return;
        WindowAppearance.SetMaximizedBounds(ref message);
        if (proposedWindow is { } bounds) WindowAppearance.AdjustCaptionClient(ref message, bounds);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        var maximized = WindowState == FormWindowState.Maximized;
        if (!integratedCaption || !webReady || maximized == captionMaximized) return;
        _ = SendCaptionStateAsync();
    }

    private async Task SendCaptionStateAsync()
    {
        if (!integratedCaption || IsDisposed || webView.CoreWebView2 is not { } core) return;
        captionMaximized = WindowState == FormWindowState.Maximized;
        await core.ExecuteScriptAsync($"window.DayZMapCompanion.setWindowMaximized({JsonSerializer.Serialize(captionMaximized)});");
    }

    public void OpenTab(string tab)
    {
        pendingTab = tab;
        if (tab == "updates")
        {
            _ = RefreshUpdateInfoAsync(false);
        }

        if (!webReady || webView.CoreWebView2 is null)
        {
            return;
        }

        var json = JsonSerializer.Serialize(tab);
        _ = webView.CoreWebView2.ExecuteScriptAsync($"window.DayZMapCompanion.openTab({json});");
    }

    public void ApplyExternalConfig(AppConfig source)
    {
        config = source.Clone();
        config.Normalize();
        _ = SendStateAsync();
    }

    public void ApplyDayZState(DayZCompanionSettings settings, DayZCompanionStatus status)
    {
        dayZSettings = settings;
        dayZSettings.Normalize();
        dayZStatus = status;
        _ = SendStateAsync();
    }

    public void ApplyBattlePassState(BattlePassSettings settings, BattlePassSnapshot snapshot)
    {
        battlePassSettings = settings.Clone();
        battlePassSnapshot = snapshot;
        _ = SendStateAsync();
    }

    public void ApplyPerformanceState(PerformanceSnapshot snapshot)
    {
        performanceSnapshot = snapshot;
        if (!webReady || IsDisposed || webView.CoreWebView2 is null) return;
        _ = SendPerformanceAsync(snapshot);
    }

    private async Task SendPerformanceAsync(PerformanceSnapshot snapshot)
    {
        try
        {
            if (webView.CoreWebView2 is { } core)
            {
                var blocked = GetSessionMarkerImportBlockReason(snapshot, dayZStatus.DayZRunning || snapshot.Running);
                await core.ExecuteScriptAsync($"window.DayZMapCompanion.receivePerformance({JsonSerializer.Serialize(snapshot, JsonOptions)}, {JsonSerializer.Serialize(blocked, JsonOptions)});");
            }
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            if (!IsDisposed) AppRuntimeLog.Error("Could not update performance display", ex);
        }
    }

    public void ApplyPerformancePlacement(PerformanceOverlayPlacement placement)
    {
        if (!webReady || IsDisposed || webView.CoreWebView2 is null) return;
        _ = webView.CoreWebView2.ExecuteScriptAsync($"window.DayZMapCompanion.receivePerformancePlacement({JsonSerializer.Serialize(placement, JsonOptions)});");
    }

    public async Task CaptureTreasureAsync()
    {
        if (treasureCaptureInProgress) return;
        treasureCaptureInProgress = true;
        await SendStateAsync();
        Hide();
        try
        {
            var capture = treasureCaptureService.CaptureImage();
            if (capture is null) return;
            var captures = treasureStore.Load();
            captures.Insert(0, capture);
            treasureStore.Save(captures);
            treasureOcrQueue.Enqueue(capture.Id);
            _ = ProcessTreasureQueueAsync();
        }
        finally
        {
            treasureCaptureInProgress = false;
            Show();
            Activate();
            await SendStateAsync();
        }
    }

    public void QueueTreasureRecognition(string captureId)
    {
        if (string.IsNullOrWhiteSpace(captureId) || treasureOcrQueue.Contains(captureId)) return;
        treasureOcrQueue.Enqueue(captureId);
        _ = ProcessTreasureQueueAsync();
        _ = SendStateAsync();
    }

    private async Task ProcessTreasureQueueAsync()
    {
        if (treasureOcrWorkerRunning) return;
        treasureOcrWorkerRunning = true;
        try
        {
            while (treasureOcrQueue.TryDequeue(out var id))
            {
                var captures = treasureStore.Load();
                var capture = captures.SingleOrDefault(item => item.Id == id);
                if (capture is null || !File.Exists(capture.ImagePath)) continue;
                capture.Status = TreasureRecognitionStatus.Recognizing;
                treasureStore.Save(captures);
                await SendStateAsync();
                try
                {
                    await treasureCaptureService.RecognizeCaptureAsync(capture);
                }
                catch (Exception ex)
                {
                    AppRuntimeLog.Error("Treasure OCR failed", ex);
                    capture.Status = TreasureRecognitionStatus.Ambiguous;
                    capture.RawText = "OCR error: " + ex.Message;
                }
                treasureStore.Save(captures);
                await SendStateAsync();
            }
        }
        finally
        {
            treasureOcrWorkerRunning = false;
        }
    }

    private async Task InitializeWebViewAsync()
    {
        try
        {
            var browserData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppIdentity.DataDirectoryName, "WebView2");
            Directory.CreateDirectory(browserData);
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: browserData);
            await webView.EnsureCoreWebView2Async(environment);
            webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            webView.CoreWebView2.Settings.AreDevToolsEnabled = true;
            webView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
            var html = GetEditorHtml();
            try
            {
                webView.CoreWebView2.Settings.IsNonClientRegionSupportEnabled = true;
                html = html.Replace("<body>", "<body class=\"integrated-frame\">", StringComparison.Ordinal);
                integratedCaption = true;
                UpdateStyles();
            }
            catch (NotImplementedException)
            {
                // Older WebView2 runtimes keep the system caption.
            }
            webView.CoreWebView2.NavigateToString(html);
        }
        catch (Exception ex)
        {
            AppRuntimeLog.Error("WebView2 editor failed to start", ex);
            MessageBox.Show(
                $"WebView2 editor failed to start.\n\n{ex.Message}",
                AppIdentity.DisplayName,
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private async void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (e.Source != "about:blank") return;
        try
        {
            using var document = JsonDocument.Parse(e.WebMessageAsJson);
            var root = document.RootElement;
            var type = root.GetProperty("type").GetString();

            switch (type)
            {
                case "ready":
                    webReady = true;
                    var shouldRefreshUpdates = pendingTab == "updates";
                    await SendCaptionStateAsync();
                    await SendStateAsync();
                    if (shouldRefreshUpdates)
                    {
                        await RefreshUpdateInfoAsync(false);
                    }
                    break;
                case "updateConfig":
                    ApplyConfigFromWeb(root.GetProperty("config"));
                    break;
                case "previewConfig":
                    await ApplyPreviewConfigFromWebAsync(root.GetProperty("config"));
                    break;
                case "previewSize":
                    var nextSize = new Size(
                        Math.Clamp(root.GetProperty("width").GetInt32(), 1, 8192),
                        Math.Clamp(root.GetProperty("height").GetInt32(), 1, 8192));
                    if (nextSize != previewSize)
                    {
                        previewSize = nextSize;
                        var previewJson = JsonSerializer.Serialize(RenderPreviewDataUri(config.CurrentProfile), JsonOptions);
                        await webView.CoreWebView2.ExecuteScriptAsync($"window.DayZMapCompanion.receivePreview({previewJson});");
                    }
                    break;
                case "updateDayZSettings":
                    ApplyDayZSettingsFromWeb(root.GetProperty("settings"));
                    break;
                case "updateBattlePassSettings":
                    ApplyBattlePassSettingsFromWeb(root.GetProperty("settings"));
                    break;
                case "command":
                    await HandleCommandAsync(root.GetProperty("name").GetString());
                    break;
                case "sessionMap":
                    await PlaceSessionMapAsync(root);
                    break;
                case "copySessionMarker":
                    await CopySessionMarkerAsync(root);
                    break;
                case "manualTask":
                    var manualTask = root.GetProperty("task").Deserialize<ManualTask>(JsonOptions);
                    var action = root.GetProperty("action").GetString();
                    if (manualTask is null || action is not ("save" or "delete")) break;
                    if (action == "save" && (string.IsNullOrWhiteSpace(manualTask.Title) || manualTask.Current < 0 || manualTask.Target <= 0))
                        throw new InvalidOperationException("Укажите название и корректный прогресс (текущее значение и цель).");
                    ManualTaskCommandRequested?.Invoke(action, manualTask);
                    break;
                case "updatePlayerPositionSettings":
                    await ApplyPlayerPositionSettingsFromWebAsync(root.GetProperty("settings"));
                    break;
                case "treasure":
                    HandleTreasureFromWeb(root);
                    await SendStateAsync();
                    break;
                case "window":
                    HandleWindowCommand(root.GetProperty("name").GetString());
                    break;
            }
        }
        catch (Exception ex)
        {
            await SendErrorAsync(ex.Message);
        }
    }

    private static string? SessionMapAddress(string? id) => id switch
    {
        "cherno" or "chernarus" or "chernarusplus" => "https://dayz-map.ru/chernarus/",
        "deerisle" => "https://dayz-map.ru/deer-isle/",
        "deadfall" => "https://dayz-map.ru/deadfall/",
        "nasdara" => "https://dayz-map.ru/nasdara/",
        _ => null
    };

    private async Task PlaceSessionMapAsync(JsonElement placement)
    {
        var map = GetString(placement, "map");
        var address = SessionMapAddress(map);
        var bounds = Rectangle.Intersect(webView.ClientRectangle, new Rectangle(
            placement.GetProperty("left").GetInt32(), placement.GetProperty("top").GetInt32(),
            placement.GetProperty("width").GetInt32(), placement.GetProperty("height").GetInt32()));
        if (address is null || bounds.Width < 80 || bounds.Height < 80)
        {
            requestedSessionMap = null;
            if (sessionMapView is not null) sessionMapView.Visible = false;
            return;
        }

        requestedSessionMap = address;
        var view = sessionMapView ??= CreateSessionMapView();
        view.Bounds = bounds;
        try
        {
            await (sessionMapInitialization ??= InitializeSessionMapAsync(view));
        }
        catch (Exception ex)
        {
            if (IsDisposed || !ReferenceEquals(sessionMapView, view)) return;
            view.Dispose();
            sessionMapView = null;
            sessionMapInitialization = null;
            AppRuntimeLog.Error("Embedded DayZ-Map failed to start", ex);
            if (requestedSessionMap == address) await ReportSessionMapFailureAsync();
            return;
        }
        if (IsDisposed || view.IsDisposed || requestedSessionMap != address) return;
        if (sessionMapAddress != address)
        {
            sessionMapAddress = address;
            view.Visible = false;
            view.CoreWebView2.Navigate(address);
        }
        else if (sessionMapLoaded)
        {
            view.Visible = true;
        }
    }

    private WebView2 CreateSessionMapView()
    {
        var view = new WebView2 { Visible = false, DefaultBackgroundColor = BackColor, TabStop = true };
        Controls.Add(view);
        _ = view.Handle;
        view.BringToFront();
        return view;
    }

    private async Task InitializeSessionMapAsync(WebView2 view)
    {
        // The website allows framing only from its own origin. Keep it in a
        // sibling WebView with no access to the local editor's native commands.
        await view.EnsureCoreWebView2Async(webView.CoreWebView2.Environment);
        var core = view.CoreWebView2;
        core.Settings.IsWebMessageEnabled = false;
        core.Settings.AreHostObjectsAllowed = false;
        using var script = typeof(EditorForm).Assembly.GetManifestResourceStream("DayZMapCompanion.SessionMapView")
            ?? throw new InvalidOperationException("Missing session map presentation.");
        using var reader = new StreamReader(script);
        await core.AddScriptToExecuteOnDocumentCreatedAsync(await reader.ReadToEndAsync());
        ulong currentNavigation = 0;
        core.NavigationStarting += (_, e) =>
        {
            e.Cancel = !Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri)
                || uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort || uri.Host != "dayz-map.ru";
            if (!e.Cancel)
            {
                currentNavigation = e.NavigationId;
                sessionMapLoaded = false;
            }
        };
        core.ContentLoading += (_, e) =>
        {
            if (IsDisposed || view.IsDisposed || currentNavigation != e.NavigationId || e.IsErrorPage) return;
            sessionMapLoaded = true;
            view.Visible = requestedSessionMap is not null && requestedSessionMap == sessionMapAddress;
        };
        core.NavigationCompleted += async (_, e) =>
        {
            if (IsDisposed || view.IsDisposed || currentNavigation != e.NavigationId) return;
            sessionMapLoaded = e.IsSuccess;
            view.Visible = e.IsSuccess && requestedSessionMap is not null && requestedSessionMap == sessionMapAddress;
            if (!e.IsSuccess && requestedSessionMap is not null && requestedSessionMap == sessionMapAddress)
            {
                sessionMapAddress = null;
                await ReportSessionMapFailureAsync();
            }
        };
    }

    private async Task ReportSessionMapFailureAsync()
    {
        if (!IsDisposed && webView.CoreWebView2 is { } core)
            await core.ExecuteScriptAsync("window.DayZMapCompanion.sessionMapFailed();");
    }

    private async Task CopySessionMarkerAsync(JsonElement request)
    {
        var marker = request.GetProperty("marker").Deserialize<DayZSessionMarker>(JsonOptions);
        if (marker is null) return;
        var text = GetString(request, "action") switch
        {
            "coordinates" => FormattableString.Invariant($"{Math.Round(marker.X)} {Math.Round(marker.Z)}"),
            "name" => marker.Name,
            _ => null
        };
        if (string.IsNullOrEmpty(text)) return;
        try
        {
            Clipboard.SetText(text);
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            await SendErrorAsync("Не удалось скопировать данные метки в буфер обмена.");
        }
    }

    private void ApplyConfigFromWeb(JsonElement configElement)
    {
        var next = configElement.Deserialize<AppConfig>(JsonOptions);
        if (next is null)
        {
            return;
        }

        config = next;
        config.Normalize();
        EmitChanged();
    }

    private async Task ApplyPreviewConfigFromWebAsync(JsonElement configElement)
    {
        var next = configElement.Deserialize<AppConfig>(JsonOptions);
        if (next is null)
        {
            return;
        }

        config = next;
        config.Normalize();
        await SendStateAsync();
    }

    private async Task HandleCommandAsync(string? name)
    {
        switch (name)
        {
            case "importImage":
                ImportImage();
                break;
            case "refreshMonitors":
                await SendStateAsync();
                break;
            case "checkUpdate":
                if (AppDistribution.IsMicrosoftStore) { AppDistribution.OpenStore(); break; }
                await RefreshUpdateInfoAsync(true);
                break;
            case "downloadUpdate":
                if (updateInfo is null)
                {
                    updateInfo = await updateService.GetLatestAsync();
                }
                UpdateService.OpenDownload(updateInfo);
                break;
            case "installUpdate":
                UpdateRequested?.Invoke();
                break;
            case "cancelUpdate":
                UpdateCancelRequested?.Invoke();
                break;
            case "openCompanionReleases":
                UpdateService.OpenReleasesPage();
                break;
            case "exitApplication":
                // Return from the WebView callback before closing its owning form.
                // Closing it synchronously can leave the callback and its COM message pump
                // waiting on each other, making the application appear stuck.
                BeginInvoke(new Action(() => ExitRequested?.Invoke()));
                break;
            case "selectDayZMarkersFile":
                SelectDayZMarkersFile();
                break;
            case "clearDayZMarkersFile":
                dayZSettings.PrivateMarkersPath = null;
                DayZSettingsChanged?.Invoke(dayZSettings);
                break;
            case "refreshDayZStatus":
                DayZStatusRequested?.Invoke();
                break;
            case "selectPerformanceFont":
                SelectPerformanceFont();
                break;
            case "fitPerformanceOverlay":
                PerformanceOverlayRenderer.Fit(dayZSettings.Performance);
                DayZSettingsChanged?.Invoke(dayZSettings);
                _ = SendStateAsync();
                break;
            case "openDayZMarkersFolder":
                OpenDayZMarkersFolder();
                break;
            case "exportSessionMarkers":
                ExportSessionMarkers();
                break;
            case "importSessionMarkers":
                ImportSessionMarkers();
                DayZStatusRequested?.Invoke();
                break;
            case "openSessionMap":
                Process.Start(new ProcessStartInfo(SessionMapAddress(performanceSnapshot.MapName.ToLowerInvariant()) ?? "https://dayz-map.ru/") { UseShellExecute = true });
                break;
            case "openRuntimeLog":
                OpenFileLocation(AppRuntimeLog.FilePath);
                break;
            case "scanBattlePass":
            case "scanBattlePassDaily":
            case "scanBattlePassWeeklyPage1":
            case "scanBattlePassWeeklyPage2":
            case "scanBattlePassSeasonal":
            case "editBattlePassTasks":
            case "clearBattlePass":
            case "showBattlePassDebug":
            case "previewBattlePassZones":
            case "calibrateBattlePassZones":
            case "resetBattlePassOverlayBounds":
                if (name is not null) BattlePassCommandRequested?.Invoke(name);
                break;
            case "installOcr":
                TesseractOcr.Install();
                ocrInstallPollAttempts = 0;
                ocrInstallPollTimer.Start();
                ocrStatus = TesseractOcr.Detect();
                await SendStateAsync();
                break;
            case "refreshOcr":
                ocrStatus = TesseractOcr.Detect();
                await SendStateAsync();
                break;
            case "captureTreasure":
                await CaptureTreasureAsync();
                break;
            case "selectPlayerPositionRegion":
                if (PlayerPositionRegionRequested is { } selectRegion) await selectRegion();
                break;
            case "showPlayerPositionHint":
                PlayerPositionSelectionHintForm.ShowHint(this);
                break;
            case "testPlayerPosition":
                await TestPlayerPositionAsync();
                break;
            case "openPlayerPositionDiagnostics":
                OpenFileLocation(playerPositionCaptureService.DiagnosticsDirectory);
                break;
            case "openTreasureCapturesFolder":
                OpenFileLocation(treasureStore.ImagesDirectory);
                break;
        }
    }

    private void ApplyDayZSettingsFromWeb(JsonElement settingsElement)
    {
        var next = settingsElement.Deserialize<DayZCompanionSettings>(JsonOptions);
        if (next is null) return;
        next.Normalize();
        var previous = dayZSettings.Performance;
        var current = next.Performance;
        if ((!previous.ShowFrameTime && current.ShowFrameTime) || (!previous.ShowOnePercentLow && current.ShowOnePercentLow)
            || (!previous.ShowJitter && current.ShowJitter) || (!previous.ShowFrameGraph && current.ShowFrameGraph)
            || (!previous.VerticalLayout && current.VerticalLayout)) PerformanceOverlayRenderer.Fit(current, onlyGrow: true);
        dayZSettings = next;
        DayZSettingsChanged?.Invoke(next);
    }

    private string? GetSessionMarkerImportBlockReason(PerformanceSnapshot snapshot, bool gameRunning) =>
        DayZMarkersService.GetOfflineImportBlockReason(gameRunning, dayZSettings.BlockWritesWhenDayZRunning, snapshot, DateTimeOffset.UtcNow);

    private void EnsureSessionMarkerImportAllowed()
    {
        var reason = GetSessionMarkerImportBlockReason(performanceSnapshot, DayZMarkersService.IsDayZRunning());
        if (reason is not null) throw new DayZCompanionException(reason);
    }

    private void ExportSessionMarkers()
    {
        var server = DayZMarkersService.IsDayZRunning() ? performanceSnapshot.Server : null;
        var json = new DayZMarkersService(dayZSettings).BuildExport(server);
        using var dialog = new SaveFileDialog { Title = "Выгрузить метки из игры", Filter = "Метки DayZ (*.json)|*.json", FileName = "DayZ-Markers.json" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        if (string.Equals(Path.GetFileName(dialog.FileName), "PrivateMarkers.json", StringComparison.OrdinalIgnoreCase))
            throw new DayZCompanionException("Выберите другое имя для файла экспорта.");
        File.WriteAllText(dialog.FileName, json);
    }

    private void ImportSessionMarkers()
    {
        EnsureSessionMarkerImportAllowed();
        using var dialog = new OpenFileDialog { Title = "Загрузить метки в игру", Filter = "Метки DayZ (*.json)|*.json" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        var requests = DayZMarkersService.ReadImportRequests(dialog.FileName);
        EnsureSessionMarkerImportAllowed();
        var targets = string.Join(", ", requests.Take(4).Select(request => request.Server));
        if (requests.Count > 4) targets += $" и ещё {requests.Count - 4}";
        if (MessageBox.Show(this, $"Загрузить метки из файла?\n\nСерверы: {targets}\nМеток: {requests.Sum(request => request.Markers.Count)}\n\nСуществующие метки с тем же UID будут обновлены.",
            "Загрузить метки в игру", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        new DayZMarkersService(dayZSettings).ImportBatch(requests, EnsureSessionMarkerImportAllowed);
    }

    private async Task ApplyPlayerPositionSettingsFromWebAsync(JsonElement settingsElement)
    {
        var next = settingsElement.Deserialize<PlayerPositionTrackingSettings>(JsonOptions);
        if (next is null) return;
        // The web form edits display options; only the native selector can change the OCR region.
        next.CopyRegionFrom(dayZSettings.PlayerPositionTracking);
        next.Normalize();
        if (next.Enabled && !next.Paused) await WindowGraphicsCapture.GetBorderlessAccessAsync();
        dayZSettings.PlayerPositionTracking = next;
        DayZSettingsChanged?.Invoke(dayZSettings);
    }

    public async Task SelectPlayerPositionRegionAsync()
    {
        if (selectingPlayerPositionRegion) return;
        selectingPlayerPositionRegion = true;
        try
        {
            using var frame = await playerPositionCaptureService.CaptureSelectionImageAsync();
            if (IsDisposed) return;
            var display = Screen.FromRectangle(frame.ClientBounds).WorkingArea;
            var region = ScreenRegionSelector.SelectImageRegion(frame.Image, display);
            if (!region.HasValue) return;
            dayZSettings.PlayerPositionTracking.SetScreenRectangle(region.Value, new Rectangle(Point.Empty, frame.Image.Size));
            dayZSettings.PlayerPositionTracking.RegionRelativeToWindow = true;
            DayZSettingsChanged?.Invoke(dayZSettings);
        }
        finally { selectingPlayerPositionRegion = false; }
    }

    public async Task TestPlayerPositionAsync()
    {
        var settings = dayZSettings.PlayerPositionTracking;
        var session = playerPositionMapBridge.GetSession();
        var map = session?.Maps.SingleOrDefault(item => item.Id == settings.MapId);
        var result = await playerPositionCaptureService.TestAsync(settings, map);
        settings.LastError = result.Position is null ? result.Message : "Тест: " + result.Message;
        if (result.Position is not null) { settings.LastX = result.Position.X; settings.LastY = result.Position.Y; settings.LastZ = result.Position.Z; }
        DayZSettingsChanged?.Invoke(dayZSettings);
    }

    private void HandleTreasureFromWeb(JsonElement root)
    {
        var id = GetString(root, "id");
        var captures = treasureStore.Load();
        var capture = captures.SingleOrDefault(item => item.Id == id);
        switch (GetString(root, "action"))
        {
            case "delete":
                if (capture is not null) captures.Remove(capture);
                break;
            case "retry":
                if (capture is not null)
                {
                    capture.Status = TreasureRecognitionStatus.Queued;
                    treasureOcrQueue.Enqueue(capture.Id);
                    _ = ProcessTreasureQueueAsync();
                }
                break;
            case "clear":
                captures.Clear();
                break;
            case "clearErrors":
                foreach (var item in captures.Where(item => item.Status == TreasureRecognitionStatus.Ambiguous).ToList())
                {
                    captures.Remove(item);
                }
                break;
            case "edit":
                if (capture is not null && root.TryGetProperty("x", out var x) && root.TryGetProperty("z", out var z) && x.TryGetInt32(out var xValue) && z.TryGetInt32(out var zValue))
                {
                    capture.X = xValue; capture.Z = zValue; capture.ManuallyEdited = true; capture.Status = TreasureRecognitionStatus.Recognized; capture.SentAt = null; capture.DeliveryResult = "";
                }
                break;
            case "send":
                treasureMapBridge.Queue(
                    GetString(root, "mapId"),
                    GetString(root, "profileId"),
                    GetString(root, "markerName"),
                    GetString(root, "markerType"),
                    GetString(root, "markerColor"),
                    captures);
                treasureFeedback = "Пакет подтверждённых координат передан карте. Ожидаю импорт во вкладке браузера…";
                break;
        }
        treasureStore.Save(captures);
    }

    private static string GetString(JsonElement root, string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    private object[] PrepareTreasureCapturesForView()
    {
        var captures = treasureStore.Load();
        var outcomes = treasureMapBridge.GetDeliveryOutcomes();
        var changed = false;
        foreach (var capture in captures.Where(item => outcomes.ContainsKey(item.Id)))
        {
            var outcome = outcomes[capture.Id];
            capture.DeliveryResult = outcome.Message;
            if (!string.Equals(outcome.Result, "error", StringComparison.OrdinalIgnoreCase)) capture.SentAt = DateTimeOffset.Now;
            changed = true;
        }
        if (changed) treasureStore.Save(captures);
        return captures.Select(capture => (object)new
        {
            capture.Id, capture.CapturedAt, capture.RawText, capture.X, capture.Z, capture.Status,
            capture.ManuallyEdited, capture.SentAt, capture.DeliveryResult,
            preview = TreasurePreviewDataUri(capture.ImagePath)
        }).ToArray();
    }

    private void ApplyBattlePassSettingsFromWeb(JsonElement settingsElement)
    {
        var next = settingsElement.Deserialize<BattlePassSettings>(JsonOptions);
        if (next is null) return;
        next.Normalize();
        battlePassSettings = next;
        BattlePassSettingsChanged?.Invoke(next);
    }

    private void SelectDayZMarkersFile()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Выберите PrivateMarkers.json",
            Filter = "PrivateMarkers.json|PrivateMarkers.json",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        if (!string.Equals(Path.GetFileName(dialog.FileName), "PrivateMarkers.json", StringComparison.OrdinalIgnoreCase))
        {
            _ = SendErrorAsync("Можно выбрать только файл с именем PrivateMarkers.json.");
            return;
        }
        dayZSettings.PrivateMarkersPath = dialog.FileName;
        DayZSettingsChanged?.Invoke(dayZSettings);
    }

    private void SelectPerformanceFont()
    {
        var settings = dayZSettings.Performance;
        var style = (settings.Bold ? FontStyle.Bold : FontStyle.Regular) | (settings.Italic ? FontStyle.Italic : FontStyle.Regular);
        using var font = new Font(settings.FontFamily, settings.FontSize, style);
        using var dialog = new FontDialog { Font = font, FontMustExist = true, MinSize = 8, MaxSize = 72, ShowEffects = false };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        settings.FontFamily = dialog.Font.Name;
        settings.FontSize = (int)Math.Round(dialog.Font.SizeInPoints);
        settings.Bold = dialog.Font.Bold;
        settings.Italic = dialog.Font.Italic;
        settings.Normalize();
        DayZSettingsChanged?.Invoke(dayZSettings);
        _ = SendStateAsync();
    }

    private void OpenDayZMarkersFolder()
    {
        var path = dayZStatus.PrivateMarkersPath ?? dayZSettings.PrivateMarkersPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            _ = SendErrorAsync("PrivateMarkers.json пока не найден.");
            return;
        }
        OpenFileLocation(path);
    }

    private static void OpenFileLocation(string path)
    {
        var directory = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(directory) { UseShellExecute = true });
        }
    }

    private void HandleWindowCommand(string? name)
    {
        switch (name)
        {
            case "resize-top" when integratedCaption && WindowState == FormWindowState.Normal:
                if ((MouseButtons & MouseButtons.Left) != 0) WindowAppearance.BeginTopResize(Handle, Cursor.Position);
                break;
            case "minimize":
                WindowState = FormWindowState.Minimized;
                break;
            case "maximize":
                WindowState = WindowState == FormWindowState.Maximized
                    ? FormWindowState.Normal
                    : FormWindowState.Maximized;
                break;
            case "close":
                BeginInvoke(new Action(Close));
                break;
        }
    }

    private void ImportImage()
    {
        var profile = config.Profiles.FirstOrDefault(profile => profile.Id == config.ActiveProfileId);
        if (profile is null)
        {
            return;
        }

        using var dialog = new OpenFileDialog
        {
            Title = "Import crosshair image",
            Filter = "Images|*.png;*.jpg;*.jpeg",
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        try
        {
            profile.ImageLayer.Path = assetStore.ImportImage(dialog.FileName);
            profile.ImageLayer.Enabled = true;
            SetAnchorToImageCenter(profile.ImageLayer);
            EmitChanged();
        }
        catch (Exception ex)
        {
            _ = SendErrorAsync(ex.Message);
        }
    }

    private void EmitChanged()
    {
        config.Normalize();
        ConfigChanged?.Invoke(config.Clone());
        _ = SendStateAsync();
    }

    private async Task SendStateAsync()
    {
        if (!webReady || webView.CoreWebView2 is null)
        {
            return;
        }

        var profile = config.CurrentProfile;
        var openTab = pendingTab;
        pendingTab = null;
        var payload = new
        {
            microsoftStore = AppDistribution.IsMicrosoftStore,
            config,
            openTab,
            update = updateInfo,
            updateProgress = updateService.Progress,
            dayZ = new { settings = dayZSettings, status = dayZStatus },
            performance = performanceSnapshot,
            markerImportBlockReason = GetSessionMarkerImportBlockReason(performanceSnapshot, dayZStatus.DayZRunning || performanceSnapshot.Running),
            performanceDefaults = new PerformanceSettings(),
            ocr = ocrStatus ??= TesseractOcr.Detect(),
            battlePass = new { settings = battlePassSettings, snapshot = battlePassSnapshot, manualTasks = battlePassStore.LoadManualTasks(), ocr = ocrStatus },
            treasures = new { captures = PrepareTreasureCapturesForView(), destination = treasureMapBridge.GetSession(), selecting = treasureCaptureInProgress, processing = treasureOcrWorkerRunning, queued = treasureOcrQueue.Count, feedback = treasureMapBridge.GetDeliveryFeedback() ?? treasureFeedback },
            playerPosition = new { settings = dayZSettings.PlayerPositionTracking, destination = playerPositionMapBridge.GetSession(), feedback = playerPositionMapBridge.GetFeedback(), diagnostics = playerPositionCaptureService.GetDiagnostics() },
            hotkeyErrors = config.HotkeyRegistrationErrors,
            monitors = MonitorInfo.GetAll().Select(monitor => new
            {
                monitor.DeviceName,
                monitor.DisplayName,
                monitor.Primary
            }),
            preview = RenderPreviewDataUri(profile)
        };
        var json = JsonSerializer.Serialize(payload, JsonOptions);
            await webView.CoreWebView2.ExecuteScriptAsync($"window.DayZMapCompanion.receiveState({json});");
    }

    private async Task RefreshOcrAfterInstallAsync()
    {
        ocrInstallPollAttempts++;
        ocrStatus = TesseractOcr.Detect();
        if (ocrStatus.Ready || ocrInstallPollAttempts >= 24)
        {
            ocrInstallPollTimer.Stop();
        }
        await SendStateAsync();
    }

    private async Task RefreshUpdateInfoAsync(bool forceRefresh)
    {
        if (AppDistribution.IsMicrosoftStore) { await SendStateAsync(); return; }
        updateInfo = await updateService.GetLatestAsync(forceRefresh);
        await SendStateAsync();
    }

    private void OnUpdateProgressChanged()
    {
        if (!IsDisposed && webReady) _ = SendStateAsync();
    }

    private async Task SendErrorAsync(string message)
    {
        if (!webReady || webView.CoreWebView2 is null)
        {
            return;
        }

        var json = JsonSerializer.Serialize(message);
        await webView.CoreWebView2.ExecuteScriptAsync($"window.DayZMapCompanion.showError({json});");
    }

    private string RenderPreviewDataUri(CrosshairProfile profile)
    {
        using var bitmap = new Bitmap(previewSize.Width, previewSize.Height, PixelFormat.Format32bppPArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.FromArgb(14, 14, 16));
            using var gridPen = new Pen(Color.FromArgb(50, 72, 72, 78), 1);
            for (var x = 0; x < bitmap.Width; x += 32)
            {
                graphics.DrawLine(gridPen, x, 0, x, bitmap.Height);
            }
            for (var y = 0; y < bitmap.Height; y += 32)
            {
                graphics.DrawLine(gridPen, 0, y, bitmap.Width, y);
            }
            using var centerPen = new Pen(Color.FromArgb(95, 238, 177, 91), 1);
            graphics.DrawLine(centerPen, bitmap.Width / 2, 0, bitmap.Width / 2, bitmap.Height);
            graphics.DrawLine(centerPen, 0, bitmap.Height / 2, bitmap.Width, bitmap.Height / 2);
        }

        using (var rendered = CrosshairRenderer.RenderBitmap(bitmap.Size, profile))
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.DrawImageUnscaled(rendered, 0, 0);
        }

        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        return "data:image/png;base64," + Convert.ToBase64String(stream.ToArray());
    }

    private static string? TreasurePreviewDataUri(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        try
        {
            using var source = Image.FromFile(path);
            var width = Math.Min(420, source.Width);
            var height = Math.Max(1, source.Height * width / source.Width);
            using var thumbnail = new Bitmap(width, height);
            using (var graphics = Graphics.FromImage(thumbnail)) graphics.DrawImage(source, new Rectangle(0, 0, width, height));
            using var stream = new MemoryStream();
            thumbnail.Save(stream, ImageFormat.Png);
            return "data:image/png;base64," + Convert.ToBase64String(stream.ToArray());
        }
        catch (Exception ex)
        {
            AppRuntimeLog.Error("Could not create treasure preview", ex);
            return null;
        }
    }

    private static void SetAnchorToImageCenter(ImageLayer layer)
    {
        if (!TryGetImageSize(layer.Path, out var size))
        {
            layer.AnchorX = null;
            layer.AnchorY = null;
            return;
        }

        layer.AnchorX = size.Width / 2;
        layer.AnchorY = size.Height / 2;
    }

    private static bool TryGetImageSize(string? path, out Size size)
    {
        size = Size.Empty;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return false;
        }

        try
        {
            using var stream = File.OpenRead(path);
            using var image = Image.FromStream(stream);
            size = image.Size;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string GetEditorHtml()
    {
        var spritePath = Path.Combine(AppContext.BaseDirectory, "assets", "marker-icons.svg");
        var sprite = File.Exists(spritePath) ? File.ReadAllText(spritePath) : "";
        return EditorHtml.Replace("<!-- marker-icons -->", sprite, StringComparison.Ordinal);
    }

    private const string EditorHtml = """
<!doctype html>
<html lang="ru">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<style>
:root {
  color-scheme: dark;
  accent-color: #00e2ed;
  --bg: #14181b;
  --chrome: #191d21;
  --surface: #1c2125;
  --raised: #283037;
  --line: #3c474e;
  --soft: #30393f;
  --text: #f2f5f6;
  --muted: #b7c1c6;
  --faint: #a0adb4;
  --accent: #00e2ed;
  --accent-bg: #11363e;
  --accent-line: #28636b;
  --accent-ink: #052023;
  --accent-2: #77d6a4;
  --danger: #d25d69;
}
* { box-sizing: border-box; }
body {
  margin: 0;
  --caption-space: 0px;
  background: var(--bg);
  color: var(--text);
  font: 14px/1.45 "Segoe UI Variable Text", "Segoe UI", system-ui, sans-serif;
  overflow: hidden;
}
body.integrated-frame { --caption-space: 32px; }
body:not(.integrated-frame) .window-caption { display: none; }
.window-caption {
  position: fixed;
  z-index: 40;
  inset: 0 0 auto;
  height: var(--caption-space);
  display: flex;
  justify-content: flex-end;
  -webkit-app-region: drag;
  app-region: drag;
}
.window-caption button {
  display: grid;
  place-items: center;
  width: clamp(46px, 4.15vw, 66px);
  border: 0;
  background: transparent;
  color: var(--text);
  -webkit-app-region: no-drag;
  app-region: no-drag;
}
.window-caption button:hover { background: #ffffff16; }
.window-caption [data-window-command="close"]:hover { background: #c42b1c; }
.window-caption svg { width: 16px; height: 16px; }
.window-top-resize {
  position: absolute;
  z-index: 1;
  inset: 0 6px auto;
  height: 5px;
  cursor: ns-resize;
  -webkit-app-region: no-drag;
  app-region: no-drag;
}
body.window-maximized .window-top-resize { display: none; }
.window-caption [data-window-command="maximize"][aria-pressed="true"] .maximize-glyph,
.window-caption [data-window-command="maximize"][aria-pressed="false"] .restore-glyph { display: none; }
button, input, select { font: inherit; }
button:focus-visible, select:focus-visible, input:focus-visible {
  outline: 2px solid var(--accent);
  outline-offset: -2px;
}
.layout {
  height: 100vh;
  display: grid;
  grid-template-columns: clamp(154px, 15vw, 226px) minmax(0, 1fr);
  grid-template-rows: minmax(0, 1fr);
  min-height: 0;
  min-width: 0;
}
.layout.with-crosshair-preview {
  grid-template-columns: clamp(154px, 15vw, 226px) minmax(0, 820px) minmax(260px, 460px);
}
.layout:not(.with-crosshair-preview) .preview-wrap { display: none; }
.panel {
  min-height: 0;
  min-width: 0;
  background: var(--surface);
  border: 1px solid var(--soft);
  border-radius: 8px;
}
.sidebar {
  display: grid;
  grid-template-rows: auto minmax(0, 1fr);
  background: linear-gradient(150deg, #1b1e21, var(--chrome) 65%);
  border-right: 1px solid var(--soft);
  gap: clamp(16px, 2vw, 30px);
}
.brand {
  margin: 0 clamp(12px, 1.25vw, 20px);
  padding: calc(var(--caption-space) + clamp(16px, 2vw, 30px)) 0 clamp(15px, 1.8vw, 27px) 4px;
  border-bottom: 1px solid var(--line);
  display: flex;
  align-items: center;
  gap: 10px;
  font-size: clamp(15px, 1.55vw, 23px);
  line-height: 1.12;
  font-family: "Segoe UI Variable Display", "Segoe UI", sans-serif;
}
.brand-icon { width: 29px; height: 35px; flex: none; color: var(--accent); }
.icon { width: 21px; height: 21px; flex: none; vertical-align: middle; }
.profile-select, .field select, .field input[type="text"], .number-input {
  width: 100%;
  height: 38px;
  color: var(--text);
  background: #171c20;
  border: 1px solid var(--line);
  border-radius: 8px;
  padding: 0 10px;
  outline: none;
}
.nav {
  display: grid;
  align-content: start;
  gap: 2px;
  overflow-x: hidden;
  overflow-y: auto;
}
.nav button, .action {
  min-height: 38px;
  border: 1px solid transparent;
  border-radius: 7px;
  background: transparent;
  color: var(--text);
  text-align: left;
  padding: 0 12px;
  cursor: pointer;
}
.nav button {
  display: flex;
  align-items: center;
  gap: clamp(9px, 1.4vw, 21px);
  min-height: clamp(40px, 3.65vw, 58px);
  font-size: clamp(15px, 1.4vw, 21px);
  border: 0;
  border-left: 3px solid transparent;
  border-radius: 0;
  padding: 0 clamp(12px, 1.55vw, 24px);
}
.nav .icon { width: clamp(19px, 1.75vw, 26px); height: clamp(19px, 1.75vw, 26px); }
.nav button.active, .nav button[aria-expanded="true"].current {
  border-left-color: var(--accent);
  background: var(--accent-bg);
}
.nav button.active .icon, .nav button.current .icon { color: var(--accent); }
.nav button:hover, .action:hover { background: var(--raised); }
.nav-group, .nav-children { display: grid; }
.nav-main, .nav-group-toggle { font-weight: 500; }
.nav-group-toggle { position: relative; }
.nav-group-toggle span { position: absolute; right: 14px; color: var(--muted); }
.nav-group-toggle[aria-expanded="false"]:not(:hover):not(:focus-visible) span { visibility: hidden; }
.nav-children button {
  min-height: 34px;
  padding-left: 38px;
  color: var(--muted);
  font-size: 13px;
}
.limit {
  color: var(--faint);
  font-size: 12px;
  min-width: 0;
  overflow-wrap: anywhere;
  word-break: break-word;
}
.hotkey-warning {
  color: #ffb454;
  margin-top: 6px;
}
.editor {
  container-type: inline-size;
  margin-top: var(--caption-space);
  padding: clamp(12px, 1.5vw, 23px);
  overflow: auto;
  background: transparent;
  border: 0;
  border-radius: 0;
}
.section {
  display: none;
  gap: 10px;
}
.section.active {
  display: grid;
}
.section h2 {
  margin: 0 0 4px;
  font-size: 18px;
  font-weight: 650;
}
.control-group {
  display: grid;
  gap: 10px;
  min-width: 0;
}
.control-group + .control-group {
  margin-top: 4px;
}
.treasure-preview {
  display: block;
  width: 100%;
  max-height: 280px;
  object-fit: contain;
}
.treasure-preview-button {
  display: block;
  width: 100%;
  padding: 0;
  border: 1px solid var(--line);
  border-radius: 6px;
  overflow: hidden;
  background: #090a0c;
  cursor: zoom-in;
}
.treasure-image-modal { position: fixed; inset: 0; z-index: 100; display: grid; place-items: center; padding: 24px; background: rgba(0,0,0,.76); cursor: zoom-out; }
.treasure-image-modal img { max-width: 96vw; max-height: 92vh; object-fit: contain; border: 1px solid var(--line); border-radius: 8px; background: #090a0c; }
.control-group.separated {
  margin-top: 20px;
  padding-top: 20px;
  border-top: 1px solid var(--line);
}
.group-title {
  color: var(--accent);
  font-size: 12px;
  font-weight: 650;
  letter-spacing: .02em;
  text-transform: uppercase;
}
.field {
  min-width: 0;
  background: #232a2f;
  border: 1px solid var(--soft);
  border-radius: 8px;
  padding: 10px;
  display: grid;
  gap: 8px;
}
.field-row {
  display: grid;
  grid-template-columns: 1fr auto;
  gap: 10px;
  align-items: center;
  min-width: 0;
}
label, .caption {
  color: var(--muted);
  font-size: 12px;
}
.value {
  min-width: 42px;
  text-align: right;
  color: var(--text);
  font-variant-numeric: tabular-nums;
}
input[type="range"] {
  width: 100%;
  accent-color: var(--accent);
}
.slider-row {
  display: grid;
  grid-template-columns: minmax(0, 1fr) 72px;
  gap: 10px;
  align-items: center;
  min-width: 0;
}
.number-input {
  padding: 0 8px;
  text-align: center;
  font-variant-numeric: tabular-nums;
}
input[type="checkbox"] {
  width: 18px;
  height: 18px;
  accent-color: var(--accent);
}
input[type="color"] {
  width: 100%;
  height: 38px;
  padding: 3px;
  border: 1px solid var(--line);
  border-radius: 8px;
  background: #171c20;
}
.treasure-destination-grid { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 8px; align-items: end; }
.treasure-destination-grid label, .treasure-template-label { display: grid; gap: 5px; }
.treasure-template-row { display: grid; grid-template-columns: minmax(0, 1fr) minmax(0, 250px) 38px; gap: 8px; align-items: center; }
.treasure-template-row input[type="text"] { min-width: 0; }
.treasure-color-picker { width: 38px !important; height: 38px !important; padding: 3px !important; cursor: pointer; }
.treasure-type-picker { position: relative; min-width: 0; }
.treasure-choice-picker { position: relative; min-width: 0; }
.treasure-type-trigger { width: 100%; height: 38px; display: flex; align-items: center; gap: 8px; padding: 0 10px; color: var(--text); background: #171c20; border: 1px solid var(--line); border-radius: 8px; cursor: pointer; text-align: left; }
.treasure-choice-trigger { width: 100%; height: 38px; display: flex; align-items: center; padding: 0 10px; color: var(--text); background: #171c20; border: 1px solid var(--line); border-radius: 8px; cursor: pointer; text-align: left; }
.treasure-type-trigger::after, .treasure-choice-trigger::after {
  content: ""; flex: none; width: 6px; height: 6px; margin: -3px 0 0 auto;
  border-right: 1px solid var(--muted); border-bottom: 1px solid var(--muted); transform: rotate(45deg);
}
.treasure-type-trigger span, .treasure-choice-trigger span { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.treasure-type-icon { width: 18px; height: 18px; flex: 0 0 18px; fill: none; stroke: currentColor; stroke-width: 1.8; stroke-linecap: round; stroke-linejoin: round; }
.treasure-type-menu, .treasure-choice-menu { position: absolute; z-index: 20; top: calc(100% + 4px); left: 0; right: 0; display: grid; max-height: 264px; overflow-y: auto; padding: 4px; background: #171c20; border: 1px solid var(--line); border-radius: 8px; box-shadow: 0 12px 24px rgba(0,0,0,.35); }
.treasure-type-picker.open-up .treasure-type-menu, .treasure-choice-picker.open-up .treasure-choice-menu { top: auto; bottom: calc(100% + 4px); }
.treasure-type-menu[hidden] { display: none; }
.treasure-choice-menu[hidden] { display: none; }
.treasure-type-option { height: 32px; display: flex; align-items: center; gap: 8px; padding: 0 8px; border: 0; border-radius: 5px; color: var(--text); background: transparent; cursor: pointer; text-align: left; }
.treasure-choice-option { height: 32px; display: flex; align-items: center; padding: 0 8px; border: 0; border-radius: 5px; color: var(--text); background: transparent; cursor: pointer; text-align: left; }
.treasure-type-option:hover, .treasure-type-option.active { background: #3a3a3e; }
.treasure-choice-option:hover, .treasure-choice-option.active { background: #3a3a3e; }
@container (max-width: 440px) {
  .treasure-destination-grid { grid-template-columns: minmax(0, 1fr); }
  .treasure-template-row { grid-template-columns: minmax(0, 1fr) 38px; }
  .treasure-template-row > input[type="text"] { grid-column: 1 / -1; }
}
.actions {
  display: grid;
  grid-template-columns: 1fr 1fr;
  gap: 8px;
  min-width: 0;
}
.action {
  text-align: center;
  border-color: var(--line);
  background: #1b2125;
  color: var(--text);
}
.action.primary {
  background: var(--accent);
  border-color: var(--accent);
  color: var(--accent-ink);
  font-weight: 700;
}
.action.primary:hover:not(:disabled) { background: #61f1f7; }
.action.primary:focus-visible { outline-color: var(--accent-ink); }
.action:disabled, .action.primary:disabled, .action.danger:disabled {
  background: #29292d;
  border-color: #414146;
  color: #8a8a90;
  cursor: not-allowed;
  opacity: 1;
}
.action:disabled:hover {
  background: #29292d;
  color: #8a8a90;
}
.action.active {
  background: var(--accent-bg);
  border-color: var(--accent-line);
  color: var(--accent);
}
.layout:not(.with-crosshair-preview) .actions {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
}
.layout:not(.with-crosshair-preview) .actions .action {
  max-width: 100%;
  padding-inline: 16px;
  overflow-wrap: anywhere;
}
.layout:not(.with-crosshair-preview) .actions.task-settings-actions {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  align-items: stretch;
  gap: 10px;
}
.task-settings-actions .action {
  width: 100%;
  min-height: 44px;
  white-space: normal;
}
@container (max-width: 560px) {
  .layout:not(.with-crosshair-preview) .actions.task-settings-actions {
    grid-template-columns: minmax(0, 1fr);
  }
}
.layout:not(.with-crosshair-preview) .field-row {
  grid-template-columns: auto auto;
  justify-content: start;
}
.layout:not(.with-crosshair-preview) input[type="number"] {
  width: 120px;
  max-width: 100%;
  height: 36px;
  padding: 0 8px;
  border: 1px solid var(--line);
  border-radius: 6px;
  background: #171c20;
  color: var(--text);
}
.layout:not(.with-crosshair-preview) input:disabled {
  color: var(--faint);
}
.layout:not(.with-crosshair-preview) .field select {
  max-width: 420px;
}
.layout:not(.with-crosshair-preview) .field > label {
  display: flex;
  align-items: center;
  gap: 10px;
  flex-wrap: wrap;
}
.layout:not(.with-crosshair-preview) .field > label input[type="checkbox"] {
  flex: 0 0 auto;
}
.layout:not(.with-crosshair-preview) .update-status {
  min-width: 0;
  overflow-wrap: anywhere;
}
.action.danger {
  color: #f0aab1;
}
.profile-list {
  display: grid;
  gap: 8px;
  max-height: 260px;
  overflow: auto;
}
.profile-list button {
  min-height: 38px;
  border: 1px solid var(--line);
  border-radius: 8px;
  background: #1b2125;
  color: var(--text);
  text-align: left;
  padding: 0 12px;
  cursor: pointer;
  overflow-wrap: anywhere;
}
.profile-list button.active {
  background: var(--accent-bg);
  border-color: var(--accent-line);
  color: var(--accent);
}
.update-status {
  display: grid;
  gap: 8px;
  color: var(--text);
}
.update-status strong {
  color: var(--accent);
}
.release-notes {
  min-height: 0;
  max-height: none;
  overflow: auto;
  color: var(--muted);
}
.release-notes > :first-child { margin-top: 0; }
.release-notes > :last-child { margin-bottom: 0; }
.release-notes h1, .release-notes h2, .release-notes h3,
.release-notes h4, .release-notes h5, .release-notes h6 {
  color: var(--text);
  margin: 16px 0 8px;
}
.release-notes h1 { font-size: 20px; }
.release-notes h2 { font-size: 18px; }
.release-notes h3, .release-notes h4, .release-notes h5, .release-notes h6 { font-size: 16px; }
.release-notes p { margin: 0 0 10px; }
.release-notes ul, .release-notes ol { margin: 0 0 10px; padding-left: 24px; }
.release-notes li + li { margin-top: 4px; }
.release-notes a { color: var(--accent); }
.release-notes code {
  padding: 2px 4px;
  border-radius: 4px;
  background: #25252b;
  color: var(--text);
  font-family: Consolas, monospace;
}
.release-notes pre {
  margin: 0 0 10px;
  padding: 10px;
  overflow: auto;
  border-radius: 6px;
  background: #25252b;
  color: var(--text);
}
.release-notes pre code { padding: 0; background: none; }
.preview-wrap {
  align-self: start;
  margin: calc(var(--caption-space) + clamp(12px, 1.5vw, 23px)) 16px 0 0;
  height: clamp(250px, 48vh, 460px);
  padding: 11px;
  display: grid;
  grid-template-rows: auto minmax(0, 1fr);
  gap: 8px;
}
.preview-head {
  display: flex;
  justify-content: space-between;
  gap: 12px;
  align-items: center;
}
.preview-head h2 {
  margin: 0;
  font-size: 15px;
}
.preview-head span {
  color: var(--muted);
  font-size: 12px;
}
.preview {
  min-height: 0;
  width: 100%;
  height: 100%;
  object-fit: contain;
  background: #13181b;
  border: 1px solid var(--soft);
  border-radius: 8px;
}
.toast {
  position: fixed;
  right: 24px;
  bottom: 24px;
  max-width: 420px;
  padding: 12px 14px;
  background: #351b20;
  border: 1px solid #77313a;
  color: #ffd8dc;
  border-radius: 8px;
  opacity: 0;
  transform: translateY(8px);
  transition: .18s ease;
  pointer-events: none;
}
.toast.show {
  opacity: 1;
  transform: translateY(0);
}
.performance-grid { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 10px; }
.performance-grid .field { min-width: 0; margin: 0; }
.performance-grid input[type="number"] { width: 110px; }
.performance-grid input[type="color"] { width: 70px; height: 34px; padding: 3px; }
.performance-grid select { max-width: 100%; }
.section.compact-settings { width: 100%; max-width: 820px; min-width: 0; align-content: start; gap: 8px; }
.layout.with-crosshair-preview .compact-settings .actions { display: flex; flex-wrap: wrap; }
.compact-settings h2 { font-size: 17px; margin: 0 0 4px; }
.compact-settings h3 { font-size: 14px; margin: 0; }
.compact-settings :is(label, .action) { font-size: 13px; }
.compact-settings [hidden] { display: none !important; }
.compact-settings :is(.field, .compact-disclosure) { padding: 9px 11px; border-radius: 7px; }
.compact-row {
  display: flex;
  align-items: center;
  justify-content: space-between;
  flex-wrap: wrap;
  gap: 6px 14px;
  min-width: 0;
}
.compact-stack { display: grid; gap: 7px; min-width: 0; }
.compact-settings .compact-row :is(input:not([type="checkbox"]), select) { margin-left: auto; }
.compact-settings .compact-row input[type="text"] { width: min(100%, 250px); }
.compact-settings .compact-row select { width: auto; max-width: 100%; }
.compact-settings .compact-row :is(input[type="number"], input[type="text"], select) { height: 33px; }
.compact-settings .compact-row input[type="number"] { width: 93px; }
.compact-settings .compact-row input[type="color"] { width: 46px; height: 26px; }
.position-profile-choices { display: flex; flex-wrap: wrap; align-items: flex-start; gap: 8px 16px; }
.position-profile-choices label {
  display: inline-grid;
  grid-template-columns: auto minmax(0, 1fr);
  align-items: center;
  gap: 8px;
  max-width: 100%;
}
.position-profile-choices input[type="checkbox"] { margin: 0; }
.position-profile-choices label > span { overflow-wrap: anywhere; }
.compact-settings .action { min-height: 33px; }
.compact-settings .field > label.compact-row { display: flex; gap: 6px 14px; }
.compact-settings .compact-row :is(.profile-select, .number-input) { width: auto; }
.compact-settings .compact-row .number-input { width: 78px; }
.compact-settings .compact-slider { display: grid; grid-template-columns: minmax(0, 1fr) minmax(120px, 45%); align-items: center; gap: 6px 12px; }
.compact-settings .compact-slider .slider-row { min-width: 0; }
.compact-settings .compact-slider .slider-row input[type="range"] { width: 100%; min-width: 0; }
.compact-control { min-width: 0; }
.compact-control .compact-value { max-width: min(100%, 48ch); }
.settings-workspace { display: grid; grid-template-columns: minmax(0, 1fr); align-items: start; gap: 10px; }
.settings-side { min-width: 0; }
.settings-side .session-marker-list, .task-result-list { max-height: min(52vh, 460px); overflow: auto; }
.compact-settings .session-marker { min-height: 36px; font-size: 13px; }
.compact-settings .session-empty { font-size: 13px; }
.update-notes-slot .release-notes { max-height: min(58vh, 480px); overflow: auto; }
.compact-settings .release-notes :is(h1, h2, h3, h4) { font-size: 14px; }
.treasure-workspace { --treasure-side: 0; }
.treasure-list { display: grid; gap: 6px; }
.treasure-entry { display: grid; gap: 6px; min-width: 0; }
.treasure-inline:not(:has(> :not([hidden]))), .treasure-capture-side:empty { display: none; }
.treasure-list .treasure-list-item {
  display: grid;
  grid-template-columns: minmax(0, 1fr) auto;
  gap: 3px 8px;
  text-align: left;
  padding: 6px 9px;
}
.treasure-list .treasure-list-item[aria-expanded] { grid-template-columns: minmax(0, 1fr) auto 12px; }
.treasure-list-item[aria-expanded]::after {
  content: "";
  grid-area: 1 / 3;
  place-self: center;
  width: 6px;
  height: 6px;
  border: solid currentColor;
  border-width: 0 1px 1px 0;
  transform: translateY(-2px) rotate(45deg);
}
.treasure-list-item[aria-expanded="true"]::after { transform: translateY(2px) rotate(225deg); }
.treasure-list-item .compact-value { grid-column: 1 / -1; font-size: 12px; }
.treasure-coordinates { justify-content: flex-start; }
.manual-task-progress { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 8px; }
.task-result-list { display: grid; gap: 6px; margin: 0; padding: 0; list-style: none; }
.task-result-list li + li { border-top: 1px solid var(--soft); padding-top: 6px; }
@container (min-width: 980px) {
  .section.compact-settings:has(.settings-workspace) { max-width: 1260px; }
  .settings-workspace { grid-template-columns: minmax(0, 1.32fr) minmax(0, 1fr); gap: 14px; }
  .treasure-workspace { --treasure-side: 1; }
  .treasure-list { max-height: min(52vh, 460px); overflow: auto; }
}
@container (max-width: 440px) {
  .compact-settings .compact-slider { grid-template-columns: minmax(0, 1fr); }
}
.compact-value { color: var(--text); font-variant-numeric: tabular-nums; overflow-wrap: anywhere; }
.compact-disclosure { background: var(--surface); border: 1px solid var(--soft); min-width: 0; }
.compact-disclosure > summary { cursor: pointer; color: var(--text); font-weight: 600; font-size: 13px; }
.compact-disclosure > summary::marker { color: var(--muted); }
.compact-disclosure[open] > summary { margin-bottom: 9px; }
.compact-metrics { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 8px; }
.compact-metrics .field { align-content: start; gap: 3px; }
.compact-metrics strong { font-size: 20px; line-height: 1.15; }
.compact-settings :is(.compact-row .compact-value, .compact-metrics strong) { min-width: 0; }
.editor[data-page="overview"] { display: grid; grid-template-rows: minmax(0, 1fr); overflow: clip; }
.layout:not(.with-crosshair-preview) .section.session {
  width: 100%;
  min-width: 0;
  max-width: none;
  align-content: stretch;
  grid-template-rows: auto auto minmax(0, 1fr) auto;
  gap: clamp(12px, 2.5vh, 26px);
}
.session h1 {
  margin: 0;
  font: 750 clamp(30px, 3vw, 52px)/1.12 "Segoe UI Variable Display", "Segoe UI", sans-serif;
}
.session-bar, .session-card, .session-map {
  background: linear-gradient(120deg, #1e2327, var(--surface) 75%);
  border: 1px solid var(--line);
  border-radius: 8px;
}
.session-bar {
  display: flex;
  align-items: center;
  gap: clamp(15px, 2.7vw, 42px);
  min-height: clamp(43px, 3.7vw, 60px);
  padding: 8px clamp(14px, 2.1vw, 32px);
  font-size: clamp(15px, 1.45vw, 23px);
}
.session-bar > span { display: flex; align-items: center; gap: clamp(8px, 1.2vw, 18px); min-width: 0; }
.session-bar > span + span { border-left: 1px solid #687176; padding-left: clamp(15px, 2.7vw, 42px); }
.session-bar .icon { width: clamp(21px, 1.8vw, 27px); height: clamp(21px, 1.8vw, 27px); }
.session-bar b { font-weight: 500; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
.session-grid {
  display: grid;
  min-height: 0;
  grid-template-columns: minmax(0, 1.44fr) minmax(258px, 1fr);
  grid-template-rows: minmax(340px, 1fr);
  gap: clamp(12px, 1.25vw, 22px);
  overflow: auto;
}
.session-map {
  min-height: 0;
  display: grid;
  place-content: center;
  justify-items: center;
  gap: 10px;
  padding: 14px;
  text-align: center;
  background: radial-gradient(ellipse at 40% 40%, #273333, #1a2226);
}
.session-map p { margin: 0; color: var(--muted); }
.session-side { min-height: 0; display: grid; grid-template-rows: auto minmax(0, 1fr); gap: 14px; }
.session-card { min-width: 0; padding: 14px; }
.session-card h2 { font: 700 clamp(21px, 2vw, 25px)/1.16 "Segoe UI Variable Display", "Segoe UI", sans-serif; margin: 0; }
.session-coordinates { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 10px; margin: 18px 0 12px; }
.session-coordinate { min-width: 0; padding: 9px 14px; background: var(--chrome); border: 1px solid var(--line); border-radius: 8px; color: var(--muted); font-size: 16px; }
.session-coordinate b { display: block; font: 720 clamp(26px, 3.2vw, 38px)/1.1 "Segoe UI Variable Display", "Segoe UI", sans-serif; color: var(--text); font-variant-numeric: tabular-nums; }
.session-visibility { display: flex; align-items: center; gap: 10px; width: 100%; text-align: left; min-height: 38px; font-size: 16px; }
.session-visibility > .icon:last-child { margin-left: auto; margin-right: 3px; width: 16px; height: 16px; color: var(--text); }
.session-markers { min-height: 0; display: grid; grid-template-rows: auto minmax(0, 1fr) auto; gap: 12px; }
.session-marker-list { overflow: auto; scrollbar-width: thin; border-top: 1px solid var(--line); }
.session-markers .session-marker-list { min-height: 0; overscroll-behavior: contain; }
.session-marker {
  display: grid;
  grid-template-columns: auto minmax(0, 1fr) auto auto auto;
  column-gap: 8px;
  align-items: center;
  min-height: 36px;
  padding: 4px 8px;
  border-bottom: 1px solid var(--line);
  font-size: clamp(14px, 1.1vw, 16px);
  font-variant-numeric: tabular-nums;
}
.session-marker > :first-child { color: var(--accent); }
.session-marker > span:nth-child(2) { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.session-marker em { font-style: normal; color: var(--muted); }
.session-marker-menu {
  border: 0;
  border-radius: 5px;
  background: transparent;
  color: var(--text);
  cursor: pointer;
  line-height: 1;
  padding: 6px 5px;
}
.session-marker-menu:is(:hover, [aria-expanded="true"]) { background: var(--raised); }
.session-marker-popover {
  position: fixed;
  inset: auto;
  margin: 0;
  padding: 4px;
  min-width: 204px;
  color: var(--text);
  background: var(--surface);
  border: 1px solid var(--line);
  border-radius: 8px;
  box-shadow: 0 8px 24px #0008;
}
.session-marker-popover button {
  display: block;
  width: 100%;
  min-height: 32px;
  padding: 5px 10px;
  color: inherit;
  background: transparent;
  border: 0;
  border-radius: 4px;
  text-align: left;
  cursor: pointer;
}
.session-marker-popover button:is(:hover, :focus-visible) { background: var(--accent-bg); }
.layout:not(.with-crosshair-preview) .session-marker-actions {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  align-items: stretch;
  gap: 8px 12px;
  margin-bottom: 8px;
}
.layout:not(.with-crosshair-preview) .session-marker-actions .action {
  min-height: 40px;
  padding: 4px 6px;
  line-height: 1.15;
  font-size: clamp(13px, 1.1vw, 16px);
  font-weight: 650;
  overflow-wrap: normal;
}
.session-marker-actions .action:not(.primary) { border-color: #79828a; }
.session-marker-actions .action:disabled { background: #20262b; border-color: #414b52; color: #929ca3; }
.session-marker-actions .action.primary:disabled { background: #203e44; border-color: #31575e; color: #a9c4c8; }
.session-marker-import-hint { grid-column: 1 / -1; flex-basis: 100%; }
.session-empty { margin: 12px 0; color: var(--muted); font-size: clamp(14px, 1.12vw, 18px); }
.session-footer { display: flex; align-items: center; gap: clamp(10px, 2.2vw, 38px); border-top: 1px solid var(--line); padding-top: clamp(4px, 1vh, 12px); }
.session-footer .action { border-color: transparent; background: transparent; padding: 0 7px; color: var(--muted); font-size: clamp(15px, 1.4vw, 23px); }
.session-footer .action + .action { border-left: 1px solid var(--line); border-radius: 0; padding-left: clamp(11px, 2vw, 32px); }
.session-footer .action + .action:hover { border-radius: 0 7px 7px 0; }
.session-footer .action:hover { background: var(--raised); }
.session-footer b { margin-left: 4px; color: var(--accent); font-variant-numeric: tabular-nums; }
@media (max-width: 1160px) {
  .layout.with-crosshair-preview { grid-template-columns: clamp(154px, 15vw, 226px) minmax(0, 1fr); }
  .layout.with-crosshair-preview .editor { padding-bottom: 0; }
  .layout.with-crosshair-preview .preview-wrap { grid-column: 2; margin: 8px 14px 12px; height: clamp(145px, 26vh, 205px); max-width: 792px; }
  .layout.with-crosshair-preview { grid-template-rows: minmax(0, 1fr) auto; }
  .layout.with-crosshair-preview .sidebar { grid-row: span 2; }
}
@media (max-width: 760px) {
  .layout, .layout.with-crosshair-preview { grid-template-columns: 142px minmax(0, 1fr); }
  .session-grid { grid-template-columns: minmax(0, 1fr); grid-template-rows: auto auto; align-content: start; }
  .session-map { min-height: 330px; }
  .session-markers { min-height: 170px; max-height: 330px; }
}
</style>
</head>
<body>
<div class="window-caption" role="group" aria-label="Управление окном">
  <div class="window-top-resize" aria-hidden="true"></div>
  <button type="button" data-window-command="minimize" aria-label="Свернуть" title="Свернуть">
    <svg viewBox="0 0 16 16" fill="none" stroke="currentColor" aria-hidden="true"><path d="M2 8h12"/></svg>
  </button>
  <button type="button" data-window-command="maximize" aria-label="Развернуть" title="Развернуть" aria-pressed="false">
    <svg viewBox="0 0 16 16" fill="none" stroke="currentColor" aria-hidden="true"><rect class="maximize-glyph" x="2.5" y="2.5" width="11" height="11"/><path class="restore-glyph" d="M5.5 4.5V2.5h8v8h-2m-9-5h8v8h-8z"/></svg>
  </button>
  <button type="button" data-window-command="close" aria-label="Закрыть" title="Закрыть">
    <svg viewBox="0 0 16 16" fill="none" stroke="currentColor" aria-hidden="true"><path d="m2.5 2.5 11 11m0-11-11 11"/></svg>
  </button>
</div>
<main class="layout" id="layout">
  <aside class="sidebar" aria-label="Разделы приложения">
    <div class="brand">
      <svg class="brand-icon" viewBox="0 0 24 28" aria-hidden="true"><path fill="currentColor" fill-rule="evenodd" d="M12 0a11 11 0 0 0-11 11c0 7 11 17 11 17s11-10 11-17A11 11 0 0 0 12 0Zm0 7a4 4 0 1 1 0 8 4 4 0 0 1 0-8Z"/></svg>
      <strong>DayZ-Map<br>Companion</strong>
    </div>
    <nav class="nav" id="nav" aria-label="Навигация"></nav>
  </aside>
  <section class="panel editor" id="editor"></section>
  <section class="panel preview-wrap">
    <div class="preview-head">
      <h2>Предпросмотр</h2>
      <span id="activeProfileName"></span>
    </div>
    <img class="preview" id="preview" alt="">
  </section>
</main>
<div class="session-marker-popover" id="session-marker-popover" popover="manual" role="menu" aria-label="Действия с меткой">
  <button type="button" role="menuitem" data-copy-session-marker="coordinates">Копировать координаты</button>
  <button type="button" role="menuitem" data-copy-session-marker="name">Копировать название</button>
</div>
<div class="toast" id="toast"></div>

<!-- marker-icons -->
<script>
const bridge = window.chrome.webview;
const sessionMarkerPopover = document.getElementById("session-marker-popover");
let sessionMarkerSelection = null;
let state = null;
let activeTab = "overview";
let treasureMapId = null;
let treasureProfileId = null;
const treasureTemplateDrafts = new Map();
const treasureCoordinateTimers = new Map();
let treasurePreviewUri = null;
let treasureTypeMenuOpen = false;
let treasureMapMenuOpen = false;
let treasureProfileMenuOpen = false;
let playerPositionSettingsTimer = null;
let treasureTypeMenuUp = false;
let treasureMapMenuUp = false;
let treasureProfileMenuUp = false;
const treasureMarkerTypes = [
  ["default", "⌖", "Обычный маркер", "#3498db"], ["cross", "×", "X", "#3498db"], ["home", "⌂", "Дом", "#e74c3c"], ["camp", "△", "Лагерь", "#27ae60"], ["safezone", "♢", "Безопасная зона", "#2ecc71"], ["blackmarket", "▣", "Чёрный рынок", "#34495e"], ["hospital", "+", "Госпиталь", "#e74c8c"], ["sniper", "⊙", "Снайпер", "#c0392b"], ["player", "♙", "Игрок", "#9b59b6"], ["flag", "⚑", "Флаг", "#d35400"], ["star", "☆", "Звезда", "#f1c40f"], ["car", "▰", "Авто", "#16a085"], ["parking", "P", "Парковка", "#7f8c8d"], ["heli", "✈", "Вертолёт", "#2980b9"], ["rail", "▤", "Железная дорога", "#8e44ad"], ["ship", "⚓", "Корабль", "#3498db"], ["scooter", "◉", "Скутер", "#1abc9c"], ["bank", "¤", "Банк", "#f39c12"], ["restaurant", "●", "Ресторан", "#e67e22"], ["post", "✉", "Почта", "#95a5a6"], ["castle", "♜", "Замок", "#7d3c98"], ["ranger-station", "♲", "Станция рейнджера", "#27ae60"], ["water", "♒", "Вода", "#3498db"], ["triangle", "▲", "Треугольник", "#e74c3c"], ["cow", "♧", "Корова", "#8b4513"], ["bear", "♛", "Медведь", "#2c3e50"], ["car-repair", "⚒", "Ремонт авто", "#d35400"], ["communications", "⌁", "Коммуникации", "#9b59b6"], ["roadblock", "▰", "Блокпост", "#c0392b"], ["stadium", "▭", "Стадион", "#f1c40f"], ["skull", "☠", "Череп", "#2c3e50"], ["rocket", "▲", "Ракета", "#e74c3c"], ["bbq", "♨", "BBQ", "#d35400"], ["ping", "●", "Пинг", "#2ecc71"], ["circle", "●", "Круг", "#3498db"]
].map(([id, icon, name, color]) => ({ id, icon, name, color }));
const treasureMarkerIcon = (type, color) => `<svg class="treasure-type-icon" style="color:${color}" viewBox="0 0 24 24" aria-hidden="true"><use href="#${type}"></use></svg>`;
const playerPositionShapes = [
  ["triangle", "▲", "Треугольник"], ["circle", "●", "Круг"], ["square", "■", "Квадрат"], ["diamond", "◆", "Ромб"],
  ["heart", "♥", "Сердце"], ["cross", "✚", "Крест"], ["star", "★", "Звезда"],
  ["hexagon", "⬢", "Шестиугольник"], ["pentagon", "⬠", "Пятиугольник"], ["x", "✕", "X"], ["paw", "🐾", "Лапка"]
].map(([id, icon, name]) => ({ id, icon, name }));
document.addEventListener("keydown", event => {
  if (event.key === "Escape") {
    if (sessionMarkerSelection) {
      closeSessionMarkerMenu(true);
      event.preventDefault();
      event.stopPropagation();
      return;
    }
    const hadPreview = Boolean(treasurePreviewUri);
    treasurePreviewUri = null;
    treasureMapMenuOpen = false;
    treasureProfileMenuOpen = false;
    treasureTypeMenuOpen = false;
    document.querySelectorAll(".treasure-choice-menu, .treasure-type-menu").forEach(menu => { menu.hidden = true; });
    if (hadPreview) render();
    event.preventDefault();
    event.stopPropagation();
    return;
  }
  if (event.key !== "ArrowDown" && event.key !== "ArrowUp" && event.key !== "Enter") return;
  const menu = [...document.querySelectorAll(".treasure-choice-menu:not([hidden]), .treasure-type-menu:not([hidden])")][0];
  if (!menu) return;
  const options = [...menu.querySelectorAll("button")];
  if (!options.length) return;
  if (event.key === "Enter" && document.activeElement?.matches(".treasure-choice-option, .treasure-type-option")) {
    event.preventDefault();
    document.activeElement.click();
    event.stopPropagation();
    return;
  }
  if (event.key !== "Enter") {
    event.preventDefault();
    const current = options.indexOf(document.activeElement);
    const next = current < 0 ? options.findIndex(item => item.classList.contains("active")) : current + (event.key === "ArrowUp" ? -1 : 1);
    options[(next + options.length) % options.length].focus();
    event.stopPropagation();
  }
}, true);
let hotkeyCapture = null;
let pendingConfig = null;
let pendingConfigTimer = null;
let selectedProfileId = null;
let selectedTreasureId = null;
let treasureCaptureExpanded = true;
let dayZStatusPending = false;
let dayZActionFeedback = "";

const defaultHotkeys = {
  ToggleOverlay: { Enabled: true, Key: "X", Control: true, Alt: true, Shift: false, Win: false },
  PreviousProfile: { Enabled: true, Key: "Left", Control: true, Alt: true, Shift: false, Win: false },
  NextProfile: { Enabled: true, Key: "Right", Control: true, Alt: true, Shift: false, Win: false },
  OpacityUp: { Enabled: true, Key: "Up", Control: true, Alt: true, Shift: false, Win: false },
  OpacityDown: { Enabled: true, Key: "Down", Control: true, Alt: true, Shift: false, Win: false },
  SizeUp: { Enabled: true, Key: "PageUp", Control: true, Alt: true, Shift: false, Win: false },
  SizeDown: { Enabled: true, Key: "PageDown", Control: true, Alt: true, Shift: false, Win: false },
  ToggleBattlePassOverlay: { Enabled: true, Key: "F8", Control: true, Alt: true, Shift: false, Win: false },
  ScanBattlePass: { Enabled: false, Key: "None", Control: true, Alt: true, Shift: false, Win: false },
  EditBattlePassOverlay: { Enabled: true, Key: "F10", Control: true, Alt: true, Shift: false, Win: false },
  ToggleBattlePassDescriptions: { Enabled: true, Key: "F12", Control: true, Alt: true, Shift: false, Win: false },
  CaptureTreasure: { Enabled: true, Key: "F7", Control: true, Alt: true, Shift: false, Win: false }
};

const iconPaths = {
  chevronDown: '<path d="m4 8 8 8 8-8"/>',
  overview: '<path d="m3 11 9-8 9 8M5 10v11h5v-6h4v6h5V10"/>',
  position: '<circle cx="12" cy="12" r="8"/><path d="M12 2v4m0 12v4M2 12h4m12 0h4"/>',
  markers: '<path d="M20 10c0 5-8 11-8 11S4 15 4 10a8 8 0 0 1 16 0Z"/><circle cx="12" cy="10" r="2.5"/>',
  marker: '<path stroke="none" fill="currentColor" fill-rule="evenodd" d="M12 2a8 8 0 0 0-8 8c0 5 8 12 8 12s8-7 8-12a8 8 0 0 0-8-8Zm0 5a3 3 0 1 1 0 6 3 3 0 0 1 0-6Z"/>',
  treasures: '<rect x="3" y="8" width="18" height="13" rx="2"/><path d="M3 12h18M10 12v4h4v-4M8 8V5h8v3"/>',
  crosshair: '<circle cx="12" cy="12" r="7"/><path d="M12 2v6m0 8v6M2 12h6m8 0h6"/>',
  tasks: '<rect x="5" y="5" width="14" height="16" rx="2"/><path d="M9 5V3h6v2M9 10h6m-6 4h6m-6 4h4"/>',
  application: '<circle cx="12" cy="12" r="3"/><path d="M10 3h4l.6 2.4 2 .9 2.2-1.1 2 3.5-1.6 1.7v2.4l1.6 1.7-2 3.5-2.2-1.1-2 .9L14 21h-4l-.6-2.4-2-.9-2.2 1.1-2-3.5 1.6-1.7v-2.4L3.2 8.5l2-3.5 2.2 1.1 2-.9Z"/>',
  server: '<rect x="3" y="4" width="18" height="7" rx="1"/><rect x="3" y="13" width="18" height="7" rx="1"/><path d="M7 7.5h.01m3 0H17M7 16.5h.01m3 0H17"/>',
  map: '<path d="m3 6 6-3 6 3 6-3v15l-6 3-6-3-6 3Zm6-3v15m6-12v15"/>',
  eye: '<path d="M2 12s4-6 10-6 10 6 10 6-4 6-10 6-10-6-10-6Z"/><circle cx="12" cy="12" r="2.5"/>',
  player: '<path fill="currentColor" stroke="#152123" d="m6 4 14 8-7 1-3 7Z"/>'
};
const icon = name => `<svg class="icon" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">${iconPaths[name]}</svg>`;

const navigation = [
  { icon: "overview", tab: ["overview", "Обзор"] },
  { icon: "position", tab: ["player-position", "Позиция"] },
  { icon: "markers", tab: ["dayz", "Метки"] },
  { icon: "treasures", tab: ["treasures", "Клады"] },
  { id: "crosshair", icon: "crosshair", label: "Прицел", tabs: [
    ["crosshair", "Настройка прицела"],
    ["image", "Изображение"],
    ["profiles", "Профили"],
    ["hotkeys", "Горячие клавиши"],
    ["monitor", "Монитор"]
  ] },
  { id: "tasks", icon: "tasks", label: "Задания", tabs: [
    ["tasks", "Сканирование"],
    ["taskhotkeys", "Горячие клавиши"]
  ] },
  { id: "application", icon: "application", label: "Настройки", tabs: [
    ["general", "Общие"],
    ["performance", "Пинг и FPS"],
    ["updates", "Обновление"]
  ] }
];
let expandedNavGroups = new Set();
const expandedSettings = new Set();
let queryPortDraft = null;

function settingsDetails(key, title, content) {
  return `<details class="compact-disclosure" data-settings-disclosure="${key}" ${expandedSettings.has(key) ? "open" : ""}>
    <summary>${title}</summary><div class="compact-stack">${content}</div>
  </details>`;
}

function post(message) {
  bridge.postMessage(message);
}

function postConfig(config, options = {}) {
  if (options.previewOnly) {
    post({ type: "previewConfig", config });
    return;
  }

  if (!options.debounce) {
    clearPendingConfig();
    post({ type: "updateConfig", config });
    return;
  }

  const interval = Number(options.debounce) || 120;
  pendingConfig = clone(config);
  if (pendingConfigTimer) {
    return;
  }

  flushPendingConfig();
  pendingConfigTimer = setTimeout(() => {
    pendingConfigTimer = null;
    if (pendingConfig) {
      flushPendingConfig();
    }
  }, interval);
}

function flushPendingConfig() {
  if (!pendingConfig) return;
  const config = pendingConfig;
  pendingConfig = null;
  post({ type: "updateConfig", config });
}

function clearPendingConfig() {
  clearTimeout(pendingConfigTimer);
  pendingConfigTimer = null;
  pendingConfig = null;
}

function profile() {
  return state.config.Profiles.find(item => item.Id === state.config.ActiveProfileId) || state.config.Profiles[0];
}

function clone(value) {
  return JSON.parse(JSON.stringify(value));
}

function update(mutator, options = {}) {
  const next = clone(state.config);
  mutator(next);
  state.config = next;
  if (options.render !== false) {
    render();
  }
  postConfig(next, options);
}

function updateProfile(mutator, options = {}) {
  update(config => {
    const item = config.Profiles.find(p => p.Id === config.ActiveProfileId) || config.Profiles[0];
    mutator(item);
  }, options);
}

function activeProfile(config) {
  return config.Profiles.find(item => item.Id === config.ActiveProfileId) || config.Profiles[0];
}

function selectedProfile(config = state.config) {
  return config.Profiles.find(item => item.Id === selectedProfileId)
    || activeProfile(config)
    || config.Profiles[0];
}

function newId() {
  if (crypto.randomUUID) return crypto.randomUUID().replaceAll("-", "");
  const values = new Uint8Array(16);
  crypto.getRandomValues(values);
  return Array.from(values, b => b.toString(16).padStart(2, "0")).join("");
}

function field(label, inner) {
  return `<div class="field"><div class="field-row"><label>${label}</label>${inner.value || ""}</div>${inner.input}</div>`;
}

function settingRow(label, control) {
  return `<label class="field compact-row"><span>${label}</span>${control}</label>`;
}

function slider(label, path, min, max) {
  const p = profile();
  const value = getPath(p, path);
  return `<div class="field compact-slider"><label for="range-${path}">${label}</label>
    <div class="slider-row"><input type="range" id="range-${path}" min="${min}" max="${max}" value="${value}" data-slider="${path}">
    <input class="number-input" type="number" min="${min}" max="${max}" value="${value}" data-number="${path}" aria-label="${label}"></div></div>`;
}

function check(label, path) {
  const checked = getPath(profile(), path) ? "checked" : "";
  return settingRow(label, `<input type="checkbox" ${checked} data-check="${path}">`);
}

function color(label, path, shared = false) {
  const c = getPath(profile(), path);
  const hex = "#" + [c.R, c.G, c.B].map(v => v.toString(16).padStart(2, "0")).join("");
  return settingRow(label, `<input type="color" value="${hex}" ${shared ? "data-color-all" : `data-color="${path}"`}>`);
}

const crosshairShapes = [
  ["Classic", "Классика"], ["TightPlus", "Плотный плюс"], ["OpenPlus", "Сплошной плюс"], ["Corners", "Уголки"],
  ["Box", "Рамка"], ["Circle", "Кольцо"], ["Arcs", "Дуги"], ["Chevron", "Шеврон"], ["Brackets", "Скобки"],
  ["DotCorners", "Точки по углам"], ["Sniper", "Снайперский"]
];
const dotShapes = [
  ["Circle", "Круг"], ["Square", "Квадрат"], ["Diamond", "Ромб"], ["Triangle", "Треугольник"],
  ["Heart", "Сердце"], ["Cross", "Крест"], ["Star", "Звезда"], ["Hexagon", "Шестиугольник"],
  ["Pentagon", "Пятиугольник"], ["X", "X"], ["Paw", "Лапа"]
];

function shapeIndex(value, options) {
  return typeof value === "number" ? value : options.findIndex(([id]) => id === value);
}

function selectShape(label, path, options) {
  const selected = shapeIndex(getPath(profile(), path), options);
  const html = options.map(([, name], index) => `<option value="${index}" ${index === selected ? "selected" : ""}>${name}</option>`).join("");
  return settingRow(label, `<select data-select="${path}">${html}</select>`);
}

function text(label, id, value) {
  return settingRow(label, `<input type="text" id="${id}" value="${escapeHtml(value || "")}">`);
}

function getPath(obj, path) {
  return path.split(".").reduce((current, key) => current[key], obj);
}

function setPath(obj, path, value) {
  const parts = path.split(".");
  const last = parts.pop();
  const target = parts.reduce((current, key) => current[key], obj);
  target[last] = value;
}

function escapeHtml(value) {
  return String(value).replace(/[&<>"']/g, ch => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", "\"": "&quot;", "'": "&#39;" }[ch]));
}

function renderMarkdown(markdown) {
  const inline = value => escapeHtml(value)
    .replace(/`([^`]+)`/g, "<code>$1</code>")
    .replace(/\*\*([^*]+)\*\*/g, "<strong>$1</strong>")
    .replace(/__([^_]+)__/g, "<strong>$1</strong>")
    .replace(/(?<!\*)\*([^*]+)\*(?!\*)/g, "<em>$1</em>")
    .replace(/(?<!_)_([^_]+)_(?!_)/g, "<em>$1</em>")
    .replace(/\[([^\]]+)\]\((https?:\/\/[^\s)]+)\)/g, (_, label, url) => `<a href="${url}" target="_blank" rel="noopener noreferrer">${label}</a>`);

  const lines = String(markdown).replace(/\r\n?/g, "\n").split("\n");
  const output = [];
  let paragraph = [];
  let listType = null;
  let code = null;

  const closeParagraph = () => {
    if (paragraph.length) {
      output.push(`<p>${inline(paragraph.join("\n")).replace(/\n/g, "<br>")}</p>`);
      paragraph = [];
    }
  };
  const closeList = () => {
    if (listType) {
      output.push(`</${listType}>`);
      listType = null;
    }
  };

  for (const line of lines) {
    if (line.startsWith("```")) {
      closeParagraph();
      closeList();
      if (code) {
        output.push(`<pre><code>${escapeHtml(code.join("\n"))}</code></pre>`);
        code = null;
      } else {
        code = [];
      }
      continue;
    }
    if (code) {
      code.push(line);
      continue;
    }

    const heading = line.match(/^(#{1,6})\s+(.+)$/);
    const unordered = line.match(/^\s*[-*+]\s+(.+)$/);
    const ordered = line.match(/^\s*\d+[.)]\s+(.+)$/);
    if (heading) {
      closeParagraph();
      closeList();
      const level = heading[1].length;
      output.push(`<h${level}>${inline(heading[2])}</h${level}>`);
    } else if (unordered || ordered) {
      closeParagraph();
      const nextType = unordered ? "ul" : "ol";
      if (listType !== nextType) {
        closeList();
        listType = nextType;
        output.push(`<${listType}>`);
      }
      output.push(`<li>${inline((unordered || ordered)[1])}</li>`);
    } else if (!line.trim()) {
      closeParagraph();
      closeList();
    } else {
      closeList();
      paragraph.push(line);
    }
  }

  if (code) output.push(`<pre><code>${escapeHtml(code.join("\n"))}</code></pre>`);
  closeParagraph();
  closeList();
  return output.join("");
}

function render() {
  if (!state) return;
  const showPreview = ["crosshair", "image", "profiles"].includes(activeTab);
  document.getElementById("layout").classList.toggle("with-crosshair-preview", showPreview);
  renderSidebar();
  renderEditor();
  setCrosshairPreview(state.preview);
  document.querySelector(".preview-head h2").textContent = activeTab === "profiles" ? "Активный прицел" : "Предпросмотр";
  document.getElementById("activeProfileName").textContent = profile()?.Name || "";
}

function setCrosshairPreview(source) {
  const preview = document.getElementById("preview");
  if (source) preview.src = source;
  else preview.removeAttribute("src");
}

function isEditingValueControl() {
  const active = document.activeElement;
  return !!active?.matches?.("input, select, textarea, [contenteditable='true']");
}

function renderDataChanged(previous, next) {
  if (!previous) return true;
  // DayZ status is polled periodically. It is not form data, so it must not
  // recreate the whole editor and steal focus from a field or an open select.
  const settingsWithoutPositionTelemetry = settings => {
    if (!settings?.PlayerPositionTracking) return settings;
    const { LastX, LastY, LastZ, LastSentAt, ConsecutiveErrors, LastError, ...tracking } = settings.PlayerPositionTracking;
    return { ...settings, PlayerPositionTracking: tracking };
  };
  const markerFileStatus = status => {
    if (!status || activeTab !== "dayz") return undefined;
    const { SessionMarkers, MarkerServer, ...fileStatus } = status;
    return fileStatus;
  };
  const stable = value => ({
    config: value.config,
    openTab: value.openTab,
    update: value.update,
    updateProgress: value.updateProgress,
    ocr: value.ocr,
    dayZSettings: settingsWithoutPositionTelemetry(value.dayZ?.settings),
    markerFileStatus: markerFileStatus(value.dayZ?.status),
    battlePass: value.battlePass,
    treasures: value.treasures,
    positionDestination: { maps: value.playerPosition?.destination?.Maps, profiles: value.playerPosition?.destination?.Profiles },
    hotkeyErrors: value.hotkeyErrors,
    monitors: value.monitors,
    preview: value.preview
  });
  return JSON.stringify(stable(previous)) !== JSON.stringify(stable(next));
}

function keepEditedState(previous, next) {
  return {
    ...previous,
    preview: next.preview,
    markerImportBlockReason: next.markerImportBlockReason,
    dayZ: next.dayZ ? { ...previous.dayZ, status: next.dayZ.status } : previous.dayZ
  };
}

function renderSidebar() {
  document.getElementById("nav").innerHTML = navigation.map(item => {
    if (item.tab) {
      const [id, label] = item.tab;
      const current = id === activeTab;
      return `<button class="nav-main ${current ? "active" : ""}" ${current ? 'aria-current="page"' : ""} data-tab="${id}">${icon(item.icon)}${label}</button>`;
    }
    const open = expandedNavGroups.has(item.id);
    const current = item.tabs.some(([id]) => id === activeTab);
    const children = item.tabs.map(([id, label]) => `<button ${id === activeTab ? 'class="active" aria-current="page"' : ""} data-tab="${id}">${label}</button>`).join("");
    return `<div class="nav-group"><button class="nav-group-toggle ${current ? "current" : ""}" aria-expanded="${open}" data-nav-group="${item.id}">${icon(item.icon)}${item.label}<span aria-hidden="true">${open ? "▾" : "▸"}</span></button>${open ? `<div class="nav-children">${children}</div>` : ""}</div>`;
  }).join("");
}

function expandNavForTab(tab) {
  expandedNavGroups.clear();
  const group = navigation.find(item => item.tabs?.some(([id]) => id === tab));
  if (group) expandedNavGroups.add(group.id);
}

function selectEditorTab(tab) {
  activeTab = tab === "performance-settings" ? "performance" : tab;
  if (tab === "performance-settings") expandedSettings.add("performance-overlay");
  expandNavForTab(activeTab);
}

function renderEditor() {
  closeSessionMarkerMenu();
  const sections = {
    overview: renderOverview,
    general: renderGeneral,
    crosshair: renderCrosshair,
    image: renderImage,
    hotkeys: renderHotkeys,
    taskhotkeys: renderBattlePassHotkeys,
    monitor: renderMonitor,
    dayz: renderDayZ,
    "player-position": renderPlayerPosition,
    performance: renderPerformance,
    treasures: renderTreasures,
    tasks: renderBattlePass,
    profiles: renderProfiles,
    updates: renderUpdates
  };
  const editor = document.getElementById("editor");
  const compact = activeTab !== "overview";
  const scrollTop = compact && editor.dataset.page === activeTab ? editor.scrollTop : 0;
  editor.dataset.page = activeTab;
  const sectionClass = compact ? "compact-settings" : "session";
  editor.innerHTML = `<div class="section active ${sectionClass}">${(sections[activeTab] || renderOverview)()}</div>`;
  if (compact) editor.scrollTop = scrollTop;
  syncPresetButtons();
  bindEditorEvents();
}

function syncPresetButtons() {
  const preset = currentPreset(profile());
  document.querySelectorAll("[data-preset]").forEach(button => {
    button.classList.remove("primary");
    button.classList.toggle("active", button.dataset.preset === preset);
  });
}

function renderCrosshair() {
  const presets = [["classic", "Классика"], ["dot", "Точка"], ["compact", "Компакт"], ["thin", "Тонкий"],
    ["bold", "Жирный"], ["diamond", "Ромб"], ["crossdot", "Крест-точка"], ["t", "Т-форма"], ["x", "X"]];
  return `<h2>Прицел</h2>
    <div class="field"><h3>Пресеты</h3><div class="actions">
      ${presets.map(([id, label]) => `<button class="action" data-preset="${id}">${label}</button>`).join("")}
    </div></div>
    ${check("Отображение прицела", "ProceduralEnabled")}
    ${settingRow("Показывать только в DayZ", `<input type="checkbox" data-general-check="ShowOnlyInGame" ${state.config.ShowOnlyInGame ? "checked" : ""}>`)}
    ${color("Общий цвет прицела", "Color", true)}
    ${settingsDetails("crosshair-lines", "Перекрестие", `
      ${check("Перекрестие", "CrosshairEnabled")}
      ${selectShape("Форма", "CrosshairShape", crosshairShapes)}
      ${slider("Длина", "Length", 1, 80)}
      ${slider("Зазор", "Gap", 0, 50)}
      ${slider("Толщина", "Thickness", 1, 20)}
      ${slider("Поворот", "CrosshairRotation", 0, 359)}
      ${check("Т-форма", "TShape")}
      ${slider("Прозрачность перекрестия", "Color.A", 0, 255)}
      ${color("Цвет перекрестия", "Color")}`)}
    ${settingsDetails("crosshair-dot", "Точка", `
      ${check("Центральная точка", "DotEnabled")}
      ${selectShape("Форма точки", "DotShape", dotShapes)}
      ${slider("Размер точки", "DotSize", 1, 30)}
      ${slider("Прозрачность точки", "DotOpacity", 0, 255)}
      ${color("Цвет точки", "DotColor")}`)}
    ${settingsDetails("crosshair-outline", "Обводка", `
      ${check("Обводка", "OutlineEnabled")}
      ${slider("Толщина обводки", "OutlineThickness", 1, 10)}
      ${slider("Прозрачность обводки", "OutlineColor.A", 0, 255)}
      ${color("Цвет обводки", "OutlineColor")}`)}`;
}

function renderGeneral() {
  const overlaySize = state.config.OverlayWindowSize;
  return `<h2>Общие</h2>
    ${settingRow("Запускать вместе с Windows", `<input type="checkbox" ${state.config.StartWithWindows ? "checked" : ""} data-general-check="StartWithWindows">`)}
    ${settingRow("Запускать свёрнутым в трей", `<input type="checkbox" ${state.config.StartMinimizedToTray ? "checked" : ""} data-general-check="StartMinimizedToTray">`)}
    ${settingRow("Размер окна прицела", `<select data-overlay-size>
      <option value="Compact200" ${overlaySize === "Compact200" ? "selected" : ""}>200 × 200 (по умолчанию)</option>
      <option value="QuarterScreen" ${overlaySize === "QuarterScreen" ? "selected" : ""}>25% экрана</option>
      <option value="HalfScreen" ${overlaySize === "HalfScreen" ? "selected" : ""}>50% экрана</option>
      <option value="ThreeQuartersScreen" ${overlaySize === "ThreeQuartersScreen" ? "selected" : ""}>75% экрана</option>
      <option value="FullScreen" ${overlaySize === "FullScreen" ? "selected" : ""}>100% экрана</option>
    </select>`)}
    <div class="actions"><button class="action danger" data-command="exitApplication">Выйти из приложения</button></div>`;
}

function renderImage() {
  const layer = profile().ImageLayer;
  return `<h2>Изображение</h2>
    <div class="field">
      <div class="compact-row compact-control"><h3>Файл изображения</h3><span class="compact-value">${escapeHtml(layer.Path || "Не выбран")}</span></div>
      <div class="actions"><button class="action primary" data-command="importImage">Импортировать</button>
        <button class="action" data-image="clear" ${layer.Path ? "" : "disabled"}>Очистить</button></div>
    </div>
    ${check("Включить изображение", "ImageLayer.Enabled")}
    ${settingsDetails("image-display", "Отображение", `
      ${slider("Масштаб", "ImageLayer.ScalePercent", 1, 400)}
      ${slider("Прозрачность", "ImageLayer.Opacity", 0, 255)}
      ${slider("Поворот", "ImageLayer.Rotation", 0, 359)}`)}
    ${settingsDetails("image-placement", "Положение", `
      ${slider("Смещение X", "ImageLayer.OffsetX", -500, 500)}
      ${slider("Смещение Y", "ImageLayer.OffsetY", -500, 500)}
      <div class="actions"><button class="action" data-image="center">Центр якоря</button><button class="action" data-image="reset">Сброс позиции</button></div>`)}`;
}

function renderOcrStatus(ocr = state.ocr) {
  const ready = !!ocr?.Ready;
  const status = ready ? "OCR: готово" : "OCR: не установлен";
  return field("Распознавание текста", { input: `<div class="update-status"><strong>${status}</strong><span>${escapeHtml(ocr?.Message || "Проверка OCR ещё не выполнена.")}</span></div><div class="actions"><button class="action primary" data-command="installOcr" ${ready ? "disabled" : ""}>Установить OCR</button><button class="action" data-command="refreshOcr">Проверить снова</button></div>` });
}

function renderPerformance() {
  const settings = { ...(state.performanceDefaults || {}), ...(state.dayZ?.settings?.Performance || {}) };
  const port = settings.QueryPort || 0;
  const manualPort = port > 0 || queryPortDraft !== null;
  const metric = (label, key, status = "") => `<div class="field">
    <span>${label}</span><strong class="compact-value" data-performance-value="${key}">—</strong>
    ${status ? `<span class="limit" data-performance-value="${status}" data-performance-note></span>` : ""}
  </div>`;
  return `<h2>Пинг и FPS</h2>
    <div class="field">
      <label class="compact-row">Измерять пинг и FPS DayZ <input type="checkbox" data-performance-check="Enabled" ${settings.Enabled ? "checked" : ""}></label>
      ${settings.Enabled ? '<span class="limit" data-performance-value="HelperStatus" data-performance-note></span>' : ""}
    </div>
    <div class="compact-metrics">
      ${metric("Текущий FPS", "Fps", "FpsStatus")}
      ${metric("Пинг сервера", "Ping", "PingStatus")}
      ${settings.ShowFrameTime ? metric("Время кадра", "FrameTime") : ""}
      ${settings.ShowOnePercentLow ? metric("1% low", "OnePercentLow") : ""}
      ${settings.ShowJitter ? metric("Колебания пинга", "Jitter") : ""}
    </div>
    ${settings.ShowFrameGraph ? `<div class="field"><h3>Время кадра за последнюю минуту</h3><canvas data-performance-graph style="width:100%;height:110px"></canvas></div>` : ""}
    ${settingsDetails("performance-overlay", "Оверлей FPS", renderPerformanceOverlay(settings))}
    ${settingsDetails("performance-connection", "Сервер и опрос", `
      <div class="field">
        <div class="compact-row"><span>Текущий сервер</span><span class="compact-value" data-performance-value="Server">—</span></div>
        <div class="compact-row"><span>Сетевой адаптер</span><span class="compact-value" data-performance-value="Adapter">—</span></div>
        <div class="compact-row"><span>Адрес опроса</span><span class="compact-value" data-performance-value="Query">—</span></div>
      </div>
      <div class="field">
        <label class="compact-row">Порт опроса <select data-performance-port-mode>
          <option value="auto" ${manualPort ? "" : "selected"}>Автопоиск</option>
          <option value="manual" ${manualPort ? "selected" : ""}>Вручную</option>
        </select></label>
        <form class="compact-row" data-performance-port-form ${manualPort ? "" : "hidden"}>
          <label class="compact-row">Порт <input type="number" min="1" max="65535" step="1" required data-performance-port value="${escapeHtml(queryPortDraft ?? (port || ""))}"></label>
          <button class="action" type="submit">Применить</button>
        </form>
        <span class="limit">Пинг измеряется по UDP-ответам A2S и может отличаться от значения в игре.</span>
      </div>`)}`;
}

function renderPerformanceOverlay(s) {
  const check = (label, key) => `<label class="compact-row">${label}<input type="checkbox" data-performance-check="${key}" ${s[key] ? "checked" : ""}></label>`;
  const number = (label, key, min, max) => `<label class="field compact-row">${label}<input type="number" data-performance-number="${key}" min="${min}" max="${max}" step="1" value="${s[key]}"></label>`;
  const opacity = (label, key) => {
    const value = s[key] ?? s.Opacity ?? 90;
    return `<label class="field"><span>${label} · <span data-performance-opacity-label="${key}">${value}</span>%</span><input type="range" data-performance-number="${key}" min="0" max="100" step="1" value="${value}"></label>`;
  };
  const color = (label, key) => `<label class="field compact-row">${label}<input type="color" data-performance-color="${key}" value="${s[key]}"></label>`;
  const monitors = (state.monitors || []).map(m => `<option value="${escapeHtml(m.DeviceName)}" ${m.DeviceName === s.MonitorDeviceName ? "selected" : ""}>${escapeHtml(m.DisplayName)}</option>`).join("");
  return `<div class="field">
      ${check("Показывать оверлей", "ShowOverlay")}
      ${check("Показывать только в DayZ", "ShowOnlyInGame")}
    </div>
    ${settingsDetails("overlay-metrics", "Показатели", `<div class="field">
      ${check("Показывать FPS", "ShowFps")}
      ${check("Показывать пинг", "ShowPing")}
      ${check("Время кадра", "ShowFrameTime")}
      ${check("1% low", "ShowOnePercentLow")}
      ${check("График просадок", "ShowFrameGraph")}
      ${check("Колебания пинга", "ShowJitter")}
    </div>`)}
    ${settingsDetails("overlay-placement", "Расположение и размер", `
      <div class="field">
        ${check("Разрешить перемещение и изменение размера", "EditingEnabled")}
        ${s.EditingEnabled ? '<div class="limit">Перетащите оверлей или потяните за край. Выключите этот режим, чтобы клики снова проходили в игру.</div>' : ""}
        <label class="compact-row">Монитор <select data-performance-select="MonitorDeviceName"><option value="" ${!s.MonitorDeviceName ? "selected" : ""}>Основной монитор</option>${monitors}</select></label>
      </div>
      <div class="performance-grid">
        ${number("Ширина, px", "Width", 120, 1200)}${number("Высота, px", "Height", 32, 500)}
        ${number("Отступ слева, px", "Left", 0, 100000)}${number("Отступ сверху, px", "Top", 0, 100000)}
      </div>
      <div class="actions"><button class="action" data-command="fitPerformanceOverlay">Подогнать размер по содержимому</button></div>`)}
    ${settingsDetails("overlay-style", "Оформление", `
      <div class="performance-grid">${color("Цвет текста", "TextColor")}${color("Цвет фона", "BackgroundColor")}</div>
      <div class="performance-grid">${opacity("Непрозрачность текста", "TextOpacity")}${opacity("Непрозрачность фона", "BackgroundOpacity")}</div>
      <div class="field compact-row"><span>Шрифт · ${escapeHtml(s.FontFamily || "Segoe UI")}</span><button class="action" data-command="selectPerformanceFont">Выбрать шрифт…</button></div>
      <div class="performance-grid">
        ${number("Размер шрифта, pt", "FontSize", 8, 72)}
        <div class="field">${check("Жирный", "Bold")}${check("Курсив", "Italic")}</div>
      </div>
      <div class="field">
        <label class="compact-row">Расположение показателей <select data-performance-select="VerticalLayout"><option value="false" ${!s.VerticalLayout ? "selected" : ""}>В одну строку</option><option value="true" ${s.VerticalLayout ? "selected" : ""}>Друг под другом</option></select></label>
        <label class="compact-row">Выравнивание текста <select data-performance-select="Alignment">${[["Left", "Слева"], ["Center", "По центру"], ["Right", "Справа"]].map(([value, label]) => `<option value="${value}" ${s.Alignment === value ? "selected" : ""}>${label}</option>`).join("")}</select></label>
      </div>`)}
    ${settingsDetails("overlay-thresholds", "Цвет по порогам", `
      <div class="field">${check("Подсвечивать FPS, 1% low и пинг по порогам", "ColorByThresholds")}</div>
      <div class="performance-grid">
        ${number("FPS: предупреждение ниже", "FpsWarning", 1, 1000)}${number("FPS: критический ниже", "FpsCritical", 1, 1000)}
        ${number("Пинг: предупреждение от, мс", "PingWarning", 1, 10000)}${number("Пинг: критический от, мс", "PingCritical", 1, 10000)}
      </div>
      <div class="performance-grid">${color("Норма", "GoodColor")}${color("Предупреждение", "WarningColor")}${color("Критическое значение", "CriticalColor")}</div>`)}
    <div class="actions"><button class="action" data-performance-reset>Сбросить настройки оверлея</button></div>`;
}

function refreshPerformanceStatus() {
  const data = state?.performance || {};
  document.querySelectorAll("[data-performance-value]").forEach(element => {
    const key = element.dataset.performanceValue;
    const value = data[key];
    if (element.hasAttribute("data-performance-note")) element.hidden = !value;
    element.textContent = key === "Fps" || key === "OnePercentLow" ? (Number.isFinite(value) ? Math.round(value).toString() : "—")
      : key === "Ping" ? (Number.isFinite(value) ? `${Math.round(value)} мс` : "—")
      : key === "FrameTime" || key === "Jitter" ? (Number.isFinite(value) ? `${value.toFixed(1)} мс` : "—")
      : value || "—";
  });
  const canvas = document.querySelector("[data-performance-graph]");
  if (canvas) {
    const width = Math.max(1, Math.floor(canvas.clientWidth)), height = 110, ratio = window.devicePixelRatio || 1;
    canvas.width = Math.round(width * ratio); canvas.height = Math.round(height * ratio);
    const ctx = canvas.getContext("2d"); ctx.scale(ratio, ratio);
    const values = data.FrameTimeHistory || [], finite = values.filter(Number.isFinite), max = Math.max(33.3, ...finite);
    ctx.strokeStyle = "#444"; ctx.beginPath(); ctx.moveTo(0, 20); ctx.lineTo(width, 20); ctx.moveTo(0, height - 4); ctx.lineTo(width, height - 4); ctx.stroke();
    ctx.fillStyle = "#aaa"; ctx.font = "11px Segoe UI"; ctx.fillText(`${Math.round(max)} мс`, 0, 12);
    ctx.strokeStyle = state.dayZ?.settings?.Performance?.TextColor || "#00e2ed"; ctx.lineWidth = 1.5; ctx.beginPath(); let connected = false;
    values.forEach((value, i) => { if (!Number.isFinite(value)) { connected = false; return; } const x = i * width / Math.max(1, values.length - 1), y = height - 4 - value / max * (height - 24); if (connected) ctx.lineTo(x, y); else ctx.moveTo(x, y); connected = true; }); ctx.stroke();
  }
}

const sessionMaps = {
  cherno: "Черноруссия",
  deerisle: "Deer Isle",
  deadfall: "Deadfall",
  nasdara: "Nasdara"
};
let sessionMapObserver;
let lastMapPlacement = "";

function syncSessionMapPlacement() {
  const map = document.querySelector("[data-session-map]");
  const grid = map?.parentElement;
  const viewport = grid?.getBoundingClientRect();
  const bounds = map?.getBoundingClientRect();
  const left = bounds ? Math.ceil(Math.max(bounds.left, viewport.left) + 1) : 0;
  const top = bounds ? Math.ceil(Math.max(bounds.top, viewport.top) + 1) : 0;
  const right = bounds ? Math.floor(Math.min(bounds.right, viewport.left + grid.clientWidth) - 1) : 0;
  const bottom = bounds ? Math.floor(Math.min(bounds.bottom, viewport.top + grid.clientHeight) - 1) : 0;
  const scale = window.devicePixelRatio;
  const placement = { map: map?.dataset.map || "", left: Math.round(left * scale), top: Math.round(top * scale),
    width: Math.round(Math.max(0, right - left) * scale), height: Math.round(Math.max(0, bottom - top) * scale) };
  const signature = JSON.stringify(placement);
  if (signature === lastMapPlacement) return;
  lastMapPlacement = signature;
  post({ type: "sessionMap", ...placement });
}

function renderOverview() {
  return `<h1>Игровая сессия</h1>
    <div class="session-bar">
      <span>${icon("server")}<b data-session-server>Нет подключения к серверу</b></span>
      <span>${icon("map")}<b data-session-world>—</b></span>
    </div>
    <div class="session-grid">
      <div class="session-map" data-session-map aria-label="Интерактивная карта" role="region">
        <p data-session-map-status>Загрузка карты…</p>
        <button class="action" data-command="openSessionMap">Открыть карту в браузере ↗</button>
      </div>
      <div class="session-side">
        <div class="session-card">
          <h2>Позиция игрока</h2>
          <div class="session-coordinates">
            <div class="session-coordinate" title="Последняя распознанная координата X">X<b data-session-x>—</b></div>
            <div class="session-coordinate" title="Последняя распознанная координата Z">Z<b data-session-z>—</b></div>
          </div>
          <button class="action session-visibility" data-tab="player-position" title="Настроить видимость позиции">${icon("eye")}<span data-session-visibility>Только мне</span>${icon("chevronDown")}</button>
        </div>
        <div class="session-card session-markers">
          <h2>Метки</h2>
          <div class="session-marker-list" data-session-markers></div>
          ${renderSessionMarkerActions("session-marker-actions")}
        </div>
      </div>
    </div>
    <footer class="session-footer">
      <button class="action" data-tab="performance">FPS <b data-performance-value="Fps">—</b></button>
      <button class="action" data-tab="performance">Пинг <b data-performance-value="Ping">—</b></button>
    </footer>`;
}

function renderSessionMarkerActions(className = "") {
  return `<div class="actions ${className}">
    <button class="action" data-command="exportSessionMarkers" title="Сохранить метки игры в JSON">Выгрузить из игры</button>
    <button class="action primary" data-command="importSessionMarkers" title="Загрузить метки из JSON">Загрузить в игру</button>
    <div class="limit session-marker-import-hint" data-session-marker-import-hint hidden></div>
  </div>`;
}

function refreshOverview() {
  refreshSessionMarkers();
  const mapBody = document.querySelector("[data-session-map]");
  if (!mapBody) return;
  const performance = state.performance || {};
  const settings = state.playerPosition?.settings || {};
  const server = performance.Server || "";
  const mapKey = (performance.MapName || "").toLowerCase().replace(/[^a-z0-9]/g, "");
  const activeMap = ["chernarus", "chernarusplus"].includes(mapKey) ? "cherno" : mapKey;
  const mapId = server ? activeMap : "cherno";
  const map = Object.hasOwn(sessionMaps, mapId) ? sessionMaps[mapId] : null;
  const position = Number.isInteger(settings.LastX) && Number.isInteger(settings.LastZ)
    ? { Name: "Последняя позиция игрока", X: settings.LastX, Z: settings.LastZ }
    : null;
  const setText = (key, value) => {
    const element = document.querySelector(`[data-session-${key}]`);
    if (element.textContent !== String(value)) element.textContent = value;
    return element;
  };
  const serverLabel = performance.ServerName || server || "Нет подключения к серверу";
  setText("server", serverLabel).title = serverLabel;
  setText("world", server ? map || performance.MapName || "Карта определяется" : sessionMaps.cherno).title =
    server ? performance.MapName || "Карта определяется" : "Обзорная карта";
  setText("x", position?.X ?? "—");
  setText("z", position?.Z ?? "—");
  setText("visibility", settings.Visibility === "public" ? "Всем на сайте" : settings.Visibility === "profiles" ? "Выбранные профили" : "Только мне");
  const page = map ? mapId : "";
  if (mapBody.dataset.map !== page) {
    mapBody.dataset.map = page;
    setText("map-status", page ? "Загрузка карты…" : performance.MapName ? "Эта карта недоступна для встраивания." : "Определяем карту сервера…");
    syncSessionMapPlacement();
  }
}

function refreshSessionMarkers() {
  const list = document.querySelector("[data-session-markers]");
  if (!list) return;
  const server = state.performance?.Server || "";
  const status = state.dayZ?.status || {};
  const markers = server && server === status.MarkerServer ? status.SessionMarkers || [] : [];
  const markerMessage = !server ? "Список меток сервера появится после подключения." :
    !status.PrivateMarkersPath ? "Укажите файл меток в настройках." :
    status.MarkerServer !== server ? "Чтение меток…" : "У текущего сервера пока нет меток.";
  const listSignature = JSON.stringify([markers, markerMessage]);
  if (list.dataset.content !== listSignature) {
    closeSessionMarkerMenu();
    list.innerHTML = markers.length ? markers.map(({ Name, X, Z }, index) =>
      `<div class="session-marker">${icon("marker")}<span title="${escapeHtml(Name)}">${escapeHtml(Name)}</span><span><em>X</em> ${Math.round(X)}</span><span><em>Z</em> ${Math.round(Z)}</span><button type="button" class="session-marker-menu" aria-label="Действия с меткой ${escapeHtml(Name)}" aria-haspopup="menu" aria-expanded="false" aria-controls="session-marker-popover" data-session-marker-menu="${index}">…</button></div>`).join("") :
      `<p class="session-empty">${markerMessage}</p>`;
    list.dataset.content = listSignature;
  }
  const importButton = document.querySelector('[data-command="importSessionMarkers"]');
  const reason = state.markerImportBlockReason || (!status.FileWritable ? "Выберите доступный для записи файл меток." : "");
  importButton.disabled = Boolean(reason);
  const hint = document.querySelector("[data-session-marker-import-hint]");
  hint.textContent = reason;
  hint.hidden = !reason;
}

function closeSessionMarkerMenu(restoreFocus = false) {
  if (!sessionMarkerSelection) return;
  const { trigger } = sessionMarkerSelection;
  sessionMarkerSelection = null;
  trigger.setAttribute("aria-expanded", "false");
  sessionMarkerPopover.hidePopover();
  if (restoreFocus) trigger.focus({ preventScroll: true });
}

function toggleSessionMarkerMenu(trigger) {
  if (sessionMarkerSelection?.trigger === trigger) {
    closeSessionMarkerMenu(true);
    return;
  }
  closeSessionMarkerMenu();
  const marker = state.dayZ?.status?.SessionMarkers?.[Number(trigger.dataset.sessionMarkerMenu)];
  if (!marker) return;
  sessionMarkerSelection = { trigger, marker };
  trigger.setAttribute("aria-expanded", "true");
  sessionMarkerPopover.showPopover();
  const bounds = trigger.getBoundingClientRect();
  const height = sessionMarkerPopover.offsetHeight;
  const below = bounds.bottom + 4;
  const top = below + height <= window.innerHeight - 4 ? below : bounds.top - height - 4;
  sessionMarkerPopover.style.left = `${Math.max(4, bounds.right - sessionMarkerPopover.offsetWidth)}px`;
  sessionMarkerPopover.style.top = `${Math.max(top, document.getElementById("editor").getBoundingClientRect().top + 4)}px`;
  sessionMarkerPopover.firstElementChild.focus({ preventScroll: true });
}

function renderPlayerPosition() {
  const data = state.playerPosition || {};
  const s = data.settings || {};
  const destination = data.destination;
  const diagnostics = data.diagnostics;
  const profiles = (destination?.Profiles || []).filter(item => item.MapId === s.MapId);
  const selectedProfiles = new Set(s.ProfileIds || []);
  const selectedShape = playerPositionShapes.find(item => item.id === s.IndicatorShape) || playerPositionShapes[0];
  const shapeOptions = playerPositionShapes.map(item => `<option value="${item.id}" ${item.id === selectedShape.id ? "selected" : ""}>${escapeHtml(item.name)}</option>`).join("");
  const status = !s.Enabled ? "Выключено" : s.Paused ? "На паузе" : "Включено";
  return `<h2>Позиция игрока</h2>
    <div class="field">
      <div class="compact-row"><h3>Отслеживание</h3><span class="compact-value">${status}</span></div>
      <label class="compact-row">Отслеживать позицию <input type="checkbox" data-position-enabled ${s.Enabled ? "checked" : ""}></label>
      ${s.Enabled || s.Paused ? `<label class="compact-row">Пауза <input type="checkbox" data-position-paused ${s.Paused ? "checked" : ""}></label>` : ""}
      <div class="compact-row"><span>Последняя позиция</span><span class="compact-value" data-player-position-last>—</span></div>
    </div>
    <div class="field"><label class="compact-row">Кому показывать позицию <select data-position-visibility>
      <option value="private" ${!["public", "profiles"].includes(s.Visibility) ? "selected" : ""}>Только для меня</option>
      <option value="public" ${s.Visibility === "public" ? "selected" : ""}>Всем на сайте</option>
      <option value="profiles" ${s.Visibility === "profiles" ? "selected" : ""}>Выбранные профили</option>
    </select></label>
      <div class="limit">${s.Visibility === "public" ? "Позицию увидят все посетители карты. Для передачи войдите в аккаунт." : s.Visibility === "profiles" ? "Позицию увидят участники выбранных серверных профилей." : "Позиция видна только вам в браузере."}</div>
      ${s.Visibility === "profiles" ? `<div class="position-profile-choices">${profiles.map(item => `<label><input type="checkbox" data-position-profile value="${escapeHtml(item.Id)}" ${selectedProfiles.has(item.Id) ? "checked" : ""}><span>${escapeHtml(item.Name)}</span></label>`).join("") || `<div class="limit">Нет доступных профилей для этой карты. Войдите на DayZ-Map, чтобы выбрать профиль.</div>`}</div>` : ""}
    </div>
    <div class="field">
      <div class="compact-row"><h3>Область координат</h3><span class="limit">${s.HasRegion ? "Настроена" : "Не выбрана"}</span></div>
      <div class="actions"><button class="action primary" data-command="selectPlayerPositionRegion">Настроить область координат</button><button class="action" data-command="showPlayerPositionHint">Подсказка</button></div>
      ${state.ocr?.Ready === false ? `<div class="compact-row"><span class="limit">Для распознавания координат нужен OCR.</span><button class="action" data-command="installOcr">Установить OCR</button></div>` : ""}
      ${destination ? "" : `<div class="limit">Откройте DayZ-Map, чтобы подключить карту.</div>`}
      <div class="limit" data-player-position-error hidden></div>
    </div>
    ${settingsDetails("position-display", "Отображение и обновление", `
    <div class="field" data-position-centering-block>
      <label class="compact-row">Центрировать карту на позиции игрока <input type="checkbox" data-position-center ${s.CenterOnPlayer ? "checked" : ""}></label>
      <label class="compact-row">Сохранять текущий зум <input type="checkbox" data-position-keep-zoom ${s.KeepZoom !== false ? "checked" : ""} ${s.CenterOnPlayer ? "" : "disabled"}></label>
      <label class="compact-row">Зум центрирования <input class="number" data-position-zoom type="number" min="2" max="12" step="1" value="${s.CenteringZoom ?? 8}" ${s.CenterOnPlayer && s.KeepZoom === false ? "" : "disabled"}></label>
    </div>
    <div class="field" data-position-marker-block>
      <label class="compact-row">Имя маркера <input data-position-name type="text" value="${escapeHtml(s.MarkerName || "")}" placeholder="Подпись маркера"></label>
      <label class="compact-row">Цвет индикатора <input class="treasure-color-picker" data-position-color type="color" value="${escapeHtml(s.MarkerColor || "#3498db")}"></label>
      <label class="compact-row">Фигура индикатора <select data-position-shape>${shapeOptions}</select></label>
      <label class="compact-row">Размер маркера, пикселей <input class="number" data-position-size type="number" min="16" max="48" step="1" value="${s.MarkerSize ?? 32}"></label>
    </div>
    <div class="field"><label class="compact-row">Интервал обновления, секунд <input class="number" data-position-interval type="number" min="2" max="300" value="${s.IntervalSeconds || 5}"></label></div>`)}
    ${settingsDetails("position-diagnostics", "Диагностика", `
      ${renderOcrStatus()}
      <div class="field">
        <div class="compact-row"><span>Последняя отправка</span><span class="compact-value" data-player-position-sent>—</span></div>
        <div class="compact-row"><span>Ошибок подряд</span><span class="compact-value" data-player-position-errors>0</span></div>
        <div class="limit" data-player-position-ocr-result hidden></div>
        <div class="actions"><button class="action" data-command="testPlayerPosition">Тест распознавания</button><button class="action" data-command="openPlayerPositionDiagnostics" ${diagnostics ? "" : "disabled"}>Открыть диагностику OCR</button></div>
      </div>`)}`;
}

function renderTreasures() {
  const items = state.treasures?.captures || [];
  const readyItems = items.filter(item => Number.isInteger(item.X) && Number.isInteger(item.Z));
  const duplicateKeys = new Set(readyItems.map(item => `${item.X}:${item.Z}`).filter((key, _, all) => all.filter(value => value === key).length > 1));
  const errors = items.filter(item => item.Status === "Ambiguous");
  const selected = items.find(item => item.Id === selectedTreasureId) || items[0];
  if ((selected?.Id ?? null) !== selectedTreasureId) {
    selectedTreasureId = selected?.Id ?? null;
    treasureCaptureExpanded = true;
  }
  const captureStatus = {
    Queued: "В очереди", Recognizing: "Распознаётся", NeedsReview: "Нужно проверить",
    Recognized: "Распознано", Ambiguous: "Ошибка OCR"
  };
  const captures = items.map(item => `<div class="treasure-entry">
    <button type="button" class="action treasure-list-item"
      data-treasure-select="${escapeHtml(item.Id)}" ${item === selected ? 'aria-controls="treasure-capture-detail"' : ""}>
      <span>${escapeHtml(new Date(item.CapturedAt).toLocaleString())}</span>
      <span class="limit">${item.SentAt ? "Отправлено" : escapeHtml(captureStatus[item.Status] || "—")}</span>
      ${Number.isInteger(item.X) && Number.isInteger(item.Z) ? `<span class="compact-value">X ${item.X} · Z ${item.Z}</span>` : ""}
    </button>
    ${item === selected ? '<div class="treasure-inline"></div>' : ""}
  </div>`).join("");
  const queue = state.treasures?.processing ? `<div class="limit">⏳ Распознаю координаты. В очереди: ${state.treasures.queued}.</div>` : "";
  const destination = state.treasures?.destination;
  const maps = destination?.Maps || [];
  if (!treasureMapId || !maps.some(item => item.Id === treasureMapId)) treasureMapId = maps[0]?.Id || null;
  const profiles = (destination?.Profiles || []).filter(item => item.MapId === treasureMapId && item.Writable);
  if (!treasureProfileId || !profiles.some(item => item.Id === treasureProfileId)) treasureProfileId = profiles[0]?.Id || null;
  const selectedProfile = profiles.find(item => item.Id === treasureProfileId);
  const templateKey = `${treasureMapId || ""}:${treasureProfileId || ""}`;
  const profileTemplate = {
    name: selectedProfile?.MarkerName || "",
    type: selectedProfile?.MarkerType || "default",
    color: /^#[0-9a-f]{6}$/i.test(selectedProfile?.MarkerColor || "") ? selectedProfile.MarkerColor : "#3498db"
  };
  let storedTemplate = null;
  try { storedTemplate = JSON.parse(localStorage.getItem(`dayzCompanionTreasureTemplate:${templateKey}`) || "null"); } catch (_) { }
  const selectedTemplate = treasureTemplateDrafts.get(templateKey) || storedTemplate || profileTemplate;
  const selectedType = selectedTemplate.type;
  const selectedColor = selectedTemplate.color;
  const selectedMarkerType = treasureMarkerTypes.find(item => item.id === selectedType) || treasureMarkerTypes[0];
  const typeOptions = treasureMarkerTypes.map(item => `<button type="button" class="treasure-type-option ${item.id === selectedMarkerType.id ? "active" : ""}" data-treasure-type-option="${item.id}">${treasureMarkerIcon(item.id, item.color)}<span>${escapeHtml(item.name)}</span></button>`).join("");
  const choicePicker = (kind, items, value, isOpen, opensUp) => {
    const selected = items.find(item => item.Id === value);
    return `<div class="treasure-choice-picker ${opensUp ? "open-up" : ""}"><button type="button" class="treasure-choice-trigger" data-treasure-choice-toggle="${kind}"><span>${escapeHtml(selected?.Name || "Не выбрано")}</span></button><div class="treasure-choice-menu" ${isOpen ? "" : "hidden"}>${items.map(item => `<button type="button" class="treasure-choice-option ${item.Id === value ? "active" : ""}" data-treasure-choice-option data-treasure-choice-kind="${kind}" data-treasure-choice-value="${escapeHtml(item.Id)}">${escapeHtml(item.Name)}</button>`).join("")}</div></div>`;
  };
  const mapPicker = choicePicker("map", maps, treasureMapId, treasureMapMenuOpen, treasureMapMenuUp);
  const profilePicker = choicePicker("profile", profiles, treasureProfileId, treasureProfileMenuOpen, treasureProfileMenuUp);
  const destinationUi = !destination ? `<div class="field"><div class="field-label">Карта</div><div class="limit">Откройте DayZ-Map в браузере: Companion ожидает подключение карты.</div></div>` : `<div class="field"><div class="field-label">Отправка на карту</div><div class="treasure-destination-grid"><label>Карта${mapPicker}</label><label>Профиль${profilePicker}</label></div><div class="treasure-template-label">Настройки метки<div class="treasure-template-row"><input type="text" data-treasure-template-name value="${escapeHtml(selectedTemplate.name)}" placeholder="Название метки" maxlength="300"><div class="treasure-type-picker ${treasureTypeMenuUp ? "open-up" : ""}"><input type="hidden" data-treasure-template-type value="${selectedMarkerType.id}"><button type="button" class="treasure-type-trigger" data-treasure-type-toggle>${treasureMarkerIcon(selectedMarkerType.id, selectedMarkerType.color)}<span>${escapeHtml(selectedMarkerType.name)}</span></button><div class="treasure-type-menu" ${treasureTypeMenuOpen ? "" : "hidden"}>${typeOptions}</div></div><input class="treasure-color-picker" data-treasure-template-color type="color" value="${selectedColor}" title="Цвет метки"></div></div><div><button class="action primary" data-treasure-send ${!treasureProfileId ? "disabled" : ""}>Отправить координаты</button></div><div class="limit">Шаблон действует только для этой отправки.</div></div>`;
  const feedback = state.treasures?.feedback ? `<div class="limit">${escapeHtml(state.treasures.feedback)}</div>` : "";
  const preview = treasurePreviewUri ? `<div class="treasure-image-modal" data-treasure-preview-close><img src="${escapeHtml(treasurePreviewUri)}" alt="Скриншот уведомления"></div>` : "";
  const controls = `<div class="compact-stack">
    <div class="field">
      <div class="actions"><button class="action primary" data-command="captureTreasure" ${state.treasures?.selecting ? "disabled" : ""}>Выделить область уведомления</button></div>
      ${queue}${feedback}
    </div>
    <div class="field"><h3>Захваты</h3>
      ${captures ? `<div class="treasure-list">${captures}</div>` : '<div class="limit">Пока нет захватов.</div>'}
    </div>
    ${destinationUi}
    ${settingsDetails("treasure-diagnostics", "Горячая клавиша, OCR и файлы", `
      ${renderOcrStatus()}
      <div class="field"><div class="compact-row"><span>Горячая клавиша захвата</span><button class="action" data-hotkey="CaptureTreasure">${displayHotkey(state.config.Hotkeys.CaptureTreasure)}</button></div>${hotkeyRegistrationWarning("CaptureTreasure")}</div>
      <div class="actions">
        <button class="action" data-command="openTreasureCapturesFolder">Открыть папку PNG</button>
        <button class="action danger" data-treasure="clear" ${items.length ? "" : "disabled"}>Очистить все</button>
        ${errors.length ? `<button class="action" data-treasure="clearErrors">Удалить ошибки OCR (${errors.length})</button>` : ""}
      </div>`)}
  </div>`;
  if (!selected) return `<h2>Клады</h2>${controls}`;

  const draft = treasureCoordinateTimers.get(selected.Id);
  const duplicate = duplicateKeys.has(`${selected.X}:${selected.Z}`);
  const result = selected.SentAt || selected.DeliveryResult
    ? `<div class="limit ${selected.SentAt ? "" : "hotkey-warning"}">${escapeHtml(selected.DeliveryResult || "Отправлено")}</div>`
    : duplicate ? '<div class="limit hotkey-warning">В очереди есть захват с такими же координатами.</div>' : "";
  const id = escapeHtml(selected.Id);
  return `<h2>Клады</h2>
    <div class="settings-workspace treasure-workspace">${controls}
      <div class="settings-side treasure-capture-side">
        <section class="field" id="treasure-capture-detail"><h3>Выбранный захват</h3>
          <div class="compact-row treasure-coordinates">
            <label class="compact-row">X <input type="number" step="1" data-treasure-x="${id}" value="${draft?.x ?? selected.X ?? ""}"></label>
            <label class="compact-row">Z <input type="number" step="1" data-treasure-z="${id}" value="${draft?.z ?? selected.Z ?? ""}"></label>
          </div>
          ${selected.preview ? `<button class="treasure-preview-button" data-treasure-preview aria-label="Увеличить скриншот уведомления"><img class="treasure-preview" src="${escapeHtml(selected.preview)}" alt="Скриншот уведомления"></button>` : ""}
          ${result}
          <div class="actions">
            ${selected.Status === "Ambiguous" ? `<button class="action" data-treasure="retry" data-id="${id}">Повторить OCR</button>` : ""}
            <button class="action danger" data-treasure="delete" data-id="${id}">Удалить захват</button>
          </div>
          ${selected.RawText ? settingsDetails("treasure-raw-text", "Распознанный текст", `<div class="limit">${escapeHtml(selected.RawText)}</div>`) : ""}
        </section>
      </div>
    </div>${preview}`;
}

function syncTreasureCaptureLayout() {
  const workspace = document.querySelector(".treasure-workspace");
  if (!workspace) return;
  const wide = getComputedStyle(workspace).getPropertyValue("--treasure-side").trim() === "1";
  const panel = document.getElementById("treasure-capture-detail");
  const host = workspace.querySelector(wide ? ".treasure-capture-side" : ".treasure-inline");
  panel.hidden = !wide && !treasureCaptureExpanded;
  if (panel.parentElement !== host) {
    const focused = panel.contains(document.activeElement) ? document.activeElement : null;
    host.append(panel);
    if (!panel.hidden) focused?.focus({ preventScroll: true });
  }
  workspace.querySelectorAll("[data-treasure-select]").forEach(button => {
    const selected = button.dataset.treasureSelect === selectedTreasureId;
    button.removeAttribute(wide ? "aria-expanded" : "aria-pressed");
    button.setAttribute(wide ? "aria-pressed" : "aria-expanded", selected && (wide || treasureCaptureExpanded));
    button.classList.toggle("active", selected && (wide || treasureCaptureExpanded));
  });
}

function renderHotkeys() {
  const keys = [
    ["ToggleOverlay", "Показать или скрыть"],
    ["PreviousProfile", "Предыдущий профиль"],
    ["NextProfile", "Следующий профиль"],
    ["OpacityUp", "Прозрачность больше"],
    ["OpacityDown", "Прозрачность меньше"],
    ["SizeUp", "Размер больше"],
    ["SizeDown", "Размер меньше"]
  ];
  return renderHotkeyRows("Горячие клавиши", keys);
}

function renderHotkeyRows(title, keys) {
  return `<h2>${title}</h2>` + keys.map(([key, label]) =>
    `<div class="field"><div class="compact-row"><span>${label}</span><div class="actions">
      <button class="action" data-hotkey="${key}">${displayHotkey(state.config.Hotkeys[key])}</button>
      <button class="action" data-hotkey-clear="${key}">Очистить</button>
    </div></div>${hotkeyRegistrationWarning(key)}</div>`).join("");
}

function renderMonitor() {
  const value = state.config.TargetMonitorDeviceName || "";
  const options = (state.monitors || []).map(m => `<option value="${escapeHtml(m.DeviceName)}" ${m.DeviceName === value ? "selected" : ""}>${escapeHtml(m.DisplayName)}</option>`).join("");
  return `
    <h2>Монитор</h2>
    ${settingRow("Целевой монитор", `<select id="monitorSelect">${options}</select>`)}
    <div class="actions">
      <button class="action" data-command="refreshMonitors">Обновить мониторы</button>
    </div>
  `;
}

const manualTaskDrafts = new Map();

function renderManualTaskRow(task, isNew = false) {
  const key = isNew ? "new" : task.Id;
  const draft = manualTaskDrafts.get(key) || task;
  return `<div class="field" data-manual-row="${escapeHtml(key)}">
    <input type="text" data-manual-field="Title" value="${escapeHtml(draft.Title || "")}" placeholder="Название личной задачи" aria-label="Название личной задачи">
    <div class="manual-task-progress">
      <label class="compact-row">Текущее <input type="number" min="0" step="1" data-manual-field="Current" value="${draft.Current ?? 0}"></label>
      <label class="compact-row">Цель <input type="number" min="1" step="1" data-manual-field="Target" value="${draft.Target || ""}"></label>
    </div>
    <div class="actions">
      <button class="action ${isNew ? "primary" : ""}" data-manual-save>${isNew ? "Добавить задачу" : "Сохранить"}</button>
      ${isNew ? "" : `<button class="action danger" data-manual-delete>Удалить</button>`}
    </div>
  </div>`;
}

function renderBattlePass() {
  const bp = state.battlePass || { settings: {}, snapshot: { Tasks: [] } };
  const s = bp.settings;
  const ocr = bp.ocr ?? state.ocr;
  const tasks = bp.snapshot?.Tasks || [];
  const updated = bp.snapshot?.UpdatedAt ? escapeHtml(new Date(bp.snapshot.UpdatedAt).toLocaleString()) : "";
  const monitors = [{ DeviceName: "", DisplayName: "Основной монитор" }, ...(state.monitors || [])];
  const monitorOptions = monitors.map(m => `<option value="${escapeHtml(m.DeviceName)}" ${m.DeviceName === (s.MonitorDeviceName || "") ? "selected" : ""}>${escapeHtml(m.DisplayName)}</option>`).join("");
  const taskCheck = (label, key) => settingRow(label, `<input type="checkbox" data-bp-check="${key}" ${s[key] ? "checked" : ""}>`);
  const taskNumber = (label, key, min, max, fallback) => settingRow(label, `<input type="number" min="${min}" max="${max}" value="${s[key] ?? fallback}" data-bp-number="${key}">`);
  const taskResults = tasks.map(task => `<li class="compact-row"><span>${escapeHtml(task.Title)}</span><span class="compact-value">${escapeHtml(task.Completed ? "Готово" : task.ProgressText || "—")}</span></li>`).join("");
  return `<h2>Отслеживание заданий</h2>
    <div class="settings-workspace">
      <div class="compact-stack">
        <div class="field"><h3>Сканирование Battle Pass</h3>
          <div class="limit">Откройте нужную страницу заданий в DayZ.</div>
          <div class="actions task-settings-actions">
            <button class="action primary" data-command="scanBattlePassDaily">Ежедневные</button>
            <button class="action" data-command="scanBattlePassSeasonal">Сезонные</button>
            <button class="action" data-command="scanBattlePassWeeklyPage1">Еженедельные · стр. 1</button>
            <button class="action" data-command="scanBattlePassWeeklyPage2">Еженедельные · стр. 2</button>
          </div>
          ${updated ? `<div class="limit">Обновлено ${updated}</div>` : ""}
          ${ocr?.Ready === false ? `<div class="compact-row"><span class="limit">Для сканирования нужен OCR.</span><button class="action" data-command="installOcr">Установить OCR</button></div>` : ""}
        </div>
        <div class="field"><h3>Распознанные задания</h3>${taskResults ? `<ul class="task-result-list">${taskResults}</ul>` : '<div class="limit">Пока нет распознанных заданий.</div>'}</div>
        ${settingsDetails("tasks-overlay", "Оверлей заданий", `
          ${taskCheck("Показывать оверлей", "OverlayVisible")}
          ${taskCheck("Показывать только в DayZ", "ShowOnlyInGame")}
          ${taskCheck("Показывать выполненные", "ShowCompleted")}
          ${taskCheck("Показывать сезонные", "ShowSeasonal")}
          ${taskCheck("Показывать описания", "ShowTaskDescriptions")}
          ${taskCheck("Перемещение и изменение размера", "OverlayEditingEnabled")}
          ${settingRow("Монитор", `<select data-bp-select="MonitorDeviceName">${monitorOptions}</select>`)}
          ${taskNumber("Ширина, px", "Width", 220, 900, 360)}
          ${taskNumber("Высота, px", "Height", 92, 850, 470)}
          ${taskNumber("Размер шрифта", "FontSize", 9, 28, 14)}
          ${taskNumber("Непрозрачность", "Opacity", 40, 255, 230)}
          <div class="actions"><button class="action" data-command="resetBattlePassOverlayBounds">Сбросить положение и размер оверлея</button></div>`)}
        ${settingsDetails("tasks-diagnostics", "Зоны захвата и диагностика", `
          ${renderOcrStatus(ocr)}
          ${taskCheck("Сохранять отладочный снимок", "SaveDebugScreenshot")}
          <div class="actions">
            <button class="action" data-command="calibrateBattlePassZones">Настроить зоны на экране</button>
            <button class="action" data-command="showBattlePassDebug">Открыть отладочный снимок</button>
            <button class="action danger" data-command="clearBattlePass">Очистить данные</button>
          </div>`)}
      </div>
      <section class="field settings-side"><h3>Личные задачи</h3>
        ${(bp.manualTasks || []).map(task => renderManualTaskRow(task)).join("") || '<div class="limit">Личных задач пока нет.</div>'}
        ${renderManualTaskRow({ Title: "", Current: 0, Target: "" }, true)}
      </section>
    </div>`;
}

function renderBattlePassHotkeys() {
  const keys = [
    ["ToggleBattlePassOverlay", "Показать или скрыть оверлей"],
    ["ScanBattlePass", "Считать экран"],
    ["ToggleBattlePassDescriptions", "Свернуть/развернуть описания"]
  ];
  return renderHotkeyRows("Горячие клавиши заданий", keys);
}

function renderProfiles() {
  const selected = selectedProfile();
  const profileOptions = state.config.Profiles.map(profile => `<option value="${escapeHtml(profile.Id)}" ${profile.Id === state.config.ActiveProfileId ? "selected" : ""}>${escapeHtml(profile.Name)}</option>`).join("");
  const list = state.config.Profiles.map(profile => `
    <button type="button" class="${profile.Id === selected.Id ? "active" : ""}" aria-pressed="${profile.Id === selected.Id}" data-profile-select="${escapeHtml(profile.Id)}">
      ${escapeHtml(profile.Name)}
    </button>
  `).join("");
  return `
    <h2>Профили</h2>
    ${settingRow("Активный профиль", `<select id="profileSelect" class="profile-select">${profileOptions}</select>`)}
    <div class="field"><h3>Профили для редактирования</h3><div class="profile-list">${list}</div></div>
    ${text("Название профиля", "profileName", selected.Name)}
    <div class="actions">
      <button class="action primary" data-profile="add">Добавить</button>
      <button class="action" data-profile="duplicate">Дублировать</button>
      <button class="action danger" data-profile="delete">Удалить</button>
      <button class="action" data-profile="reset">Сбросить</button>
    </div>
  `;
}

function renderDayZ() {
  const settings = state.dayZ.settings;
  const status = state.dayZ.status;
  const path = status.PrivateMarkersPath || settings.PrivateMarkersPath || "Не найден";
  const fileState = status.FileError
    ? `Ошибка: ${escapeHtml(status.FileError)}`
    : status.FileWritable ? "Доступен для чтения и записи" : "Укажите путь к файлу игры";
  const address = status.Port ? `http://127.0.0.1:${status.Port}` : "—";
  const backups = status.Backups || [];
  const backupList = backups.length
    ? backups.slice(0, 5).map(item => `<span>${escapeHtml(new Date(item.LastWriteTime).toLocaleString())} · ${Math.max(1, Math.round(item.Size / 1024))} КБ</span>`).join("")
    : "Резервных копий пока нет.";
  const dayZWarning = status.DayZRunning
    ? (settings.BlockWritesWhenDayZRunning
      ? "DayZ запущен. Запись меток заблокирована настройкой."
      : "DayZ запущен. Загружайте метки на экране выбора персонажа или после закрытия игры.")
    : "DayZ не запущен.";
  const actionDisabled = dayZStatusPending ? "disabled" : "";
  return `<h2>Метки DayZ</h2>
    <div class="settings-workspace">
      <div class="compact-stack">
        <div class="field">
          <div class="compact-row compact-control"><h3>Файл меток</h3><span class="compact-value">${escapeHtml(path)}</span></div>
          <div class="limit">${fileState}</div>
          <div class="limit">${dayZWarning}</div>
          ${dayZActionFeedback ? `<div class="limit" role="status">${escapeHtml(dayZActionFeedback)}</div>` : ""}
          <div class="actions">
            <button class="action primary" data-command="selectDayZMarkersFile">Выбрать файл</button>
            <button class="action" data-command="clearDayZMarkersFile" ${actionDisabled}>Искать автоматически</button>
            <button class="action" data-command="refreshDayZStatus" ${actionDisabled}>Проверить</button>
            <button class="action" data-command="openDayZMarkersFolder">Открыть папку</button>
          </div>
        </div>
        ${settingRow("Запрещать запись при запущенном DayZ", `<input type="checkbox" ${settings.BlockWritesWhenDayZRunning ? "checked" : ""} data-dayz-check="BlockWritesWhenDayZRunning">`)}
        ${settingsDetails("markers-backups", "Резервные копии", `
          ${settingRow("Максимум копий", `<input type="number" min="1" max="100" value="${settings.BackupLimit}" data-dayz-number="BackupLimit">`)}
          ${settingRow("Хранить, дней", `<input type="number" min="1" max="3650" value="${settings.BackupMaxAgeDays}" data-dayz-number="BackupMaxAgeDays">`)}
          <div class="field"><h3>Последние копии</h3><div class="update-status">${backupList}</div></div>`)}
        ${settingsDetails("markers-api", "Подключение и API", `
          <div class="field">
            <div class="compact-row"><span>Статус API</span><span class="compact-value">${escapeHtml(status.ServiceStatus)}</span></div>
            <div class="compact-row"><span>Адрес</span><span class="compact-value">${escapeHtml(address)}</span></div>
            <div class="limit">${escapeHtml(status.LastOperation || "Операций с метками пока не было.")}</div>
          </div>
          ${settingRow("Автоматически выбирать порт", `<input type="checkbox" ${settings.AutoPort ? "checked" : ""} data-dayz-check="AutoPort">`)}
          ${settings.AutoPort ? "" : settingRow("Порт API", `<input type="number" min="1" max="65535" value="${settings.Port}" data-dayz-number="Port">`)}
          ${settingRow("Разрешить localhost:8000", `<input type="checkbox" ${settings.AllowDevelopmentOrigin ? "checked" : ""} data-dayz-check="AllowDevelopmentOrigin">`)}
          <div class="actions"><button class="action" data-command="openRuntimeLog">Открыть журнал</button></div>
          <div class="limit">DayZ-Map.ru Companion не аффилирован и не авторизован Bohemia Interactive a.s. DAYZ является товарным знаком Bohemia Interactive a.s.</div>`)}
      </div>
      <section class="field settings-side"><h3>Метки текущего сервера</h3>
        <div class="session-marker-list" data-session-markers></div>
        ${renderSessionMarkerActions()}
      </section>
    </div>`;
}

function renderUpdates() {
  if (state.microsoftStore) return `<h2>Обновление</h2>
    <div class="field"><h3>Microsoft Store</h3><div class="limit">Обновления доступны в библиотеке магазина.</div>
      <div class="actions"><button class="action primary" data-command="checkUpdate">Открыть Microsoft Store</button></div>
    </div>`;
  const info = state.update;
  const operation = state.updateProgress || {};
  const busy = !!operation.IsBusy;
  const progressHtml = operation.Message ? field("Обновление Companion", { input: `<div class="update-status" role="status" aria-live="polite">${escapeHtml(operation.Message)}</div>${busy ? `<progress style="width:100%;accent-color:var(--accent)" max="100" ${Number.isInteger(operation.Percent) ? `value="${operation.Percent}"` : ""}></progress>` : ""}${operation.CanCancel ? `<div class="actions"><button class="action" data-command="cancelUpdate">Отменить скачивание</button></div>` : ""}` }) : "";
  if (!info) {
    return `<h2>Обновление</h2>
      ${progressHtml}
      <div class="field"><div class="limit">Проверка ещё не выполнялась.</div><div class="actions">
        <button class="action primary" data-command="checkUpdate">Проверить</button>
        <button class="action" data-command="openCompanionReleases">Открыть релизы</button>
      </div></div>`;
  }

  const status = info.ErrorMessage
    ? `Не удалось проверить обновление: ${escapeHtml(info.ErrorMessage)}`
    : info.IsUpdateAvailable
      ? `<strong>Доступна новая версия ${escapeHtml(info.LatestVersion)}</strong>`
      : "Установлена актуальная версия.";
  const published = info.PublishedAt ? new Date(info.PublishedAt).toLocaleDateString() : "";
  const notes = (info.ReleaseNotes || "").trim() || "Описание версии не указано.";
  const downloadDisabled = !info.InstallerUrl && !info.ReleaseUrl ? "disabled" : "";
  return `<h2>Обновление</h2>
    <div class="compact-stack">
      ${progressHtml}
      <div class="field">
        <div class="update-status" role="status">${status}</div>
        <div class="compact-row"><span>Текущая версия</span><span class="compact-value">${escapeHtml(info.CurrentVersion)}</span></div>
        <div class="compact-row"><span>Последняя версия</span><span class="compact-value">${escapeHtml(info.LatestVersion || "—")}${published ? " · " + escapeHtml(published) : ""}</span></div>
      </div>
      <section class="field update-notes-slot"><h3>Описание версии</h3><div class="release-notes">${renderMarkdown(notes)}</div></section>
      <div class="actions">
        <button class="action primary" data-command="installUpdate" ${info.CanInstall && !busy ? "" : "disabled"}>Обновить и перезапустить</button>
        <button class="action" data-command="checkUpdate" ${busy ? "disabled" : ""}>Проверить снова</button>
        <button class="action" data-command="downloadUpdate" ${busy ? "disabled" : downloadDisabled}>Скачать установщик</button>
        <button class="action" data-command="openCompanionReleases">Открыть релизы</button>
      </div>
      ${info.IsUpdateAvailable ? `<div class="limit">${info.CanInstall ? "Windows запросит разрешение на установку." : "Для этой версии доступно обновление через установщик."}</div>` : ""}
    </div>`;
}

function saveTreasureCoordinates(id) {
  const draft = treasureCoordinateTimers.get(id);
  if (!draft) return;
  clearTimeout(draft.timer);
  treasureCoordinateTimers.delete(id);
  post({ type: "treasure", action: "edit", id, x: draft.x, z: draft.z });
}

function bindEditorEvents() {
  document.querySelectorAll("[data-settings-disclosure]").forEach(details => {
    details.addEventListener("toggle", () => {
      if (!details.isConnected) return;
      const key = details.dataset.settingsDisclosure;
      if (details.open) expandedSettings.add(key);
      else expandedSettings.delete(key);
    });
  });
  refreshPlayerPositionStatus();
  refreshOverview();
  sessionMapObserver?.disconnect();
  const map = document.querySelector("[data-session-map]");
  if (map) {
    sessionMapObserver ||= new ResizeObserver(syncSessionMapPlacement);
    sessionMapObserver.observe(map);
    sessionMapObserver.observe(map.parentElement);
    map.parentElement.addEventListener("scroll", syncSessionMapPlacement);
  }
  syncSessionMapPlacement();
  refreshPerformanceStatus();
  document.querySelectorAll("[data-performance-check]").forEach(input => input.addEventListener("change", () => updateDayZ(settings => {
    settings.Performance ||= {};
    settings.Performance[input.dataset.performanceCheck] = input.checked;
    if (input.dataset.performanceCheck === "EditingEnabled" && input.checked) settings.Performance.ShowOverlay = true;
    if (!["ShowFps", "ShowPing", "ShowFrameTime", "ShowOnePercentLow", "ShowJitter", "ShowFrameGraph"].some(key => settings.Performance[key])) settings.Performance.ShowFps = true;
  })));
  document.querySelectorAll("[data-performance-number]").forEach(input => {
    input.addEventListener("input", () => {
      const label = document.querySelector(`[data-performance-opacity-label="${input.dataset.performanceNumber}"]`);
      if (label) label.textContent = input.value;
    });
    input.addEventListener("change", () => {
      if (!input.reportValidity() || input.value.trim() === "") return;
      const value = Number(input.value);
      if (Number.isInteger(value)) updateDayZ(settings => settings.Performance[input.dataset.performanceNumber] = value);
    });
  });
  document.querySelectorAll("[data-performance-color]").forEach(input => input.addEventListener("change", () => updateDayZ(settings => settings.Performance[input.dataset.performanceColor] = input.value)));
  document.querySelectorAll("[data-performance-select]").forEach(input => input.addEventListener("change", () => updateDayZ(settings => {
    const key = input.dataset.performanceSelect;
    settings.Performance[key] = key === "VerticalLayout" ? input.value === "true" : input.value || null;
    if (key === "VerticalLayout" && settings.Performance.VerticalLayout) settings.Performance.Height = Math.max(settings.Performance.Height, 64);
  })));
  document.querySelector("[data-performance-reset]")?.addEventListener("click", () => updateDayZ(settings => {
    const { Enabled, QueryPort, ShowOverlay, ShowOnlyInGame } = settings.Performance;
    settings.Performance = { ...clone(state.performanceDefaults), Enabled, QueryPort, ShowOverlay, ShowOnlyInGame };
  }));
  const portForm = document.querySelector("[data-performance-port-form]");
  if (portForm) {
    const input = portForm.querySelector("[data-performance-port]");
    const mode = document.querySelector("[data-performance-port-mode]");
    mode.addEventListener("change", () => {
      const manual = mode.value === "manual";
      portForm.hidden = !manual;
      queryPortDraft = manual ? input.value : null;
      if (manual) input.focus();
      else if (state.dayZ.settings.Performance.QueryPort) updateDayZ(settings => { settings.Performance.QueryPort = 0; });
      else input.value = "";
    });
    input.addEventListener("input", () => { queryPortDraft = input.value; });
    portForm.addEventListener("submit", event => {
      event.preventDefault();
      if (mode.value !== "manual" || !input.reportValidity()) return;
      const value = Number(input.value);
      if (!Number.isInteger(value) || value < 1 || value > 65535) return;
      queryPortDraft = null;
      updateDayZ(settings => { settings.Performance.QueryPort = value; });
    });
  }
  document.querySelectorAll("[data-slider]").forEach(input => {
    const isImageSlider = input.dataset.slider.startsWith("ImageLayer.");
    input.addEventListener("input", () => {
      const value = Number(input.value);
      const number = input.closest(".slider-row")?.querySelector("[data-number]");
      const label = input.closest(".field")?.querySelector(".value");
      if (number) number.value = value;
      if (label) label.textContent = value;
      const options = isImageSlider
        ? { render: false, previewOnly: true }
        : { render: false };
      updateProfile(p => setPath(p, input.dataset.slider, value), options);
      syncPresetButtons();
    });
    input.addEventListener("change", () => {
      if (isImageSlider) {
        postConfig(state.config);
      } else {
        flushPendingConfig();
      }
    });
  });
  document.querySelectorAll("[data-number]").forEach(input => {
    input.addEventListener("change", () => {
      const min = Number(input.min);
      const max = Number(input.max);
      const value = Math.min(max, Math.max(min, Number(input.value || min)));
      input.value = value;
      const range = input.closest(".slider-row")?.querySelector("[data-slider]");
      const label = input.closest(".field")?.querySelector(".value");
      if (range) range.value = value;
      if (label) label.textContent = value;
      updateProfile(p => setPath(p, input.dataset.number, value), { render: false });
      syncPresetButtons();
    });
  });
  document.querySelectorAll("[data-check]").forEach(input => {
    input.addEventListener("change", () => updateProfile(p => setPath(p, input.dataset.check, input.checked)));
  });
  document.querySelectorAll("[data-general-check]").forEach(input => {
    input.addEventListener("change", () => update(config => config[input.dataset.generalCheck] = input.checked));
  });
  document.querySelectorAll("[data-dayz-check]").forEach(input => {
    input.addEventListener("change", () => updateDayZ(settings => settings[input.dataset.dayzCheck] = input.checked));
  });
  document.querySelectorAll("[data-dayz-number]").forEach(input => {
    input.addEventListener("change", () => {
      const value = Math.min(Number(input.max), Math.max(Number(input.min), Number(input.value || input.min)));
      updateDayZ(settings => settings[input.dataset.dayzNumber] = value);
    });
  });
  document.querySelectorAll("[data-bp-check]").forEach(input => input.addEventListener("change", () => updateBattlePass(settings => settings[input.dataset.bpCheck] = input.checked)));
  document.querySelectorAll("[data-manual-row]").forEach(row => {
    const key = row.dataset.manualRow;
    const read = () => ({
      Title: row.querySelector('[data-manual-field="Title"]').value,
      Current: row.querySelector('[data-manual-field="Current"]').value,
      Target: row.querySelector('[data-manual-field="Target"]').value
    });
    row.querySelectorAll("input").forEach(input => input.addEventListener("input", () => manualTaskDrafts.set(key, read())));
    row.querySelector("[data-manual-save]").addEventListener("click", () => {
      const draft = read();
      const current = Number(draft.Current), target = Number(draft.Target);
      if (!draft.Title.trim() || !draft.Current.trim() || !Number.isInteger(current) || current < 0 || !Number.isInteger(target) || target <= 0 || current > 2147483647 || target > 2147483647) {
        alert("Укажите название и корректный прогресс (текущее значение и цель).");
        return;
      }
      manualTaskDrafts.delete(key);
      post({ type: "manualTask", action: "save", task: { ...(key === "new" ? {} : { Id: key }), Title: draft.Title.trim(), Current: current, Target: target } });
    });
    row.querySelector("[data-manual-delete]")?.addEventListener("click", () => {
      manualTaskDrafts.delete(key);
      post({ type: "manualTask", action: "delete", task: { Id: key } });
    });
  });
  document.querySelectorAll("[data-bp-select]").forEach(input => input.addEventListener("change", () => updateBattlePass(settings => settings[input.dataset.bpSelect] = input.value || null)));
  document.querySelectorAll("[data-bp-number]").forEach(input => input.addEventListener("change", () => updateBattlePass(settings => settings[input.dataset.bpNumber] = Number(input.value))));
  document.querySelectorAll("[data-bp-percent]").forEach(input => input.addEventListener("change", () => updateBattlePass(settings => settings[input.dataset.bpPercent] = Number(input.value) / 100)));
  document.querySelectorAll("[data-overlay-size]").forEach(input => {
    input.addEventListener("change", () => update(config => config.OverlayWindowSize = input.value));
  });
  document.querySelectorAll("[data-select]").forEach(input => {
    input.addEventListener("change", () => updateProfile(p => setPath(p, input.dataset.select, Number(input.value))));
  });
  document.querySelectorAll("[data-color]").forEach(input => {
    input.addEventListener("input", () => updateProfile(p => {
      const target = getPath(p, input.dataset.color);
      target.R = parseInt(input.value.slice(1, 3), 16);
      target.G = parseInt(input.value.slice(3, 5), 16);
      target.B = parseInt(input.value.slice(5, 7), 16);
    }, { render: false }));
    input.addEventListener("change", flushPendingConfig);
  });
  document.querySelectorAll("[data-color-all]").forEach(input => {
    input.addEventListener("input", () => {
      document.querySelectorAll("[data-color='Color'], [data-color='DotColor']").forEach(item => {
        item.value = input.value;
      });
      updateProfile(p => {
      const r = parseInt(input.value.slice(1, 3), 16);
      const g = parseInt(input.value.slice(3, 5), 16);
      const b = parseInt(input.value.slice(5, 7), 16);
      p.Color.R = r;
      p.Color.G = g;
      p.Color.B = b;
      p.DotColor.R = r;
      p.DotColor.G = g;
      p.DotColor.B = b;
      }, { render: false });
    });
    input.addEventListener("change", flushPendingConfig);
  });
  document.querySelectorAll("[data-preset]").forEach(button => {
    button.addEventListener("click", () => updateProfile(p => applyPreset(p, button.dataset.preset)));
  });
  document.querySelectorAll("[data-command]").forEach(button => {
    button.addEventListener("click", () => {
      const name = button.dataset.command;
      if (name === "refreshDayZStatus") {
        dayZStatusPending = true;
        dayZActionFeedback = "Проверяю API и PrivateMarkers.json…";
        render();
      } else if (name === "clearDayZMarkersFile") {
        dayZStatusPending = true;
        dayZActionFeedback = "Ищу PrivateMarkers.json по стандартным путям…";
        render();
      }
      post({ type: "command", name });
    });
  });
  document.querySelectorAll("[data-image]").forEach(button => {
    button.addEventListener("click", () => updateProfile(p => mutateImage(p.ImageLayer, button.dataset.image)));
  });
  document.querySelectorAll("[data-hotkey]").forEach(button => {
    button.addEventListener("click", () => {
      hotkeyCapture = button.dataset.hotkey;
      button.textContent = "Нажмите сочетание...";
    });
  });
  const positionSetting = () => ({
    ...(state.playerPosition?.settings || {}),
    Enabled: document.querySelector("[data-position-enabled]")?.checked || false,
    Paused: document.querySelector("[data-position-paused]")?.checked || false,
    AutoSelectMap: true,
    MapId: state.playerPosition?.settings?.MapId || "",
    ProfileId: "",
    Visibility: document.querySelector("[data-position-visibility]")?.value || state.playerPosition?.settings?.Visibility || "private",
    ProfileIds: (document.querySelector("[data-position-visibility]")?.value || state.playerPosition?.settings?.Visibility) === "profiles"
      ? Array.from(document.querySelectorAll("[data-position-profile]")).filter(input => input.checked).map(input => input.value)
      : (state.playerPosition?.settings?.ProfileIds || []),
    MarkerName: document.querySelector("[data-position-name]")?.value || "",
    MarkerType: "player",
    IndicatorShape: document.querySelector("[data-position-shape]")?.value || "triangle",
    MarkerColor: document.querySelector("[data-position-color]")?.value || "#3498db",
    CenterOnPlayer: document.querySelector("[data-position-center]")?.checked || false,
    KeepZoom: document.querySelector("[data-position-keep-zoom]")?.checked ?? true,
    CenteringZoom: Math.max(2, Math.min(12, Math.round(Number(document.querySelector("[data-position-zoom]")?.value || 8)))),
    ShowLabel: true,
    LabelContent: "name",
    MarkerSize: Math.max(16, Math.min(48, Math.round(Number(document.querySelector("[data-position-size]")?.value || 32)))),
    IntervalSeconds: Number(document.querySelector("[data-position-interval]")?.value || 5)
  });
  const savePlayerPositionSettings = (refreshEditor = false) => {
    clearTimeout(playerPositionSettingsTimer);
    const next = positionSetting();
    state.playerPosition = { ...(state.playerPosition || {}), settings: next };
    // Checkbox state is also reflected in the surrounding label. Update the
    // local view immediately instead of waiting for the native state push.
    if (refreshEditor) render();
    post({ type: "updatePlayerPositionSettings", settings: next });
  };
  const debouncePlayerPositionSettings = () => {
    clearTimeout(playerPositionSettingsTimer);
    playerPositionSettingsTimer = setTimeout(savePlayerPositionSettings, 180);
  };
  document.querySelectorAll("[data-position-enabled], [data-position-paused], [data-position-center], [data-position-keep-zoom]").forEach(input => input.addEventListener("change", () => savePlayerPositionSettings(true)));
  document.querySelectorAll("[data-position-interval], [data-position-zoom], [data-position-size]").forEach(input => input.addEventListener("change", () => savePlayerPositionSettings(true)));
  document.querySelector("[data-position-name]")?.addEventListener("input", debouncePlayerPositionSettings);
  document.querySelector("[data-position-color]")?.addEventListener("input", debouncePlayerPositionSettings);
  document.querySelector("[data-position-shape]")?.addEventListener("change", () => savePlayerPositionSettings());
  document.querySelectorAll("[data-position-visibility], [data-position-profile]").forEach(input => input.addEventListener("change", () => savePlayerPositionSettings(true)));
  syncTreasureCaptureLayout();
  document.querySelectorAll("[data-treasure-select]").forEach(button => button.addEventListener("click", () => {
    const id = button.dataset.treasureSelect;
    if (selectedTreasureId === id) {
      if (button.hasAttribute("aria-expanded")) {
        treasureCaptureExpanded = !treasureCaptureExpanded;
        syncTreasureCaptureLayout();
      }
      return;
    }
    selectedTreasureId = id;
    treasureCaptureExpanded = true;
    render();
    const selected = document.querySelector('[aria-controls="treasure-capture-detail"]');
    selected?.focus({ preventScroll: true });
    selected?.scrollIntoView({ block: "nearest" });
  }));
  document.querySelectorAll("[data-treasure]").forEach(button => {
    button.addEventListener("click", () => {
      const id = button.dataset.id;
      const action = button.dataset.treasure;
      const cleared = action === "clear" ? [...treasureCoordinateTimers.keys()]
        : action === "clearErrors" ? state.treasures.captures.filter(item => item.Status === "Ambiguous").map(item => item.Id)
        : [id];
      for (const key of cleared) {
        clearTimeout(treasureCoordinateTimers.get(key)?.timer);
        treasureCoordinateTimers.delete(key);
      }
      post({ type: "treasure", action, id });
    });
  });
  document.querySelectorAll("[data-treasure-x], [data-treasure-z]").forEach(input => input.addEventListener("input", () => {
    const id = input.dataset.treasureX || input.dataset.treasureZ;
    clearTimeout(treasureCoordinateTimers.get(id)?.timer);
    treasureCoordinateTimers.delete(id);
    const x = document.querySelector("[data-treasure-x]").valueAsNumber;
    const z = document.querySelector("[data-treasure-z]").valueAsNumber;
    if (!Number.isInteger(x) || !Number.isInteger(z)) return;
    treasureCoordinateTimers.set(id, { x, z, timer: setTimeout(() => saveTreasureCoordinates(id), 350) });
  }));
  document.querySelector("[data-treasure-preview]")?.addEventListener("click", event => {
    treasurePreviewUri = event.currentTarget.querySelector("img").src;
    render();
  });
  document.querySelector("[data-treasure-preview-close]")?.addEventListener("click", () => { treasurePreviewUri = null; render(); });
  document.querySelectorAll("[data-treasure-choice-toggle]").forEach(button => button.addEventListener("click", () => {
    const kind = button.dataset.treasureChoiceToggle;
    const opens = kind === "map" ? !treasureMapMenuOpen : !treasureProfileMenuOpen;
    const rect = button.getBoundingClientRect();
    const opensUp = window.innerHeight - rect.bottom < 268 && rect.top > 268;
    if (kind === "map") { treasureMapMenuOpen = opens; treasureMapMenuUp = opensUp; }
    else { treasureProfileMenuOpen = opens; treasureProfileMenuUp = opensUp; }
    if (kind === "map") treasureProfileMenuOpen = false;
    else treasureMapMenuOpen = false;
    render();
  }));
  document.querySelectorAll("[data-treasure-choice-option]").forEach(button => button.addEventListener("click", () => {
    if (button.dataset.treasureChoiceKind === "map") {
      treasureMapId = button.dataset.treasureChoiceValue;
      treasureProfileId = null;
    } else {
      treasureProfileId = button.dataset.treasureChoiceValue;
    }
    treasureMapMenuOpen = false;
    treasureProfileMenuOpen = false;
    treasureTypeMenuOpen = false;
    render();
  }));
  document.onpointerdown = event => {
    if (event.target.closest(".treasure-choice-picker, .treasure-type-picker")) return;
    treasureMapMenuOpen = false;
    treasureProfileMenuOpen = false;
    treasureTypeMenuOpen = false;
    document.querySelectorAll(".treasure-choice-menu, .treasure-type-menu").forEach(menu => { menu.hidden = true; });
  };
  document.onkeydown = event => {
    if (event.key === "Escape") {
      treasurePreviewUri = null;
      treasureMapMenuOpen = false;
      treasureProfileMenuOpen = false;
      treasureTypeMenuOpen = false;
      document.querySelectorAll(".treasure-choice-menu, .treasure-type-menu").forEach(menu => { menu.hidden = true; });
      return;
    }
    if (!["ArrowDown", "ArrowUp", "Enter"].includes(event.key)) return;
    const visible = [...document.querySelectorAll(".treasure-choice-menu:not([hidden]), .treasure-type-menu:not([hidden])")][0];
    if (!visible) return;
    const options = [...visible.querySelectorAll("button")];
    if (!options.length) return;
    event.preventDefault();
    if (event.key === "Enter" && document.activeElement?.matches("button")) { document.activeElement.click(); return; }
    const current = options.indexOf(document.activeElement);
    options[(current + (event.key === "ArrowUp" ? -1 : 1) + options.length) % options.length].focus();
  };
  const saveTreasureTemplateDraft = () => {
    const draftKey = `${treasureMapId || ""}:${treasureProfileId || ""}`;
    const draft = {
      name: document.querySelector("[data-treasure-template-name]")?.value || "",
      type: document.querySelector("[data-treasure-template-type]")?.value || "default",
      color: document.querySelector("[data-treasure-template-color]")?.value || "#3498db"
    };
    treasureTemplateDrafts.set(draftKey, draft);
    try { localStorage.setItem(`dayzCompanionTreasureTemplate:${draftKey}`, JSON.stringify(draft)); } catch (_) { }
  };
  document.querySelector("[data-treasure-template-name]")?.addEventListener("input", saveTreasureTemplateDraft);
  document.querySelector("[data-treasure-template-type]")?.addEventListener("change", saveTreasureTemplateDraft);
  document.querySelector("[data-treasure-template-color]")?.addEventListener("input", saveTreasureTemplateDraft);
  document.querySelector("[data-treasure-type-toggle]")?.addEventListener("click", () => {
    treasureTypeMenuOpen = !treasureTypeMenuOpen;
    const rect = document.querySelector("[data-treasure-type-toggle]").getBoundingClientRect();
    treasureTypeMenuUp = window.innerHeight - rect.bottom < 268 && rect.top > 268;
    render();
  });
  document.querySelectorAll("[data-treasure-type-option]").forEach(button => button.addEventListener("click", () => {
    saveTreasureTemplateDraft();
    const draftKey = `${treasureMapId || ""}:${treasureProfileId || ""}`;
    const draft = treasureTemplateDrafts.get(draftKey) || {};
    treasureTemplateDrafts.set(draftKey, { ...draft, type: button.dataset.treasureTypeOption });
    treasureTypeMenuOpen = false;
    render();
  }));
  const treasureSend = document.querySelector("[data-treasure-send]");
  if (treasureSend) {
    const ready = (state.treasures?.captures || []).filter(item => Number.isInteger(item.X) && Number.isInteger(item.Z));
    const hasDuplicates = new Set(ready.map(item => `${item.X}:${item.Z}`)).size !== ready.length;
    treasureSend.textContent = `Отправить координаты (${ready.length})`;
    treasureSend.disabled = treasureSend.disabled || !ready.length || hasDuplicates;
    treasureSend.addEventListener("click", () => {
    treasureCoordinateTimers.forEach((_, id) => saveTreasureCoordinates(id));
    saveTreasureTemplateDraft();
    const template = treasureTemplateDrafts.get(`${treasureMapId || ""}:${treasureProfileId || ""}`);
    post({
    type: "treasure",
    action: "send",
    mapId: treasureMapId,
    profileId: treasureProfileId,
    markerName: template?.name || "",
    markerType: template?.type || "default",
    markerColor: template?.color || "#3498db"
    });
    });
  }
  document.querySelectorAll("[data-hotkey-clear]").forEach(button => {
    if (button.previousElementSibling?.dataset?.hotkeyDefault === button.dataset.hotkeyClear) return;
    const defaultButton = document.createElement("button");
    defaultButton.className = "action";
    defaultButton.type = "button";
    defaultButton.dataset.hotkeyDefault = button.dataset.hotkeyClear;
    defaultButton.textContent = String.fromCharCode(1055, 1086, 32, 1091, 1084, 1086, 1083, 1095, 1072, 1085, 1080, 1102);
    button.before(defaultButton);
  });
  document.querySelectorAll("[data-hotkey-clear]").forEach(button => {
    button.addEventListener("click", () => update(config => config.Hotkeys[button.dataset.hotkeyClear].Enabled = false));
  });
  document.querySelectorAll("[data-hotkey-default]").forEach(button => {
    button.addEventListener("click", () => update(config => config.Hotkeys[button.dataset.hotkeyDefault] = clone(defaultHotkeys[button.dataset.hotkeyDefault])));
  });
  const profileSelect = document.getElementById("profileSelect");
  if (profileSelect) profileSelect.addEventListener("change", event => {
    update(config => config.ActiveProfileId = event.target.value);
  });
  const name = document.getElementById("profileName");
  if (name) name.addEventListener("input", () => {
    const nextName = name.value.trim() || String.fromCharCode(1055, 1088, 1080, 1094, 1077, 1083);
    const targetId = activeTab === "profiles" ? selectedProfile().Id : state.config.ActiveProfileId;
    if (targetId === state.config.ActiveProfileId) {
      document.getElementById("activeProfileName").textContent = nextName;
    }
    const selected = Array.from(document.querySelectorAll("#profileSelect option"))
      .find(option => option.value === targetId);
    if (selected) selected.textContent = nextName;
    update(config => {
      const target = config.Profiles.find(p => p.Id === targetId);
      if (target) target.Name = nextName;
    }, { render: false, debounce: 250 });
  });
  if (name) name.addEventListener("change", flushPendingConfig);
  const monitor = document.getElementById("monitorSelect");
  if (monitor) monitor.addEventListener("change", () => update(config => config.TargetMonitorDeviceName = monitor.value));
}

function updateDayZ(mutator) {
  const next = clone(state.dayZ.settings);
  mutator(next);
  state.dayZ.settings = next;
  post({ type: "updateDayZSettings", settings: next });
  render();
}

function updateBattlePass(mutator) {
  const next = clone(state.battlePass.settings);
  mutator(next);
  state.battlePass.settings = next;
  post({ type: "updateBattlePassSettings", settings: next });
  render();
}

function applyPreset(p, preset) {
  p.ProceduralEnabled = true;
  p.CrosshairEnabled = true;
  p.OutlineEnabled = true;
  p.OutlineThickness = 1;
  p.Color = { R: 0, G: 255, B: 120, A: 230 };
  p.DotOpacity = 230;
  p.DotColor = { R: 0, G: 255, B: 120, A: 230 };
  p.OutlineColor = { R: 0, G: 0, B: 0, A: 180 };
  p.CrosshairRotation = 0;
  p.CrosshairShape = 0;
  p.TShape = false;
  p.DotShape = 0;
  if (preset === "dot") {
    p.Length = 1; p.Gap = 0; p.Thickness = 1; p.DotSize = 7; p.DotEnabled = true; p.TShape = false; p.DotShape = 0;
  } else if (preset === "compact") {
    p.Length = 10; p.Gap = 4; p.Thickness = 2; p.DotSize = 2; p.DotEnabled = true; p.TShape = false; p.DotShape = 0;
  } else if (preset === "thin") {
    p.Length = 26; p.Gap = 9; p.Thickness = 1; p.DotSize = 2; p.DotEnabled = true; p.TShape = false; p.DotShape = 0; p.OutlineEnabled = false;
  } else if (preset === "bold") {
    p.Length = 16; p.Gap = 5; p.Thickness = 6; p.DotSize = 4; p.DotEnabled = true; p.TShape = false; p.DotShape = 0; p.OutlineThickness = 2;
  } else if (preset === "diamond") {
    p.Length = 22; p.Gap = 8; p.Thickness = 2; p.DotSize = 8; p.DotEnabled = true; p.TShape = false; p.DotShape = 2;
  } else if (preset === "crossdot") {
    p.Length = 18; p.Gap = 8; p.Thickness = 2; p.DotSize = 8; p.DotEnabled = true; p.TShape = false; p.DotShape = 5;
  } else if (preset === "t") {
    p.Length = 24; p.Gap = 8; p.Thickness = 3; p.DotSize = 3; p.DotEnabled = true; p.TShape = true; p.DotShape = 0;
  } else if (preset === "x") {
    p.Length = 20; p.Gap = 6; p.Thickness = 3; p.DotSize = 2; p.DotEnabled = false; p.TShape = false; p.CrosshairRotation = 45;
  } else {
    p.Length = 18; p.Gap = 7; p.Thickness = 3; p.DotSize = 3; p.DotEnabled = true; p.TShape = false; p.CrosshairRotation = 0; p.DotShape = 0;
  }
}

function mutateImage(layer, action) {
  if (action === "clear") {
    layer.Path = null; layer.Enabled = false; layer.AnchorX = null; layer.AnchorY = null;
  } else if (action === "center") {
    layer.AnchorX = null; layer.AnchorY = null;
  } else if (action === "reset") {
    layer.OffsetX = 0; layer.OffsetY = 0; layer.Rotation = 0; layer.ScalePercent = 100;
  }
}

function displayHotkey(binding) {
  if (!binding || !binding.Enabled || !binding.Key || binding.Key === "None") return "Отключено";
  return [binding.Control && "Ctrl", binding.Alt && "Alt", binding.Shift && "Shift", binding.Win && "Win", binding.Key].filter(Boolean).join("+");
}

function hotkeyRegistrationWarning(key) {
  const message = state.hotkeyErrors?.[key];
  return message ? `<div class="limit hotkey-warning">⚠ ${escapeHtml(message)}</div>` : "";
}

function currentPreset(p) {
  const dotShape = shapeIndex(p.DotShape, dotShapes);
  if (shapeIndex(p.CrosshairShape, crosshairShapes) !== 0) {
    return "";
  }
  if (p.TShape && p.Length === 24 && p.Gap === 8 && p.Thickness === 3 && p.DotEnabled && p.DotSize === 3) {
    return "t";
  }
  if (!p.DotEnabled && !p.TShape && p.CrosshairRotation === 45 && p.Length === 20 && p.Gap === 6 && p.Thickness === 3) {
    return "x";
  }
  if (p.DotEnabled && !p.TShape && p.CrosshairRotation === 0 && p.Length === 10 && p.Gap === 4 && p.Thickness === 2 && p.DotSize === 2 && dotShape === 0) {
    return "compact";
  }
  if (p.DotEnabled && !p.TShape && p.CrosshairRotation === 0 && p.Length === 26 && p.Gap === 9 && p.Thickness === 1 && p.DotSize === 2 && dotShape === 0) {
    return "thin";
  }
  if (p.DotEnabled && !p.TShape && p.CrosshairRotation === 0 && p.Length === 16 && p.Gap === 5 && p.Thickness === 6 && p.DotSize === 4 && dotShape === 0) {
    return "bold";
  }
  if (p.DotEnabled && !p.TShape && p.CrosshairRotation === 0 && p.Length === 22 && p.Gap === 8 && p.Thickness === 2 && p.DotSize === 8 && dotShape === 2) {
    return "diamond";
  }
  if (p.DotEnabled && !p.TShape && p.CrosshairRotation === 0 && p.Length === 18 && p.Gap === 8 && p.Thickness === 2 && p.DotSize === 8 && dotShape === 5) {
    return "crossdot";
  }
  if (p.DotEnabled && !p.TShape && p.Length === 1 && p.Gap === 0 && p.Thickness === 1 && p.DotSize === 7) {
    return "dot";
  }
  if (p.DotEnabled && !p.TShape && p.CrosshairRotation === 0 && p.Length === 18 && p.Gap === 7 && p.Thickness === 3 && p.DotSize === 3) {
    return "classic";
  }
  return "";
}

function normalizeKey(event) {
  const aliases = { ArrowLeft: "Left", ArrowRight: "Right", ArrowUp: "Up", ArrowDown: "Down", " ": "Space", PrintScreen: "Snapshot" };
  if (aliases[event.key]) return aliases[event.key];
  if (/^Key[A-Z]$/.test(event.code)) return event.code.slice(3);
  if (/^Digit[0-9]$/.test(event.code)) return "D" + event.code.slice(5);
  return event.key.length === 1 ? event.key.toUpperCase() : event.key;
}

document.addEventListener("keydown", event => {
  if (!hotkeyCapture) return;
  event.preventDefault();
  const key = normalizeKey(event);
  if (["Control", "Alt", "Shift", "Meta"].includes(key)) return;
  const target = hotkeyCapture;
  hotkeyCapture = null;
  update(config => config.Hotkeys[target] = {
    Enabled: true,
    Key: key,
    Control: event.ctrlKey,
    Alt: event.altKey,
    Shift: event.shiftKey,
    Win: event.metaKey
  });
});

document.addEventListener("click", event => {
  const markerMenu = event.target.closest("[data-session-marker-menu]");
  if (markerMenu) {
    toggleSessionMarkerMenu(markerMenu);
    return;
  }
  closeSessionMarkerMenu();
  const navGroup = event.target.closest("[data-nav-group]");
  if (navGroup) {
    const id = navGroup.dataset.navGroup;
    const wasOpen = expandedNavGroups.has(id);
    expandedNavGroups.clear();
    if (!wasOpen) expandedNavGroups.add(id);
    renderSidebar();
    return;
  }
  const tab = event.target.closest("[data-tab]");
  if (tab) {
    selectEditorTab(tab.dataset.tab);
    render();
    if (activeTab === "updates" && !state.update) {
      post({ type: "command", name: "checkUpdate" });
    }
    return;
  }
});

document.addEventListener("click", event => {
  const profileButton = event.target.closest("[data-profile-select]");
  if (!profileButton) return;
  selectedProfileId = profileButton.dataset.profileSelect;
  render();
});

document.addEventListener("click", event => {
  const button = event.target.closest("[data-profile]");
  if (!button) return;
  const action = button.dataset.profile;
  update(config => {
    const current = selectedProfile(config);
    if (action === "add") {
      const item = clone(activeProfile(config));
      item.Id = newId();
      item.Name = String.fromCharCode(1053, 1086, 1074, 1099, 1081, 32, 1087, 1088, 1080, 1094, 1077, 1083);
      config.Profiles.push(item);
      config.ActiveProfileId = item.Id;
      selectedProfileId = item.Id;
    } else if (action === "duplicate") {
      const item = clone(current);
      item.Id = newId();
      item.Name = current.Name + " copy";
      config.Profiles.push(item);
      config.ActiveProfileId = item.Id;
      selectedProfileId = item.Id;
    } else if (action === "delete" && config.Profiles.length > 1) {
      config.Profiles = config.Profiles.filter(p => p.Id !== current.Id);
      if (config.Profiles.every(p => p.Id !== config.ActiveProfileId)) {
        config.ActiveProfileId = config.Profiles[0].Id;
      }
      selectedProfileId = config.ActiveProfileId;
    } else if (action === "reset") {
      const index = config.Profiles.findIndex(p => p.Id === current.Id);
      if (index < 0) return;
      config.Profiles[index] = {
        Id: current.Id,
        Name: current.Name,
        Length: 18,
        Gap: 7,
        Thickness: 3,
        DotSize: 3,
        DotOpacity: 230,
        CrosshairRotation: 0,
        CrosshairShape: 0,
        ProceduralEnabled: true,
        CrosshairEnabled: true,
        DotEnabled: true,
        DotShape: 0,
        TShape: false,
        OutlineEnabled: true,
        OutlineThickness: 1,
        Color: { R: 0, G: 255, B: 120, A: 230 },
        DotColor: { R: 0, G: 255, B: 120, A: 230 },
        OutlineColor: { R: 0, G: 0, B: 0, A: 180 },
        ImageLayer: { Enabled: false, Path: null, ScalePercent: 100, Opacity: 255, Rotation: 0, OffsetX: 0, OffsetY: 0, AnchorX: null, AnchorY: null }
      };
      selectedProfileId = current.Id;
    }
  });
});

function refreshPlayerPositionStatus() {
  const last = document.querySelector("[data-player-position-last]");
  if (!last) return;
  const data = state.playerPosition || {};
  const settings = data.settings || {};
  last.textContent = Number.isInteger(settings.LastX) ? `X ${settings.LastX} · Y ${settings.LastY} · Z ${settings.LastZ}` : "—";
  document.querySelector("[data-player-position-sent]").textContent = settings.LastSentAt ? new Date(settings.LastSentAt).toLocaleString() : "—";
  document.querySelector("[data-player-position-errors]").textContent = settings.ConsecutiveErrors || 0;
  const error = document.querySelector("[data-player-position-error]");
  const message = settings.LastError || (settings.Enabled ? data.feedback : "") || "";
  error.textContent = message;
  error.hidden = !message;
  error.classList.toggle("hotkey-warning", Boolean(settings.LastError));
  const result = document.querySelector("[data-player-position-ocr-result]");
  result.hidden = !data.diagnostics;
  if (data.diagnostics) {
    const { Recognized, RawText } = data.diagnostics;
    result.textContent = `${Recognized ? "Распознано" : "Не распознано"}: ${(RawText || "").slice(0, 180)}`;
  }
  document.querySelector('[data-command="openPlayerPositionDiagnostics"]').disabled = !data.diagnostics;
}

sessionMarkerPopover.addEventListener("click", event => {
  const action = event.target.closest("[data-copy-session-marker]")?.dataset.copySessionMarker;
  if (!action || !sessionMarkerSelection) return;
  const { marker } = sessionMarkerSelection;
  closeSessionMarkerMenu(true);
  post({ type: "copySessionMarker", action, marker });
});
sessionMarkerPopover.addEventListener("keydown", event => {
  const first = sessionMarkerPopover.firstElementChild;
  const last = sessionMarkerPopover.lastElementChild;
  const next = event.key === "Home" ? first : event.key === "End" ? last
    : ["ArrowUp", "ArrowDown"].includes(event.key) ? (event.target === first ? last : first) : null;
  if (!next) return;
  event.preventDefault();
  next.focus({ preventScroll: true });
});
sessionMarkerPopover.addEventListener("focusout", event => {
  if (event.relatedTarget && !sessionMarkerPopover.contains(event.relatedTarget)
    && event.relatedTarget !== sessionMarkerSelection?.trigger) closeSessionMarkerMenu();
});
document.addEventListener("pointerdown", event => {
  if (!event.target.closest("#session-marker-popover, [data-session-marker-menu]")) closeSessionMarkerMenu();
});
document.addEventListener("scroll", () => closeSessionMarkerMenu(), true);
window.addEventListener("blur", () => closeSessionMarkerMenu());
window.addEventListener("resize", () => {
  closeSessionMarkerMenu();
  syncSessionMapPlacement();
});
document.querySelectorAll("[data-window-command]").forEach(button => {
  button.addEventListener("click", () => post({ type: "window", name: button.dataset.windowCommand }));
});
document.querySelector(".window-top-resize").addEventListener("pointerdown", event => {
  if (event.button !== 0) return;
  event.preventDefault();
  post({ type: "window", name: "resize-top" });
});

window.DayZMapCompanion = {
  setWindowMaximized(maximized) {
    document.body.classList.toggle("window-maximized", maximized);
    const button = document.querySelector('[data-window-command="maximize"]');
    const label = maximized ? "Восстановить" : "Развернуть";
    button.setAttribute("aria-pressed", String(maximized));
    button.setAttribute("aria-label", label);
    button.title = label;
  },
  sessionMapFailed() {
    const status = document.querySelector("[data-session-map-status]");
    if (status) status.textContent = "Не удалось загрузить карту. Проверьте подключение.";
  },
  receivePerformancePlacement(placement) {
    if (!state?.dayZ?.settings?.Performance) return;
    Object.assign(state.dayZ.settings.Performance, placement);
    document.querySelectorAll("[data-performance-number], [data-performance-select]").forEach(input => {
      const key = input.dataset.performanceNumber || input.dataset.performanceSelect;
      if (Object.hasOwn(placement, key)) input.value = placement[key];
    });
  },
  receivePerformance(value, markerImportBlockReason) {
    if (!state) return;
    state.performance = value;
    if (markerImportBlockReason !== undefined) state.markerImportBlockReason = markerImportBlockReason;
    refreshOverview();
    refreshPerformanceStatus();
  },
  receivePreview(preview) {
    if (state) state.preview = preview;
    setCrosshairPreview(preview);
  },
  receiveState(next) {
    const previous = state;
    const wasDayZStatusPending = dayZStatusPending;
    if (dayZStatusPending && next.dayZ) {
      dayZStatusPending = false;
      const status = next.dayZ.status;
      dayZActionFeedback = status.PrivateMarkersPath
        ? `Готово: файл ${status.FileWritable ? "проверен и доступен для записи" : "найден, но требует внимания"}.`
        : "Поиск завершён: PrivateMarkers.json не найден.";
    }
    if (previous && !next.openTab && isEditingValueControl()) {
      state = keepEditedState(previous, next);
      refreshSessionMarkers();
      setCrosshairPreview(state.preview);
      document.getElementById("activeProfileName").textContent = profile()?.Name || "";
      return;
    }
    const needsRender = renderDataChanged(previous, next) || wasDayZStatusPending || !!next.openTab;
    state = next;
    if (next.openTab) {
      selectEditorTab(next.openTab);
    }
    if (needsRender && (activeTab !== "overview" || !previous || next.openTab)) render();
    else {
      refreshOverview();
      refreshPlayerPositionStatus();
    }
  },
  openTab(tab) {
    selectEditorTab(tab);
    render();
    if (activeTab === "updates" && !state.update) {
      post({ type: "command", name: "checkUpdate" });
    }
  },
  showError(message) {
    const toast = document.getElementById("toast");
    toast.textContent = message;
    toast.classList.add("show");
    setTimeout(() => toast.classList.remove("show"), 4200);
  }
};

let previewResizeTimer;
let lastPreviewSize = "";
function schedulePreviewResize() {
  clearTimeout(previewResizeTimer);
  previewResizeTimer = setTimeout(() => {
    const preview = document.getElementById("preview");
    if (!preview.clientWidth || !preview.clientHeight) return;
    const ratio = window.devicePixelRatio || 1;
    const width = Math.min(8192, Math.max(1, Math.round(preview.clientWidth * ratio)));
    const height = Math.min(8192, Math.max(1, Math.round(preview.clientHeight * ratio)));
    const key = `${width}x${height}`;
    if (key === lastPreviewSize) return;
    lastPreviewSize = key;
    post({ type: "previewSize", width, height });
  }, 120);
}
new ResizeObserver(schedulePreviewResize).observe(document.getElementById("preview"));
new ResizeObserver(syncTreasureCaptureLayout).observe(document.getElementById("editor"));
window.addEventListener("resize", schedulePreviewResize);
post({ type: "ready" });
</script>
</body>
</html>
""";
}
