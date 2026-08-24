using System.Drawing;
using System.Drawing.Imaging;
using System.Diagnostics;
using System.Text.Json;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace CrosshairMarker;

internal sealed class EditorForm : Form
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly AssetStore assetStore = new();
    private readonly UpdateService updateService;
    private readonly WebView2 webView = new();
    private readonly System.Windows.Forms.Timer dayZStatusTimer = new() { Interval = 2500 };
    private AppConfig config;
    private DayZCompanionSettings dayZSettings;
    private DayZCompanionStatus dayZStatus;
    private readonly DayZEventNotifications eventNotifications;
    private UpdateInfo? updateInfo;
    private string? pendingTab;
    private bool webReady;

    public event Action<AppConfig>? ConfigChanged;
    public event Action<string?>? MonitorChanged;
    public event Action<EditorWindowBounds>? EditorBoundsChanged;
    public event Action? ExitRequested;
    public event Action<DayZCompanionSettings>? DayZSettingsChanged;
    public event Action? DayZStatusRequested;

    public EditorForm(AppConfig source, UpdateService updateService, DayZCompanionSettings dayZSettings, DayZCompanionStatus dayZStatus, DayZEventNotifications eventNotifications, string? initialTab = null)
    {
        this.updateService = updateService;
        pendingTab = initialTab;
        config = source.Clone();
        config.Normalize();
        this.dayZSettings = dayZSettings;
        this.dayZSettings.Normalize();
        this.dayZStatus = dayZStatus;
        this.eventNotifications = eventNotifications;

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

    public void ApplyEventNotificationState(DayZEventNotificationSettings settings, bool monitoring)
    {
        if (IsHandleCreated && InvokeRequired)
        {
            BeginInvoke(new Action(() => ApplyEventNotificationState(settings, monitoring)));
            return;
        }
        dayZSettings.EventNotifications = settings;
        _ = SendStateAsync();
    }

    private async Task InitializeWebViewAsync()
    {
        try
        {
            await webView.EnsureCoreWebView2Async();
            webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            webView.CoreWebView2.Settings.AreDevToolsEnabled = true;
            webView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
            webView.CoreWebView2.NavigateToString(EditorHtml);
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
                case "updateDayZSettings":
                    ApplyDayZSettingsFromWeb(root.GetProperty("settings"));
                    break;
                case "command":
                    await HandleCommandAsync(root.GetProperty("name").GetString());
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
            case "exitApplication":
                ExitRequested?.Invoke();
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
            case "connectEventNotifications":
                if (!dayZStatus.Port.HasValue) throw new DayZCompanionException("Локальный API Companion не запущен.");
                Process.Start(new ProcessStartInfo(eventNotifications.BeginPairing(dayZStatus.Port.Value)) { UseShellExecute = true });
                break;
            case "testEventNotifications":
                await eventNotifications.SendTestAsync(CancellationToken.None);
                break;
            case "disconnectEventNotifications":
                await eventNotifications.DisconnectAsync(CancellationToken.None);
                break;
            case "installEventOcr":
                eventNotifications.InstallOcr();
                break;
            case "refreshEventOcr":
                eventNotifications.RefreshOcrStatus();
                break;
            case "previewEventCapture":
                eventNotifications.RefreshCapturePreview();
                break;
            case "clearEventLog":
                eventNotifications.ClearLog();
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
            eventNotifications = new
            {
                settings = eventNotifications.Settings,
                monitoring = eventNotifications.IsMonitoring,
                windows = GameWindows.Find().Select(window => new { window.Title, width = window.Bounds.Width, height = window.Bounds.Height }),
                log = eventNotifications.GetLog(),
                ocr = eventNotifications.GetOcrStatus(),
                capturePreview = eventNotifications.GetCapturePreview()
            },
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

    private static string RenderPreviewDataUri(CrosshairProfile profile)
    {
        using var bitmap = new Bitmap(720, 420, PixelFormat.Format32bppPArgb);
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
  grid-template-columns: 250px minmax(460px, 1fr) minmax(320px, .7fr);
  gap: 12px;
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
  grid-template-rows: auto auto 1fr auto;
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
.editor {
  padding: 14px;
  overflow: auto;
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
.event-log {
  max-height: 280px;
  overflow-y: auto;
  overflow-x: hidden;
  padding-right: 8px;
  align-content: start;
  word-break: break-word;
  scrollbar-color: #6e4c1b #17171a;
}
.update-status strong {
  color: var(--accent);
}
.release-notes {
  max-height: 260px;
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

  <main class="layout">
    <aside class="panel sidebar">
      <select id="profileSelect" class="profile-select"></select>
      <div class="nav" id="nav"></div>
      <div class="limit" id="profileLimit"></div>
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

<script>
const bridge = window.chrome.webview;
let state = null;
let activeTab = "crosshair";
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
  SizeDown: { Enabled: true, Key: "PageDown", Control: true, Alt: true, Shift: false, Win: false }
};

const navigation = [
  { tab: ["dayz", "Синхронизация меток"] },
  { tab: ["events", "Уведомления о событиях"] },
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
    dayZSettings: value.dayZ?.settings,
    eventNotifications: value.eventNotifications,
    monitors: value.monitors,
    preview: value.preview
  });
  return JSON.stringify(stable(previous)) !== JSON.stringify(stable(next));
}

function keepEditedState(previous, next) {
  return {
    ...previous,
    dayZ: next.dayZ ? { ...previous.dayZ, status: next.dayZ.status } : previous.dayZ
  };
}

function renderSidebar() {
  const select = document.getElementById("profileSelect");
  select.innerHTML = state.config.Profiles.map(p => `<option value="${p.Id}" ${p.Id === state.config.ActiveProfileId ? "selected" : ""}>${escapeHtml(p.Name)}</option>`).join("");
  document.getElementById("nav").innerHTML = navigation.map(item => {
    if (item.tab) {
      const [id, label] = item.tab;
      return `<button class="nav-main ${id === activeTab ? "active" : ""}" data-tab="${id}">${label}</button>`;
    }
    const open = expandedNavGroups.has(item.id);
    const children = item.tabs.map(([id, label]) => `<button class="${id === activeTab ? "active" : ""}" data-tab="${id}">${label}</button>`).join("");
    return `<div class="nav-group"><button class="nav-group-toggle" data-nav-group="${item.id}">${item.label}<span>${open ? "▾" : "▸"}</span></button>${open ? `<div class="nav-children">${children}</div>` : ""}</div>`;
  }).join("");
  document.getElementById("profileLimit").textContent = `${state.config.Profiles.length} профилей`;
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
    monitor: renderMonitor(),
    dayz: renderDayZ(),
    events: renderEvents(),
    profiles: renderProfiles(),
    updates: renderUpdates()
  };
  document.getElementById("editor").innerHTML = `<div class="section active">${sections[activeTab]}</div>`;
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
  return `
    <h2>Общие</h2>
    ${field("Запускать вместе с Windows", { value: `<input type="checkbox" ${state.config.StartWithWindows ? "checked" : ""} data-general-check="StartWithWindows">`, input: `` })}
    ${field("Запускать свёрнутым в трей", { value: `<input type="checkbox" ${state.config.StartMinimizedToTray ? "checked" : ""} data-general-check="StartMinimizedToTray">`, input: `` })}
    ${field("Размер окна прицела", { input: `<select data-overlay-size>
      <option value="0" ${state.config.OverlayWindowSize === 0 ? "selected" : ""}>200 × 200 (по умолчанию)</option>
      <option value="1" ${state.config.OverlayWindowSize === 1 ? "selected" : ""}>25% экрана</option>
      <option value="2" ${state.config.OverlayWindowSize === 2 ? "selected" : ""}>50% экрана</option>
      <option value="3" ${state.config.OverlayWindowSize === 3 ? "selected" : ""}>75% экрана</option>
      <option value="4" ${state.config.OverlayWindowSize === 4 ? "selected" : ""}>100% экрана</option>
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
      input: `<div class="actions"><button class="action" data-hotkey="${key}">${displayHotkey(binding)}</button><button class="action" data-hotkey-clear="${key}">Очистить</button></div>`
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

function renderProfiles() {
  const selected = selectedProfile();
  const list = state.config.Profiles.map(profile => `
    <button type="button" class="${profile.Id === selected.Id ? "active" : ""}" data-profile-select="${profile.Id}">
      ${escapeHtml(profile.Name)}
    </button>
  `).join("");
  return `
    <h2>Профили</h2>
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
    ${field("Автоматически выбрать порт", { value: `<input type="checkbox" ${settings.AutoPort ? "checked" : ""} data-dayz-check="AutoPort">`, input: `` })}
    ${field("Порт API", { input: `<input type="number" min="1" max="65535" value="${settings.Port}" ${settings.AutoPort ? "disabled" : ""} data-dayz-number="Port">` })}
    ${field("Разрешить localhost:8000", { value: `<input type="checkbox" ${settings.AllowDevelopmentOrigin ? "checked" : ""} data-dayz-check="AllowDevelopmentOrigin">`, input: `` })}
    ${field("Запрещать запись при запущенном DayZ", { value: `<input type="checkbox" ${settings.BlockWritesWhenDayZRunning ? "checked" : ""} data-dayz-check="BlockWritesWhenDayZRunning">`, input: `` })}
    ${field("Максимум резервных копий", { input: `<input type="number" min="1" max="100" value="${settings.BackupLimit}" data-dayz-number="BackupLimit">` })}
    ${field("Хранить backup, дней", { input: `<input type="number" min="1" max="3650" value="${settings.BackupMaxAgeDays}" data-dayz-number="BackupMaxAgeDays">` })}
    ${field("Последние backup", { input: `<div class="update-status">${backupList}</div>` })}
    <div class="limit">DayZ-Map.ru Companion не аффилирован и не авторизован Bohemia Interactive a.s. DAYZ является товарным знаком Bohemia Interactive a.s.</div>
  `;
}

function renderCaptureZone(label, property, zone) {
  const percent = value => Math.round(Number(value) * 100);
  const input = (name, title) => `<label class="limit">${title}<input type="number" min="0" max="100" step="1" value="${percent(zone[name])}" data-event-zone="${property}" data-event-zone-value="${name}"></label>`;
  return field(label, { input: `<div class="update-status"><div class="actions">${input("X", "Слева, %")}${input("Y", "Сверху, %")}${input("Width", "Ширина, %")}${input("Height", "Высота, %")}</div><span>На предпросмотре зону можно перетаскивать мышью, а её размер — менять за маркер в правом нижнем углу.</span></div>` });
}

function renderCaptureCalibration(preview, settings) {
  if (!preview?.DataUri) return "";
  const zone = (property, label, color) => {
    const value = settings[property];
    const style = `left:${Number(value.X) * 100}%;top:${Number(value.Y) * 100}%;width:${Number(value.Width) * 100}%;height:${Number(value.Height) * 100}%;border-color:${color};`;
    return `<div data-event-calibration-zone="${property}" style="position:absolute;box-sizing:border-box;border:2px solid ${color};background:transparent;cursor:move;touch-action:none;${style}"><span style="position:absolute;left:0;top:0;padding:2px 5px;background:${color};color:#101014;font-size:11px;line-height:14px;white-space:nowrap;pointer-events:none">${label}</span><span data-event-calibration-resize="${property}" title="Изменить размер" style="position:absolute;right:-7px;bottom:-7px;width:14px;height:14px;border:2px solid #101014;border-radius:50%;background:${color};cursor:nwse-resize"></span></div>`;
  };
  return `<div data-event-calibrator style="position:relative;width:100%;margin-top:10px;line-height:0;user-select:none"><img src="${preview.DataUri}" alt="Предпросмотр зон DayZ" draggable="false" style="display:block;width:100%;height:auto;border:1px solid #414146;border-radius:6px">${zone("TopLeftZone", "Верхняя левая", "#ffb547")}${zone("TopCenterZone", "Верхняя центральная", "#55c4ff")}</div>`;
}

function renderEvents() {
  const settings = state.eventNotifications.settings;
  const windows = state.eventNotifications.windows || [];
  const log = state.eventNotifications.log || [];
  const ocr = state.eventNotifications.ocr;
  const capturePreview = state.eventNotifications.capturePreview;
  const isConnected = Boolean(settings.DeviceId && settings.ConnectedUser);
  const connected = isConnected ? `Подключено: ${settings.ConnectedUser}` : "Устройство не подключено";
  const delivery = settings.LastDeliveryAt ? new Date(settings.LastDeliveryAt).toLocaleString() : "Пока нет";
  const error = settings.LastError ? `Ошибка: ${settings.LastError}` : (state.eventNotifications.monitoring ? "Мониторинг запущен" : "Мониторинг выключен");
  return `
    <h2>Уведомления о событиях</h2>
    ${field("Связь DayZ-Map / Discord", { input: `<div class="update-status">${escapeHtml(connected)}<span>${escapeHtml(error)}</span><span>Последняя доставка: ${escapeHtml(delivery)}</span></div>` })}
    ${field("Адрес DayZ-Map API", { input: `<input type="url" value="${escapeHtml(settings.BackendUrl)}" data-event-text="BackendUrl">` })}
    ${field("Tesseract OCR", { input: `<div class="update-status">${escapeHtml(ocr?.Message || "Проверка OCR ещё не выполнена.")}</div><div class="actions"><button class="action primary" data-command="installEventOcr" ${ocr?.Ready ? "disabled" : ""}>Установить Tesseract</button><button class="action" data-command="refreshEventOcr">Проверить снова</button></div>` })}
    ${field("Окно DayZ", { input: `<select data-event-text="WindowTitle"><option value="" ${!settings.WindowTitle ? "selected" : ""}>Автоматически: первое найденное окно</option>${windows.map(window => `<option value="${escapeHtml(window.Title)}" ${window.Title === settings.WindowTitle ? "selected" : ""}>${escapeHtml(window.Title)} (${window.width}×${window.height})</option>`).join("")}</select><div class="limit">Список обновляется при открытии вкладки. Если выбранное окно закрыто, мониторинг остановится с понятной ошибкой.</div>` })}
    ${renderCaptureZone("Зона: верхняя левая", "TopLeftZone", settings.TopLeftZone)}
    ${renderCaptureZone("Зона: верхняя центральная", "TopCenterZone", settings.TopCenterZone)}
    ${field("Предпросмотр зон", { input: `<div class="update-status">${escapeHtml(capturePreview?.Message || "Предпросмотр ещё не создан.")}${renderCaptureCalibration(capturePreview, settings)}</div><div class="actions"><button class="action" data-command="previewEventCapture">Обновить предпросмотр</button><button class="action" data-event-reset-zones>Сбросить зоны</button></div>` })}
    ${field("Мониторить игровые уведомления", { value: `<input type="checkbox" ${settings.Enabled ? "checked" : ""} data-event-check="Enabled">`, input: `` })}
    ${field("Интервал проверки, мс", { input: `<input type="number" min="200" max="2000" value="${settings.PollIntervalMs}" data-event-number="PollIntervalMs">` })}
    ${field("Минимальный повтор, сек", { input: `<input type="number" min="5" max="3600" value="${settings.DuplicateIntervalSeconds}" data-event-number="DuplicateIntervalSeconds">` })}
    ${field("Военный конвой", { value: `<input type="checkbox" ${settings.MilitaryConvoy ? "checked" : ""} data-event-check="MilitaryConvoy">`, input: `` })}
    ${field("Лагерь", { value: `<input type="checkbox" ${settings.Camp ? "checked" : ""} data-event-check="Camp">`, input: `` })}
    ${field("Погрузка", { value: `<input type="checkbox" ${settings.Loading ? "checked" : ""} data-event-check="Loading">`, input: `` })}
    ${field("Зачистка местности", { value: `<input type="checkbox" ${settings.AreaClearance ? "checked" : ""} data-event-check="AreaClearance">`, input: `` })}
    ${field("Изображение", { value: `<input type="checkbox" ${settings.SendFullFrame ? "checked" : ""} data-event-check="SendFullFrame">`, input: `<div class="limit">Включено — отправляется полный кадр окна DayZ; выключено — только область уведомления.</div>` })}
    <div class="actions"><button class="action primary" data-command="connectEventNotifications" ${isConnected ? "disabled" : ""}>${isConnected ? "DayZ-Map подключён" : "Войти через DayZ-Map"}</button><button class="action" data-command="testEventNotifications" ${isConnected ? "" : "disabled"}>Проверить Discord-уведомление</button><button class="action danger" data-command="disconnectEventNotifications" ${isConnected ? "" : "disabled"}>Отключить</button></div>
    ${field("Локальный журнал", { input: `<div class="update-status event-log">${log.length ? log.map(item => `<span>${escapeHtml(new Date(item.At).toLocaleTimeString())} — ${escapeHtml(item.Message)}</span>`).join("") : "Событий в этой сессии пока нет."}</div><div class="actions"><button class="action" data-command="clearEventLog" ${log.length ? "" : "disabled"}>Очистить журнал</button></div>` })}
    <div class="limit">Захват выполняется только из двух зон окна DayZ: верхней левой и верхней центральной. Нужен установленный Tesseract OCR с языками rus и eng. Companion не читает память игры и не эмулирует ввод.</div>
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

  return `
    <h2>Обновление</h2>
    ${field("Статус", { input: `<div class="update-status">${status}<span>Текущая версия: ${escapeHtml(info.CurrentVersion)}</span><span>Последняя версия: ${escapeHtml(info.LatestVersion || "-")}${published ? " от " + escapeHtml(published) : ""}</span></div>` })}
    ${field("Описание версии", { input: `<div class="release-notes">${renderMarkdown(notes)}</div>` })}
    <div class="actions">
      <button class="action" data-command="checkUpdate">Проверить снова</button>
      <button class="action primary" data-command="downloadUpdate" ${downloadDisabled}>Скачать установщик</button>
    </div>
  `;
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
  document.querySelectorAll("[data-event-check]").forEach(input => input.addEventListener("change", () => updateDayZ(settings => settings.EventNotifications[input.dataset.eventCheck] = input.checked)));
  document.querySelectorAll("[data-event-number]").forEach(input => input.addEventListener("change", () => updateDayZ(settings => settings.EventNotifications[input.dataset.eventNumber] = Math.min(Number(input.max), Math.max(Number(input.min), Number(input.value || input.min))))));
  document.querySelectorAll("[data-event-text]").forEach(input => input.addEventListener("change", () => updateDayZ(settings => settings.EventNotifications[input.dataset.eventText] = input.value.trim())));
  document.querySelectorAll("[data-event-zone]").forEach(input => input.addEventListener("change", () => updateDayZ(settings => settings.EventNotifications[input.dataset.eventZone][input.dataset.eventZoneValue] = Math.min(100, Math.max(0, Number(input.value || 0))) / 100)));
  document.querySelectorAll("[data-event-reset-zones]").forEach(button => button.addEventListener("click", () => updateDayZ(settings => {
    settings.EventNotifications.TopLeftZone = { X: .04, Y: .06, Width: .30, Height: .11 };
    settings.EventNotifications.TopCenterZone = { X: .38, Y: .06, Width: .24, Height: .11 };
  })));
  bindCaptureCalibration();
  document.querySelectorAll("[data-overlay-size]").forEach(input => {
    input.addEventListener("change", () => update(config => config.OverlayWindowSize = Number(input.value)));
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

function bindCaptureCalibration() {
  document.querySelectorAll("[data-event-calibrator]").forEach(calibrator => {
    const image = calibrator.querySelector("img");
    const updateZoneDisplay = (property, value) => {
      const element = calibrator.querySelector(`[data-event-calibration-zone="${property}"]`);
      if (element) {
        element.style.left = `${value.X * 100}%`;
        element.style.top = `${value.Y * 100}%`;
        element.style.width = `${value.Width * 100}%`;
        element.style.height = `${value.Height * 100}%`;
      }
      document.querySelectorAll(`[data-event-zone="${property}"]`).forEach(input => {
        input.value = Math.round(Number(value[input.dataset.eventZoneValue]) * 100);
      });
    };
    calibrator.querySelectorAll("[data-event-calibration-zone]").forEach(element => {
      element.addEventListener("pointerdown", event => {
        const property = element.dataset.eventCalibrationZone;
        const resizing = event.target.closest("[data-event-calibration-resize]")?.dataset.eventCalibrationResize === property;
        const initial = clone(state.dayZ.settings.EventNotifications[property]);
        const bounds = () => image.getBoundingClientRect();
        const start = { x: event.clientX, y: event.clientY };
        let current = initial;
        event.preventDefault();
        element.setPointerCapture(event.pointerId);
        const move = moveEvent => {
          const rect = bounds();
          if (!rect.width || !rect.height) return;
          const dx = (moveEvent.clientX - start.x) / rect.width;
          const dy = (moveEvent.clientY - start.y) / rect.height;
          if (resizing) {
            current = {
              X: initial.X,
              Y: initial.Y,
              Width: Math.min(1 - initial.X, Math.max(.05, initial.Width + dx)),
              Height: Math.min(1 - initial.Y, Math.max(.05, initial.Height + dy))
            };
          } else {
            current = {
              X: Math.min(1 - initial.Width, Math.max(0, initial.X + dx)),
              Y: Math.min(1 - initial.Height, Math.max(0, initial.Y + dy)),
              Width: initial.Width,
              Height: initial.Height
            };
          }
          updateZoneDisplay(property, current);
        };
        const finish = () => {
          element.removeEventListener("pointermove", move);
          element.removeEventListener("pointerup", finish);
          element.removeEventListener("pointercancel", finish);
          updateDayZ(settings => settings.EventNotifications[property] = current);
        };
        element.addEventListener("pointermove", move);
        element.addEventListener("pointerup", finish);
        element.addEventListener("pointercancel", finish);
      });
    });
  });
}

function updateDayZ(mutator) {
  const next = clone(state.dayZ.settings);
  mutator(next);
  state.dayZ.settings = next;
  post({ type: "updateDayZSettings", settings: next });
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
  const aliases = { ArrowLeft: "Left", ArrowRight: "Right", ArrowUp: "Up", ArrowDown: "Down", " ": "Space" };
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

document.getElementById("profileSelect").addEventListener("change", event => {
  update(config => config.ActiveProfileId = event.target.value);
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

window.DayZMapCompanion = {
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

post({ type: "ready" });
</script>
</body>
</html>
""";
}
