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
    private readonly System.Windows.Forms.Timer dayZStatusTimer = new() { Interval = 2500 };
    private AppConfig config;
    private DayZCompanionSettings dayZSettings;
    private DayZCompanionStatus dayZStatus;
    private OcrStatus? ocrStatus;
    private readonly System.Windows.Forms.Timer ocrInstallPollTimer = new() { Interval = 5000 };
    private int ocrInstallPollAttempts;
    private Size previewSize = new(720, 420);
    private BattlePassSettings battlePassSettings;
    private BattlePassSnapshot battlePassSnapshot;
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
    public event Action<DayZCompanionSettings>? DayZSettingsChanged;
    public event Action? PlayerPositionRegionRequested;
    public event Action? DayZStatusRequested;
    public event Action<BattlePassSettings>? BattlePassSettingsChanged;
    public event Action<string>? BattlePassCommandRequested;

    public EditorForm(AppConfig source, UpdateService updateService, DayZCompanionSettings dayZSettings, DayZCompanionStatus dayZStatus, BattlePassSettings battlePassSettings, BattlePassSnapshot battlePassSnapshot, TreasureCaptureStore treasureStore, TreasureCaptureService treasureCaptureService, TreasureMapBridge treasureMapBridge, PlayerPositionMapBridge playerPositionMapBridge, PlayerPositionCaptureService playerPositionCaptureService, string? initialTab = null)
    {
        this.updateService = updateService;
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
        StartPosition = FormStartPosition.Manual;
        MinimumSize = new Size(1060, 700);
        Size = new Size(1240, 780);
        BackColor = Color.FromArgb(11, 11, 12);

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
            await webView.EnsureCoreWebView2Async();
            webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            webView.CoreWebView2.Settings.AreDevToolsEnabled = true;
            webView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
            webView.CoreWebView2.NavigateToString(GetEditorHtml());
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"WebView2 editor failed to start.\n\n{ex.Message}",
                AppIdentity.DisplayName,
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private async void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
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
                case "updatePlayerPositionSettings":
                    ApplyPlayerPositionSettingsFromWeb(root.GetProperty("settings"));
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
                await RefreshUpdateInfoAsync(true);
                break;
            case "downloadUpdate":
                if (updateInfo is null)
                {
                    updateInfo = await updateService.GetLatestAsync();
                }
                UpdateService.OpenDownload(updateInfo);
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
            case "openDayZMarkersFolder":
                OpenDayZMarkersFolder();
                break;
            case "openRuntimeLog":
                OpenFileLocation(AppRuntimeLog.FilePath);
                break;
            case "scanBattlePass":
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
                PlayerPositionRegionRequested?.Invoke();
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
        dayZSettings = next;
        DayZSettingsChanged?.Invoke(next);
    }

    private void ApplyPlayerPositionSettingsFromWeb(JsonElement settingsElement)
    {
        var next = settingsElement.Deserialize<PlayerPositionTrackingSettings>(JsonOptions);
        if (next is null) return;
        next.Normalize();
        dayZSettings.PlayerPositionTracking = next;
        DayZSettingsChanged?.Invoke(dayZSettings);
    }

    public void SelectPlayerPositionRegion()
    {
        var bounds = SystemInformation.VirtualScreen;
        using var screenshot = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(screenshot)) graphics.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
        var region = ScreenRegionSelector.SelectRegion(screenshot);
        if (!region.HasValue) return;
        dayZSettings.PlayerPositionTracking.SetScreenRectangle(region.Value, bounds);
        DayZSettingsChanged?.Invoke(dayZSettings);
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
            case "minimize":
                WindowState = FormWindowState.Minimized;
                break;
            case "maximize":
                WindowState = WindowState == FormWindowState.Maximized
                    ? FormWindowState.Normal
                    : FormWindowState.Maximized;
                break;
            case "close":
                Close();
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
            config,
            openTab,
            update = updateInfo,
            dayZ = new { settings = dayZSettings, status = dayZStatus },
            ocr = ocrStatus ??= TesseractOcr.Detect(),
            battlePass = new { settings = battlePassSettings, snapshot = battlePassSnapshot, ocr = ocrStatus },
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
        updateInfo = await updateService.GetLatestAsync(forceRefresh);
        await SendStateAsync();
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
  --bg: #0b0b0c;
  --chrome: #121214;
  --surface: #17171a;
  --raised: #202025;
  --line: #34343a;
  --soft: #28282e;
  --text: #eeeeef;
  --muted: #9b9ba3;
  --faint: #6f6f78;
  --accent: #eeb15b;
  --accent-2: #77d6a4;
  --danger: #d25d69;
}
* { box-sizing: border-box; }
body {
  margin: 0;
  min-width: 980px;
  min-height: 680px;
  background: var(--bg);
  color: var(--text);
  font: 14px/1.45 "Segoe UI", system-ui, sans-serif;
  overflow: hidden;
}
button, input, select {
  font: inherit;
}
.app {
  height: 100vh;
  display: grid;
  grid-template-rows: 58px 1fr;
  padding: 14px;
  gap: 12px;
}
.titlebar {
  display: grid;
  grid-template-columns: 1fr;
  align-items: center;
  background: var(--chrome);
  border: 1px solid var(--soft);
  border-radius: 10px;
  padding: 0 12px 0 18px;
}
.brand {
  display: flex;
  align-items: baseline;
  gap: 10px;
}
.brand strong {
  font-size: 18px;
  letter-spacing: 0;
}
.brand span {
  color: var(--muted);
  font-size: 12px;
}
.layout {
  min-height: 0;
  min-width: 0;
  display: grid;
  grid-template-columns: 250px minmax(460px, 1fr);
  gap: 12px;
}
.layout.with-crosshair-preview {
  grid-template-columns: 250px minmax(0, 760px) minmax(320px, 1fr);
  justify-content: start;
}
.layout:not(.with-crosshair-preview) .preview-wrap {
  display: none;
}
.panel {
  min-height: 0;
  min-width: 0;
  background: var(--surface);
  border: 1px solid var(--soft);
  border-radius: 10px;
}
.sidebar {
  padding: 14px;
  display: grid;
  grid-template-rows: 1fr auto;
  gap: 14px;
}
.profile-select, .field select, .field input[type="text"], .number-input {
  width: 100%;
  height: 38px;
  color: var(--text);
  background: #101014;
  border: 1px solid var(--line);
  border-radius: 8px;
  padding: 0 10px;
  outline: none;
}
.nav {
  display: grid;
  gap: 8px;
  align-content: start;
}
.nav button, .action {
  height: 38px;
  border: 1px solid transparent;
  border-radius: 8px;
  background: transparent;
  color: var(--muted);
  text-align: left;
  padding: 0 12px;
  cursor: pointer;
}
.nav button.active {
  background: #2c2419;
  border-color: #614720;
  color: var(--accent);
}
.nav button:hover, .action:hover {
  background: var(--raised);
  color: var(--text);
}
.nav-group {
  display: grid;
  gap: 4px;
}
.nav-group-toggle, .nav-main {
  color: var(--text) !important;
  font-weight: 600;
}
.nav-group-toggle span {
  float: right;
  color: var(--faint);
}
.nav-children {
  display: grid;
  gap: 4px;
  margin-left: 12px;
  padding-left: 8px;
  border-left: 1px solid var(--soft);
}
.nav-children button {
  height: 34px !important;
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
  padding: 14px;
  overflow: auto;
}
.editor.updates-editor {
  display: flex;
  overflow: hidden;
}
.section {
  display: none;
  gap: 10px;
}
.section.active {
  display: grid;
}
.section.updates-section {
  flex: 1 1 auto;
  min-height: 0;
}
.updates-layout {
  height: 100%;
  min-height: 0;
  display: grid;
  grid-template-rows: auto auto minmax(0, 1fr) auto;
  gap: 10px;
}
.update-notes-slot, .update-notes-slot .field {
  min-height: 0;
}
.update-notes-slot .field {
  height: 100%;
  box-sizing: border-box;
  grid-template-rows: auto minmax(0, 1fr);
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
  width: min(420px, 100%);
  max-height: 220px;
  object-fit: contain;
  object-position: left center;
  margin: 8px 0;
  border: 1px solid #3d4046;
  border-radius: 6px;
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
  background: #1d1d21;
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
  background: #101014;
}
.treasure-destination-grid { display: grid; grid-template-columns: minmax(180px, 420px) minmax(180px, 420px); gap: 8px; align-items: end; }
.treasure-destination-grid label, .treasure-template-label { display: grid; gap: 5px; }
.treasure-template-row { display: grid; grid-template-columns: minmax(110px, 1fr) minmax(170px, 250px) 38px; gap: 8px; align-items: center; }
.treasure-template-row input[type="text"] { min-width: 0; }
.treasure-color-picker { width: 38px !important; height: 38px !important; padding: 3px !important; cursor: pointer; }
.treasure-type-picker { position: relative; min-width: 0; }
.treasure-choice-picker { position: relative; min-width: 0; }
.treasure-type-trigger { width: 100%; height: 38px; display: flex; align-items: center; gap: 8px; padding: 0 10px; color: var(--text); background: #101014; border: 1px solid var(--line); border-radius: 8px; cursor: pointer; text-align: left; }
.treasure-choice-trigger { width: 100%; height: 38px; display: flex; align-items: center; padding: 0 10px; color: var(--text); background: #101014; border: 1px solid var(--line); border-radius: 8px; cursor: pointer; text-align: left; }
.treasure-type-trigger::after { content: "⌄"; margin-left: auto; color: var(--faint); }
.treasure-choice-trigger::after { content: "⌄"; margin-left: auto; color: var(--faint); }
.treasure-type-icon { width: 18px; height: 18px; flex: 0 0 18px; fill: none; stroke: currentColor; stroke-width: 1.8; stroke-linecap: round; stroke-linejoin: round; }
.treasure-type-menu, .treasure-choice-menu { position: absolute; z-index: 20; top: calc(100% + 4px); left: 0; right: 0; display: grid; max-height: 264px; overflow-y: auto; padding: 4px; background: #101014; border: 1px solid var(--line); border-radius: 8px; box-shadow: 0 12px 24px rgba(0,0,0,.35); }
.treasure-type-picker.open-up .treasure-type-menu, .treasure-choice-picker.open-up .treasure-choice-menu { top: auto; bottom: calc(100% + 4px); }
.treasure-type-menu[hidden] { display: none; }
.treasure-choice-menu[hidden] { display: none; }
.treasure-type-option { height: 32px; display: flex; align-items: center; gap: 8px; padding: 0 8px; border: 0; border-radius: 5px; color: var(--text); background: transparent; cursor: pointer; text-align: left; }
.treasure-choice-option { height: 32px; display: flex; align-items: center; padding: 0 8px; border: 0; border-radius: 5px; color: var(--text); background: transparent; cursor: pointer; text-align: left; }
.treasure-type-option:hover, .treasure-type-option.active { background: #3a3a3e; }
.treasure-choice-option:hover, .treasure-choice-option.active { background: #3a3a3e; }
@media (max-width: 700px) { .treasure-destination-grid { grid-template-columns: 1fr; } .treasure-template-row { grid-template-columns: minmax(90px, 1fr) minmax(150px, 1fr) 38px; } }
.actions {
  display: grid;
  grid-template-columns: 1fr 1fr;
  gap: 8px;
  min-width: 0;
}
.action {
  text-align: center;
  border-color: var(--line);
  background: #19191d;
  color: var(--text);
}
.action.primary {
  background: #3a2a17;
  border-color: #6e4c1b;
  color: var(--accent);
}
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
  background: #3a2a17;
  border-color: #6e4c1b;
  color: var(--accent);
}
.layout:not(.with-crosshair-preview) .editor {
  container-type: inline-size;
}
.layout:not(.with-crosshair-preview) .section {
  width: 100%;
  max-width: 1180px;
  min-width: 0;
  align-content: start;
}
.layout:not(.with-crosshair-preview) .editor[data-page="general"] .section,
.layout:not(.with-crosshair-preview) .editor[data-page="taskhotkeys"] .section {
  max-width: 760px;
}
.layout:not(.with-crosshair-preview) .section.updates-section {
  align-content: stretch;
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
  background: #101014;
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
.settings-columns {
  display: grid;
  grid-template-columns: minmax(0, 1fr);
  gap: 14px;
  align-items: start;
}
.settings-card {
  display: grid;
  min-width: 0;
  gap: 10px;
  padding: 14px;
  border: 1px solid var(--soft);
  border-radius: 10px;
}
.settings-card h3 {
  margin: 0 0 2px;
  color: var(--text);
  font-size: 14px;
  font-weight: 650;
}
@container (min-width: 900px) {
  .settings-columns {
    grid-template-columns: repeat(2, minmax(0, 1fr));
  }
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
  background: #19191d;
  color: var(--text);
  text-align: left;
  padding: 0 12px;
  cursor: pointer;
}
.profile-list button.active {
  background: #2c2419;
  border-color: #614720;
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
  padding: 14px;
  display: grid;
  grid-template-rows: auto 1fr;
  gap: 12px;
}
.preview-head {
  display: flex;
  justify-content: space-between;
  gap: 12px;
  align-items: center;
}
.preview-head h2 {
  margin: 0;
  font-size: 18px;
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
  background: #0f0f12;
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
</style>
</head>
<body>
<div class="app">
  <header class="titlebar">
    <div class="brand"><strong>DayZ-Map.ru Companion</strong></div>
  </header>

  <main class="layout with-crosshair-preview" id="layout">
    <aside class="panel sidebar">
      <div class="nav" id="nav"></div>
      <button class="action danger" id="exitApplication">Выйти из приложения</button>
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
</div>
<div class="toast" id="toast"></div>

<!-- marker-icons -->
<script>
const bridge = window.chrome.webview;
let state = null;
let activeTab = "crosshair";
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

const navigation = [
  { tab: ["player-position", "Позиция игрока"] },
  { tab: ["treasures", "\u041a\u043b\u0430\u0434\u044b"] },
  { tab: ["dayz", "Синхронизация меток"] },
  { id: "tasks", label: "Отслеживание заданий", tabs: [
    ["tasks", "Настройки"],
    ["taskhotkeys", "Горячие клавиши"]
  ] },
  { id: "crosshair", label: "Прицел", tabs: [
    ["crosshair", "Настройка прицела"],
    ["image", "Изображение"],
    ["profiles", "Профили"],
    ["hotkeys", "Горячие клавиши"],
    ["monitor", "Монитор"]
  ] },
  { id: "application", label: "Приложение", tabs: [
    ["general", "Общие"],
    ["updates", "Обновление"]
  ] }
];
let expandedNavGroups = new Set();

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

function slider(label, path, min, max) {
  const p = profile();
  const value = getPath(p, path);
  return field(label, {
    value: `<span class="value">${value}</span>`,
    input: `<div class="slider-row"><input type="range" min="${min}" max="${max}" value="${value}" data-slider="${path}"><input class="number-input" type="number" min="${min}" max="${max}" value="${value}" data-number="${path}"></div>`
  });
}

function check(label, path) {
  const checked = getPath(profile(), path) ? "checked" : "";
  return field(label, {
    value: `<input type="checkbox" ${checked} data-check="${path}">`,
    input: ``
  });
}

function color(label, path) {
  const c = getPath(profile(), path);
  const hex = "#" + [c.R, c.G, c.B].map(v => v.toString(16).padStart(2, "0")).join("");
  return field(label, {
    input: `<input type="color" value="${hex}" data-color="${path}">`
  });
}

function select(label, path, options) {
  const value = getPath(profile(), path);
  const html = options.map(([id, name]) => `<option value="${id}" ${String(value) === String(id) ? "selected" : ""}>${name}</option>`).join("");
  return field(label, {
    input: `<select data-select="${path}">${html}</select>`
  });
}

function text(label, id, value) {
  return field(label, {
    input: `<input type="text" id="${id}" value="${escapeHtml(value || "")}">`
  });
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
  const showPreview = navigation.find(item => item.id === "crosshair").tabs.some(([id]) => id === activeTab);
  document.getElementById("layout").classList.toggle("with-crosshair-preview", showPreview);
  renderSidebar();
  renderEditor();
  document.getElementById("preview").src = state.preview || "";
  document.getElementById("activeProfileName").textContent = profile()?.Name || "";
}

function isEditingValueControl() {
  const active = document.activeElement;
  return !!active?.matches?.("input, select, textarea, [contenteditable='true']");
}

function renderDataChanged(previous, next) {
  if (!previous) return true;
  // DayZ status is polled periodically. It is not form data, so it must not
  // recreate the whole editor and steal focus from a field or an open select.
  const stable = value => ({
    config: value.config,
    openTab: value.openTab,
    update: value.update,
    ocr: value.ocr,
    dayZSettings: value.dayZ?.settings,
    battlePass: value.battlePass,
    treasures: value.treasures,
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
    dayZ: next.dayZ ? { ...previous.dayZ, status: next.dayZ.status } : previous.dayZ
  };
}

function renderSidebar() {
  document.getElementById("nav").innerHTML = navigation.map(item => {
    if (item.tab) {
      const [id, label] = item.tab;
      return `<button class="nav-main ${id === activeTab ? "active" : ""}" data-tab="${id}">${label}</button>`;
    }
    const open = expandedNavGroups.has(item.id);
    const children = item.tabs.map(([id, label]) => `<button class="${id === activeTab ? "active" : ""}" data-tab="${id}">${label}</button>`).join("");
    return `<div class="nav-group"><button class="nav-group-toggle" data-nav-group="${item.id}">${item.label}<span>${open ? "▾" : "▸"}</span></button>${open ? `<div class="nav-children">${children}</div>` : ""}</div>`;
  }).join("");
}

function expandNavForTab(tab) {
  const group = navigation.find(item => item.tabs?.some(([id]) => id === tab));
  if (group) expandedNavGroups.add(group.id);
}

function renderEditor() {
  const p = profile();
  const sections = {
    general: renderGeneral(),
    crosshair: renderCrosshair(),
    image: renderImage(),
    hotkeys: renderHotkeys(),
    taskhotkeys: renderBattlePassHotkeys(),
    monitor: renderMonitor(),
    dayz: renderDayZ(),
    "player-position": renderPlayerPosition(),
    treasures: renderTreasures(),
    tasks: renderBattlePass(),
    profiles: renderProfiles(),
    updates: renderUpdates()
  };
  const editor = document.getElementById("editor");
  editor.dataset.page = activeTab;
  editor.classList.toggle("updates-editor", activeTab === "updates");
  editor.innerHTML = `<div class="section active ${activeTab === "updates" ? "updates-section" : ""}">${sections[activeTab]}</div>`;
  if (activeTab === "crosshair") {
    organizeCrosshairSection();
  }
  syncPresetButtons();
  bindEditorEvents();
}

function organizeCrosshairSection() {
  const section = document.querySelector("#editor .section.active");
  if (!section) return;

  if (!section.querySelector("[data-slider='DotOpacity']")) {
    section.insertAdjacentHTML("beforeend", slider("Прозрачность точки", "DotOpacity", 0, 255));
  }
  if (!section.querySelector("[data-color-role='crosshair']")) {
    section.insertAdjacentHTML("beforeend", color("Цвет перекрестия", "Color"));
    const colorInputs = section.querySelectorAll("[data-color='Color']");
    colorInputs.item(colorInputs.length - 1).dataset.colorRole = "crosshair";
  }
  if (!section.querySelector("[data-color-role='dot']")) {
    section.insertAdjacentHTML("beforeend", color("Цвет точки", "DotColor"));
    section.querySelector("[data-color='DotColor']").dataset.colorRole = "dot";
  }

  const title = section.querySelector("h2");
  const presets = section.querySelector(".actions");
  const byPath = selector => section.querySelector(selector)?.closest(".field");
  const move = (group, nodes) => {
    nodes.filter(Boolean).forEach(node => group.appendChild(node));
  };
  const createGroup = name => {
    const group = document.createElement("div");
    group.className = "control-group";
    const groupTitle = document.createElement("div");
    groupTitle.className = "group-title";
    groupTitle.textContent = name;
    group.appendChild(groupTitle);
    return group;
  };

  const presetGroup = createGroup("Пресеты");
  const crosshairGroup = createGroup("Перекрестие");
  const dotGroup = createGroup("Точка");
  const outlineGroup = createGroup("Обводка");
  dotGroup.classList.add("separated");
  outlineGroup.classList.add("separated");
  const crosshairOpacity = byPath("[data-slider='Color.A']");
  const crosshairColor = byPath("[data-color-role='crosshair']");
  const dotColor = byPath("[data-color-role='dot']");
  const commonColor = Array.from(section.querySelectorAll("[data-color='Color']"))
    .find(input => input.dataset.colorRole !== "crosshair")
    ?.closest(".field");
  const commonColorInput = commonColor?.querySelector("[data-color='Color']");
  if (commonColorInput) {
    commonColorInput.removeAttribute("data-color");
    commonColorInput.dataset.colorAll = "true";
  }
  const crosshairOpacityLabel = crosshairOpacity?.querySelector("label");
  if (crosshairOpacityLabel) {
    crosshairOpacityLabel.textContent = "Прозрачность перекрестия";
  }
  const proceduralField = byPath("[data-check='ProceduralEnabled']");
  const proceduralLabel = proceduralField?.querySelector("label");
  if (proceduralLabel) {
    proceduralLabel.textContent = String.fromCharCode(1054, 1090, 1086, 1073, 1088, 1072, 1078, 1077, 1085, 1080, 1077, 32, 1087, 1088, 1080, 1094, 1077, 1083, 1072);
  }

  if (!section.querySelector("[data-check='CrosshairEnabled']")) {
    section.insertAdjacentHTML("beforeend", check(crosshairGroup.querySelector(".group-title").textContent, "CrosshairEnabled"));
  }

  move(presetGroup, [presets]);
  move(crosshairGroup, [
    byPath("[data-check='CrosshairEnabled']"),
    byPath("[data-slider='Length']"),
    byPath("[data-slider='Gap']"),
    byPath("[data-slider='Thickness']"),
    byPath("[data-slider='CrosshairRotation']"),
    byPath("[data-select='CrosshairShape']"),
    byPath("[data-check='TShape']"),
    crosshairOpacity,
    crosshairColor
  ]);
  move(dotGroup, [
    byPath("[data-check='DotEnabled']"),
    byPath("[data-slider='DotSize']"),
    byPath("[data-slider='DotOpacity']"),
    byPath("[data-select='DotShape']"),
    dotColor
  ]);
  move(outlineGroup, [
    byPath("[data-check='OutlineEnabled']"),
    byPath("[data-slider='OutlineThickness']"),
    byPath("[data-slider='OutlineColor.A']"),
    byPath("[data-color='OutlineColor']"),
    commonColor
  ]);

  section.replaceChildren(title, proceduralField, presetGroup, crosshairGroup, dotGroup, outlineGroup);
}

function syncPresetButtons() {
  const preset = currentPreset(profile());
  document.querySelectorAll("[data-preset]").forEach(button => {
    button.classList.remove("primary");
    button.classList.toggle("active", button.dataset.preset === preset);
  });
}

function renderCrosshair() {
  return `
    <h2>Прицел</h2>
    <div class="actions">
      <button class="action primary" data-preset="classic">Классика</button>
      <button class="action" data-preset="dot">Точка</button>
      <button class="action" data-preset="compact">Компакт</button>
      <button class="action" data-preset="thin">Тонкий</button>
      <button class="action" data-preset="bold">Жирный</button>
      <button class="action" data-preset="diamond">Ромб</button>
      <button class="action" data-preset="crossdot">Крест-точка</button>
      <button class="action" data-preset="t">T-форма</button>
      <button class="action" data-preset="x">X</button>
    </div>
    ${check("Процедурный прицел", "ProceduralEnabled")}
    ${slider("Длина", "Length", 1, 80)}
    ${slider("Зазор", "Gap", 0, 50)}
    ${slider("Толщина", "Thickness", 1, 20)}
    ${slider("Поворот", "CrosshairRotation", 0, 359)}
    ${select("Форма перекрестия", "CrosshairShape", [[0, "Классика"], [1, "Плотный плюс"], [2, "Сплошной плюс"], [3, "Уголки"], [4, "Рамка"], [5, "Кольцо"], [6, "Дуги"], [7, "Шеврон"], [8, "Скобки"], [9, "Точки по углам"], [10, "Снайперский"]])}
    ${slider("Размер точки", "DotSize", 1, 30)}
    ${check("Центральная точка", "DotEnabled")}
    ${select("Форма точки", "DotShape", [[0, "Circle"], [1, "Square"], [2, "Diamond"], [3, "Triangle"], [4, "Heart"], [5, "Cross"], [6, "Star"], [7, "Hexagon"], [8, "Pentagon"], [9, "X"], [10, "Paw"]])}
    ${check("T-форма", "TShape")}
    ${check("Обводка", "OutlineEnabled")}
    ${slider("Толщина обводки", "OutlineThickness", 1, 10)}
    ${slider("Прозрачность", "Color.A", 0, 255)}
    ${slider("Прозрачность обводки", "OutlineColor.A", 0, 255)}
    ${color("Цвет", "Color")}
    ${color("Цвет обводки", "OutlineColor")}
  `;
}

function renderGeneral() {
  const overlaySize = state.config.OverlayWindowSize;
  return `
    <h2>Общие</h2>
    ${field("Запускать вместе с Windows", { value: `<input type="checkbox" ${state.config.StartWithWindows ? "checked" : ""} data-general-check="StartWithWindows">`, input: `` })}
    ${field("Запускать свёрнутым в трей", { value: `<input type="checkbox" ${state.config.StartMinimizedToTray ? "checked" : ""} data-general-check="StartMinimizedToTray">`, input: `` })}
    ${field("Размер окна прицела", { input: `<select data-overlay-size>
      <option value="Compact200" ${overlaySize === "Compact200" ? "selected" : ""}>200 × 200 (по умолчанию)</option>
      <option value="QuarterScreen" ${overlaySize === "QuarterScreen" ? "selected" : ""}>25% экрана</option>
      <option value="HalfScreen" ${overlaySize === "HalfScreen" ? "selected" : ""}>50% экрана</option>
      <option value="ThreeQuartersScreen" ${overlaySize === "ThreeQuartersScreen" ? "selected" : ""}>75% экрана</option>
      <option value="FullScreen" ${overlaySize === "FullScreen" ? "selected" : ""}>100% экрана</option>
    </select>` })}
  `;
}

function renderImage() {
  const layer = profile().ImageLayer;
  return `
    <h2>Изображение</h2>
    ${check("Включить изображение", "ImageLayer.Enabled")}
    ${slider("Масштаб", "ImageLayer.ScalePercent", 1, 400)}
    ${slider("Прозрачность", "ImageLayer.Opacity", 0, 255)}
    ${slider("Поворот", "ImageLayer.Rotation", 0, 359)}
    ${slider("Смещение X", "ImageLayer.OffsetX", -500, 500)}
    ${slider("Смещение Y", "ImageLayer.OffsetY", -500, 500)}
    ${field("Файл", { input: `<div class="limit">${escapeHtml(layer.Path || "Не выбран")}</div>` })}
    <div class="actions">
      <button class="action primary" data-command="importImage">Импорт</button>
      <button class="action" data-image="clear">Очистить</button>
      <button class="action" data-image="center">Центр якоря</button>
      <button class="action" data-image="reset">Сброс позиции</button>
    </div>
  `;
}

function renderOcrStatus(ocr = state.ocr) {
  const ready = !!ocr?.Ready;
  const status = ready ? "OCR: готово" : "OCR: не установлен";
  return field("Распознавание текста", { input: `<div class="update-status"><strong>${status}</strong><span>${escapeHtml(ocr?.Message || "Проверка OCR ещё не выполнена.")}</span></div><div class="actions"><button class="action primary" data-command="installOcr" ${ready ? "disabled" : ""}>Установить OCR</button><button class="action" data-command="refreshOcr">Проверить снова</button></div>` });
}

function renderPlayerPosition() {
  const data = state.playerPosition || {};
  const s = data.settings || {};
  const destination = data.destination;
  const diagnostics = data.diagnostics;
  const selectedShape = playerPositionShapes.find(item => item.id === s.IndicatorShape) || playerPositionShapes[0];
  const shapeOptions = playerPositionShapes.map(item => `<option value="${item.id}" ${item.id === selectedShape.id ? "selected" : ""}>${escapeHtml(item.name)}</option>`).join("");
  const last = Number.isInteger(s.LastX) ? `X=${s.LastX}, Y=${s.LastY}, Z=${s.LastZ}` : "ещё не распознана";
  const status = s.Enabled && !s.Paused ? "включено" : s.Paused ? "на паузе" : "выключено";
  return `<h2>Позиция игрока</h2>${renderOcrStatus()}<div class="control-group">
    <div class="limit">Захватывается только выделенная область видимого HUD; память игры, инъекции и игровой ввод не используются.</div>
    <div class="field"><label><input type="checkbox" data-position-enabled ${s.Enabled ? "checked" : ""}> Отслеживание: ${status} (${s.Enabled ? "снимите галочку, чтобы отключить отслеживание" : "поставьте галочку, чтобы включить отслеживание"})</label></div>
    <div class="field"><label><input type="checkbox" data-position-paused ${s.Paused ? "checked" : ""}> Пауза</label></div>
    <div class="limit">Карта определяется автоматически по открытой вкладке DayZ-Map.</div>
    <div class="actions"><input data-position-name value="${escapeHtml(s.MarkerName || "")}" placeholder="Подпись маркера"><input class="treasure-color-picker" data-position-color type="color" value="${escapeHtml(s.MarkerColor || "#3498db")}" title="Цвет индикатора"></div>
    <div class="field"><label>Фигура индикатора <select data-position-shape>${shapeOptions}</select></label><div class="limit">Это временный индикатор позиции — он не добавляется в профиль, списки и экспорт карты.</div></div>
    <div class="field"><label>Интервал, секунд <input class="number" data-position-interval type="number" min="2" max="300" value="${s.IntervalSeconds || 5}"></label><div class="limit">Допустимый диапазон: 2–300 секунд.</div></div>
    <div class="actions"><button class="action" data-command="selectPlayerPositionRegion">Настроить область координат</button><button class="action" data-command="testPlayerPosition">Тест распознавания</button><button class="action" data-command="openPlayerPositionDiagnostics" ${diagnostics ? "" : "disabled"}>Открыть диагностику OCR</button></div>
    <div class="limit" data-player-position-status>Область: ${s.HasRegion ? "настроена" : "не выбрана"}. Последняя позиция: ${last}. Последняя отправка: ${s.LastSentAt ? new Date(s.LastSentAt).toLocaleString() : "—"}. Ошибок подряд: ${s.ConsecutiveErrors || 0}.</div>
    <div class="limit ${s.LastError ? "hotkey-warning" : ""}" data-player-position-error>${escapeHtml(s.LastError || data.feedback || "")}</div>${diagnostics ? `<div class="limit">OCR ${diagnostics.Recognized ? "распознано" : "не распознано"}: ${escapeHtml((diagnostics.RawText || "").slice(0, 180))}</div>` : ""}${destination ? "" : `<div class="limit">Откройте DayZ-Map в браузере и подключите Companion.</div>`}
  </div>`;
}

function renderTreasures() {
  const items = state.treasures?.captures || [];
  const readyItems = items.filter(item => Number.isInteger(item.X) && Number.isInteger(item.Z));
  const duplicateKeys = new Set(readyItems.map(item => `${item.X}:${item.Z}`).filter((key, _, all) => all.filter(value => value === key).length > 1));
  const errors = items.filter(item => item.Status === "Ambiguous");
  const cards = items.map(item => {
    const duplicate = duplicateKeys.has(`${item.X}:${item.Z}`);
    const result = item.SentAt || item.DeliveryResult ? `<div class="limit ${item.SentAt ? "" : "hotkey-warning"}">${escapeHtml(item.DeliveryResult || "Отправлено")}</div>` : duplicate ? `<div class="limit hotkey-warning">⚠ Дубликат координат в очереди.</div>` : "";
    const retry = item.Status === "Ambiguous" ? `<button class="action" data-treasure="retry" data-id="${item.Id}">Повторить OCR</button>` : "";
    return `<div class="field"><div class="field-label">${escapeHtml(new Date(item.CapturedAt).toLocaleString())}</div>${item.preview ? `<img class="treasure-preview" data-treasure-preview src="${item.preview}" alt="Скриншот уведомления">` : ""}<div class="actions"><input class="number" data-treasure-x="${item.Id}" value="${item.X ?? ""}" placeholder="X"><input class="number" data-treasure-z="${item.Id}" value="${item.Z ?? ""}" placeholder="Z">${retry}<button class="action" data-treasure="delete" data-id="${item.Id}">Удалить</button></div>${result}<div class="limit">${escapeHtml(item.RawText || "Ожидание OCR…")}</div></div>`;
  }).join("") || `<div class="limit">Пока нет захватов.</div>`;
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
    return `<div class="treasure-choice-picker ${opensUp ? "open-up" : ""}"><button type="button" class="treasure-choice-trigger" data-treasure-choice-toggle="${kind}">${escapeHtml(selected?.Name || "Не выбрано")}</button><div class="treasure-choice-menu" ${isOpen ? "" : "hidden"}>${items.map(item => `<button type="button" class="treasure-choice-option ${item.Id === value ? "active" : ""}" data-treasure-choice-option data-treasure-choice-kind="${kind}" data-treasure-choice-value="${item.Id}">${escapeHtml(item.Name)}</button>`).join("")}</div></div>`;
  };
  const mapPicker = choicePicker("map", maps, treasureMapId, treasureMapMenuOpen, treasureMapMenuUp);
  const profilePicker = choicePicker("profile", profiles, treasureProfileId, treasureProfileMenuOpen, treasureProfileMenuUp);
  const destinationUi = !destination ? `<div class="field"><div class="field-label">Карта</div><div class="limit">Откройте DayZ-Map в браузере: Companion ожидает подключение карты.</div></div>` : `<div class="field"><div class="field-label">Отправка на карту</div><div class="treasure-destination-grid"><label>Карта${mapPicker}</label><label>Профиль${profilePicker}</label></div><div class="treasure-template-label">Настройки метки<div class="treasure-template-row"><input type="text" data-treasure-template-name value="${escapeHtml(selectedTemplate.name)}" placeholder="Название метки" maxlength="300"><div class="treasure-type-picker ${treasureTypeMenuUp ? "open-up" : ""}"><input type="hidden" data-treasure-template-type value="${selectedMarkerType.id}"><button type="button" class="treasure-type-trigger" data-treasure-type-toggle>${treasureMarkerIcon(selectedMarkerType.id, selectedMarkerType.color)}<span>${escapeHtml(selectedMarkerType.name)}</span></button><div class="treasure-type-menu" ${treasureTypeMenuOpen ? "" : "hidden"}>${typeOptions}</div></div><input class="treasure-color-picker" data-treasure-template-color type="color" value="${selectedColor}" title="Цвет метки"></div></div><div><button class="action primary" data-treasure-send ${!treasureProfileId ? "disabled" : ""}>Отправить координаты</button></div><div class="limit">Шаблон задаётся для этой отправки и не изменяет настройки профиля на карте.</div></div>`;
  const feedback = state.treasures?.feedback ? `<div class="limit">${escapeHtml(state.treasures.feedback)}</div>` : "";
  const preview = treasurePreviewUri ? `<div class="treasure-image-modal" data-treasure-preview-close><img src="${treasurePreviewUri}" alt="Скриншот уведомления"></div>` : "";
  return `<h2>Клады</h2>${renderOcrStatus()}<div class="control-group"><div class="field-row"><span>Горячая клавиша захвата</span><button class="action" data-hotkey="CaptureTreasure">${displayHotkey(state.config.Hotkeys.CaptureTreasure)}</button></div>${hotkeyRegistrationWarning("CaptureTreasure")}<div class="actions"><button class="action primary" data-command="captureTreasure" ${state.treasures?.selecting ? "disabled" : ""}>Выделить область уведомления</button><button class="action" data-command="openTreasureCapturesFolder">Открыть папку PNG</button><button class="action" data-treasure="clear" ${items.length ? "" : "disabled"}>Очистить все</button>${errors.length ? `<button class="action" data-treasure="clearErrors">Удалить ошибки OCR (${errors.length})</button>` : ""}</div>${queue}${feedback}${cards}${destinationUi}${preview}</div>`;
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
  return `<h2>Горячие клавиши</h2>` + keys.map(([key, label]) => {
    const binding = state.config.Hotkeys[key];
    return field(label, {
      input: `<div class="actions"><button class="action" data-hotkey="${key}">${displayHotkey(binding)}</button><button class="action" data-hotkey-clear="${key}">Очистить</button></div>${hotkeyRegistrationWarning(key)}`
    });
  }).join("");
}

function renderMonitor() {
  const value = state.config.TargetMonitorDeviceName || "";
  const options = state.monitors.map(m => `<option value="${m.DeviceName}" ${m.DeviceName === value ? "selected" : ""}>${escapeHtml(m.DisplayName)}</option>`).join("");
  return `
    <h2>Монитор</h2>
    ${field("Целевой монитор", { input: `<select id="monitorSelect">${options}</select>` })}
    <div class="actions">
      <button class="action" data-command="refreshMonitors">Обновить мониторы</button>
    </div>
  `;
}

function renderBattlePass() {
  const bp = state.battlePass || { settings: {}, snapshot: { Tasks: [] } };
  const s = bp.settings;
  const ocr = bp.ocr;
  const tasks = bp.snapshot?.Tasks || [];
  const monitors = [{ DeviceName: "", DisplayName: "Основной монитор" }, ...state.monitors];
  const monitorOptions = monitors.map(m => `<option value="${m.DeviceName}" ${m.DeviceName === (s.MonitorDeviceName || "") ? "selected" : ""}>${escapeHtml(m.DisplayName)}</option>`).join("");
  return `
    <h2>Отслеживание заданий</h2>
    ${renderOcrStatus(ocr)}
    <div class="limit">Откройте Battle Pass в DayZ, выберите тип страницы ниже и нажмите «Считать экран». Для еженедельных заданий повторите для страниц 1 и 2.</div>
    ${field("Монитор", { input: `<select data-bp-select="MonitorDeviceName">${monitorOptions}</select>` })}
    <div class="settings-columns">
    <div class="settings-card">
    <h3>Отображение заданий</h3>
    ${field("Оверлей", { input: `<label><input type="checkbox" data-bp-check="OverlayVisible" ${s.OverlayVisible ? "checked" : ""}> Показывать</label> <label><input type="checkbox" data-bp-check="ShowCompleted" ${s.ShowCompleted ? "checked" : ""}> Выполненные</label> <label><input type="checkbox" data-bp-check="ShowSeasonal" ${s.ShowSeasonal ? "checked" : ""}> Сезонные</label> <label><input type="checkbox" data-bp-check="ShowTaskDescriptions" ${s.ShowTaskDescriptions ? "checked" : ""}> Показывать описания</label> <label><input type="checkbox" data-bp-check="OverlayEditingEnabled" ${s.OverlayEditingEnabled ? "checked" : ""}> Перемещение и изменение размера</label> <label><input type="checkbox" data-bp-check="SaveDebugScreenshot" ${s.SaveDebugScreenshot ? "checked" : ""}> Сохранять отладочный снимок</label>` })}
    </div>
    <div class="settings-card">
    <h3>Размер и оформление</h3>
    ${field("Размер", { input: `<label>Ширина <input type="number" min="220" max="900" value="${s.Width || 360}" data-bp-number="Width"></label> <label>Высота <input type="number" min="92" max="850" value="${s.Height || 470}" data-bp-number="Height"></label> <label>Шрифт <input type="number" min="9" max="28" value="${s.FontSize || 14}" data-bp-number="FontSize"></label> <label>Прозрачность <input type="number" min="40" max="255" value="${s.Opacity || 230}" data-bp-number="Opacity"></label>` })}
    </div>
    </div>
    <div class="settings-card">
    <h3>Сканирование и данные</h3>
    <div class="limit">Для точной настройки используйте «Настроить зоны на экране»: зоны можно перетаскивать и менять их размер прямо поверх текущего изображения Battle Pass.</div>
    <div class="actions"><button class="action primary" data-command="calibrateBattlePassZones">Настроить зоны на экране</button><button class="action" data-command="previewBattlePassZones">Предпросмотр зон</button><button class="action primary" data-command="scanBattlePass">Считать экран</button><button class="action" data-command="editBattlePassTasks">Проверить и исправить</button><button class="action" data-command="showBattlePassDebug">Открыть отладочный снимок</button><button class="action danger" data-command="clearBattlePass">Очистить данные</button></div>
    <div class="actions"><button class="action" data-command="resetBattlePassOverlayBounds">Сбросить положение и размер оверлея</button></div>
    <div class="update-status">Последнее обновление: ${bp.snapshot?.UpdatedAt ? escapeHtml(new Date(bp.snapshot.UpdatedAt).toLocaleString()) : "ещё не выполнялось"}. Сохранено заданий: ${tasks.length}.</div>
    </div>
  `;
}

function renderBattlePassHotkeys() {
  const keys = [
    ["ToggleBattlePassOverlay", "Показать или скрыть оверлей"],
    ["ScanBattlePass", "Считать экран"],
    ["ToggleBattlePassDescriptions", "Свернуть/развернуть описания"]
  ];
  return `<h2>Отслеживание заданий — горячие клавиши</h2>` + keys.map(([key, label]) => {
    const binding = state.config.Hotkeys[key];
    return field(label, { input: `<div class="actions"><button class="action" data-hotkey="${key}">${displayHotkey(binding)}</button><button class="action" data-hotkey-clear="${key}">Очистить</button></div>${hotkeyRegistrationWarning(key)}` });
  }).join("");
}

function renderProfiles() {
  const selected = selectedProfile();
  const profileOptions = state.config.Profiles.map(profile => `<option value="${profile.Id}" ${profile.Id === state.config.ActiveProfileId ? "selected" : ""}>${escapeHtml(profile.Name)}</option>`).join("");
  const list = state.config.Profiles.map(profile => `
    <button type="button" class="${profile.Id === selected.Id ? "active" : ""}" data-profile-select="${profile.Id}">
      ${escapeHtml(profile.Name)}
    </button>
  `).join("");
  return `
    <h2>Профили</h2>
    ${field("Активный профиль", { input: `<select id="profileSelect" class="profile-select">${profileOptions}</select>` })}
    ${field("Список профилей", { input: `<div class="profile-list">${list}</div>` })}
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
    : status.FileWritable ? "Файл доступен для чтения и записи" : "Укажите путь к файлу DayZ";
  const address = status.Port ? `http://127.0.0.1:${status.Port}` : "—";
  const backups = status.Backups || [];
  const backupList = backups.length
    ? backups.slice(0, 5).map(item => `<span>${escapeHtml(new Date(item.LastWriteTime).toLocaleString())} · ${Math.max(1, Math.round(item.Size / 1024))} КБ</span>`).join("")
    : "Резервных копий пока нет.";
  const dayZWarning = status.DayZRunning
    ? (settings.BlockWritesWhenDayZRunning
      ? "DayZ_x64.exe запущен: импорт будет запрещён настройкой."
      : "DayZ_x64.exe запущен: безопаснее импортировать на экране выбора персонажа или после закрытия игры.")
    : "DayZ не запущен.";
  const actionDisabled = dayZStatusPending ? "disabled" : "";
  const actionFeedback = dayZActionFeedback || "Нажмите «Проверить», чтобы обновить статус файла и API.";
  return `
    <h2>Метки DayZ</h2>
    <div class="settings-card">
    <h3>Подключение и файл</h3>
    ${field("Статус API", { input: `<div class="update-status">${escapeHtml(status.ServiceStatus)}<span>${escapeHtml(address)} · обновляется автоматически</span></div>` })}
    ${field("Последняя операция", { input: `<div class="update-status">${escapeHtml(status.LastOperation || "Операций с метками пока не было.")}</div>` })}
    ${field("Состояние DayZ", { input: `<div class="update-status">${escapeHtml(dayZWarning)}</div>` })}
    ${field("PrivateMarkers.json", { input: `<div class="update-status"><span>${escapeHtml(path)}</span><span>${fileState}</span></div>` })}
    ${field("Действие", { input: `<div class="update-status">${escapeHtml(actionFeedback)}</div>` })}
    <div class="actions">
      <button class="action primary" data-command="selectDayZMarkersFile">Выбрать файл</button>
      <button class="action" data-command="clearDayZMarkersFile" ${actionDisabled}>Искать автоматически</button>
      <button class="action" data-command="refreshDayZStatus" ${actionDisabled}>Проверить</button>
      <button class="action" data-command="openDayZMarkersFolder">Открыть папку</button>
      <button class="action" data-command="openRuntimeLog">Открыть журнал</button>
    </div>
    </div>
    <div class="settings-columns">
    <div class="settings-card">
    <h3>Параметры API</h3>
    ${field("Автоматически выбрать порт", { value: `<input type="checkbox" ${settings.AutoPort ? "checked" : ""} data-dayz-check="AutoPort">`, input: `` })}
    ${field("Порт API", { input: `<input type="number" min="1" max="65535" value="${settings.Port}" ${settings.AutoPort ? "disabled" : ""} data-dayz-number="Port">` })}
    ${field("Разрешить localhost:8000", { value: `<input type="checkbox" ${settings.AllowDevelopmentOrigin ? "checked" : ""} data-dayz-check="AllowDevelopmentOrigin">`, input: `` })}
    ${field("Запрещать запись при запущенном DayZ", { value: `<input type="checkbox" ${settings.BlockWritesWhenDayZRunning ? "checked" : ""} data-dayz-check="BlockWritesWhenDayZRunning">`, input: `` })}
    </div>
    <div class="settings-card">
    <h3>Резервные копии</h3>
    ${field("Максимум резервных копий", { input: `<input type="number" min="1" max="100" value="${settings.BackupLimit}" data-dayz-number="BackupLimit">` })}
    ${field("Хранить backup, дней", { input: `<input type="number" min="1" max="3650" value="${settings.BackupMaxAgeDays}" data-dayz-number="BackupMaxAgeDays">` })}
    ${field("Последние backup", { input: `<div class="update-status">${backupList}</div>` })}
    </div>
    </div>
    <div class="limit">DayZ-Map.ru Companion не аффилирован и не авторизован Bohemia Interactive a.s. DAYZ является товарным знаком Bohemia Interactive a.s.</div>
  `;
}

function renderUpdates() {
  const info = state.update;
  if (!info) {
    return `
      <h2>Обновление</h2>
      ${field("Статус", { input: `<div class="update-status">Проверка еще не выполнялась.</div>` })}
      <div class="actions">
        <button class="action primary" data-command="checkUpdate">Проверить</button>
        <button class="action" data-command="openCompanionReleases">Открыть релизы</button>
      </div>
    `;
  }

  const status = info.ErrorMessage
    ? `Не удалось проверить обновление: ${escapeHtml(info.ErrorMessage)}`
    : info.IsUpdateAvailable
      ? `<strong>Доступна новая версия ${escapeHtml(info.LatestVersion)}</strong>`
      : "Установлена актуальная версия.";
  const published = info.PublishedAt ? new Date(info.PublishedAt).toLocaleDateString() : "";
  const notes = (info.ReleaseNotes || "Описание версии не указано.").trim();
  const downloadDisabled = !info.InstallerUrl && !info.ReleaseUrl ? "disabled" : "";

  return `<div class="updates-layout">
      <h2>Обновление</h2>
      ${field("Статус", { input: `<div class="update-status">${status}<span>Текущая версия: ${escapeHtml(info.CurrentVersion)}</span><span>Последняя версия: ${escapeHtml(info.LatestVersion || "-")}${published ? " от " + escapeHtml(published) : ""}</span></div>` })}
      <div class="update-notes-slot">${field("Описание версии", { input: `<div class="release-notes">${renderMarkdown(notes)}</div>` })}</div>
      <div class="actions">
        <button class="action" data-command="checkUpdate">Проверить снова</button>
        <button class="action primary" data-command="downloadUpdate" ${downloadDisabled}>Скачать установщик</button>
        <button class="action" data-command="openCompanionReleases">Открыть релизы</button>
      </div>
  </div>`;
}

function bindEditorEvents() {
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
    MapId: state.playerPosition?.settings?.MapId || "",
    ProfileId: "",
    MarkerName: document.querySelector("[data-position-name]")?.value || "",
    MarkerType: "player",
    IndicatorShape: document.querySelector("[data-position-shape]")?.value || "triangle",
    MarkerColor: document.querySelector("[data-position-color]")?.value || "#3498db",
    IntervalSeconds: Number(document.querySelector("[data-position-interval]")?.value || 5)
  });
  const savePlayerPositionSettings = (refreshEditor = false) => {
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
  document.querySelectorAll("[data-position-enabled], [data-position-paused]").forEach(input => input.addEventListener("change", () => savePlayerPositionSettings(true)));
  document.querySelector("[data-position-interval]")?.addEventListener("change", savePlayerPositionSettings);
  document.querySelector("[data-position-name]")?.addEventListener("input", debouncePlayerPositionSettings);
  document.querySelector("[data-position-color]")?.addEventListener("input", debouncePlayerPositionSettings);
  document.querySelector("[data-position-shape]")?.addEventListener("change", savePlayerPositionSettings);
  document.querySelectorAll("[data-treasure]").forEach(button => {
    button.addEventListener("click", () => {
      const id = button.dataset.id;
      const action = button.dataset.treasure;
      const x = document.querySelector(`[data-treasure-x="${id}"]`)?.value;
      const z = document.querySelector(`[data-treasure-z="${id}"]`)?.value;
      post({ type: "treasure", action, id, x: Number(x), z: Number(z) });
    });
  });
  document.querySelectorAll("[data-treasure-x], [data-treasure-z]").forEach(input => input.addEventListener("input", () => {
    const id = input.dataset.treasureX || input.dataset.treasureZ;
    clearTimeout(treasureCoordinateTimers.get(id));
    treasureCoordinateTimers.set(id, setTimeout(() => {
      const x = Number(document.querySelector(`[data-treasure-x="${id}"]`)?.value);
      const z = Number(document.querySelector(`[data-treasure-z="${id}"]`)?.value);
      if (Number.isInteger(x) && Number.isInteger(z)) post({ type: "treasure", action: "edit", id, x, z });
    }, 350));
  }));
  document.querySelectorAll("[data-treasure-preview]").forEach(image => image.addEventListener("click", () => { treasurePreviewUri = image.src; render(); }));
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
  if (p.CrosshairShape !== 0) {
    return "";
  }
  if (p.TShape && p.Length === 24 && p.Gap === 8 && p.Thickness === 3 && p.DotEnabled && p.DotSize === 3) {
    return "t";
  }
  if (!p.DotEnabled && !p.TShape && p.CrosshairRotation === 45 && p.Length === 20 && p.Gap === 6 && p.Thickness === 3) {
    return "x";
  }
  if (p.DotEnabled && !p.TShape && p.CrosshairRotation === 0 && p.Length === 10 && p.Gap === 4 && p.Thickness === 2 && p.DotSize === 2 && p.DotShape === 0) {
    return "compact";
  }
  if (p.DotEnabled && !p.TShape && p.CrosshairRotation === 0 && p.Length === 26 && p.Gap === 9 && p.Thickness === 1 && p.DotSize === 2 && p.DotShape === 0) {
    return "thin";
  }
  if (p.DotEnabled && !p.TShape && p.CrosshairRotation === 0 && p.Length === 16 && p.Gap === 5 && p.Thickness === 6 && p.DotSize === 4 && p.DotShape === 0) {
    return "bold";
  }
  if (p.DotEnabled && !p.TShape && p.CrosshairRotation === 0 && p.Length === 22 && p.Gap === 8 && p.Thickness === 2 && p.DotSize === 8 && p.DotShape === 2) {
    return "diamond";
  }
  if (p.DotEnabled && !p.TShape && p.CrosshairRotation === 0 && p.Length === 18 && p.Gap === 8 && p.Thickness === 2 && p.DotSize === 8 && p.DotShape === 5) {
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
  const navGroup = event.target.closest("[data-nav-group]");
  if (navGroup) {
    const id = navGroup.dataset.navGroup;
    if (expandedNavGroups.has(id)) expandedNavGroups.delete(id);
    else expandedNavGroups.add(id);
    render();
    return;
  }
  const tab = event.target.closest("[data-tab]");
  if (tab) {
    activeTab = tab.dataset.tab;
    expandNavForTab(activeTab);
    render();
    if (activeTab === "updates" && !state.update) {
      post({ type: "command", name: "checkUpdate" });
    }
    return;
  }
});

document.getElementById("exitApplication").addEventListener("click", () => post({ type: "command", name: "exitApplication" }));

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
  const settings = state?.playerPosition?.settings;
  if (!settings) return;
  const status = document.querySelector("[data-player-position-status]");
  if (status) {
    const last = Number.isInteger(settings.LastX) ? `X=${settings.LastX}, Y=${settings.LastY}, Z=${settings.LastZ}` : "ещё не распознана";
    const sent = settings.LastSentAt ? new Date(settings.LastSentAt).toLocaleString() : "—";
    status.textContent = `Область: ${settings.HasRegion ? "настроена" : "не выбрана"}. Последняя позиция: ${last}. Последняя отправка: ${sent}. Ошибок подряд: ${settings.ConsecutiveErrors || 0}.`;
  }
  const error = document.querySelector("[data-player-position-error]");
  if (error) {
    const message = settings.LastError || state.playerPosition?.feedback || "";
    error.textContent = message;
    error.classList.toggle("hotkey-warning", Boolean(settings.LastError));
  }
}

window.DayZMapCompanion = {
  receivePreview(preview) {
    if (state) state.preview = preview;
    document.getElementById("preview").src = preview;
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
    if (previous && isEditingValueControl()) {
      state = keepEditedState(previous, next);
      document.getElementById("preview").src = state.preview || "";
      document.getElementById("activeProfileName").textContent = profile()?.Name || "";
      return;
    }
    const needsRender = renderDataChanged(previous, next) || wasDayZStatusPending || !!next.openTab;
    state = next;
    if (next.openTab) {
      activeTab = next.openTab;
      expandNavForTab(activeTab);
    }
    if (needsRender) render();
    else refreshPlayerPositionStatus();
  },
  openTab(tab) {
    activeTab = tab;
    expandNavForTab(activeTab);
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
window.addEventListener("resize", schedulePreviewResize);
post({ type: "ready" });
</script>
</body>
</html>
""";
}
