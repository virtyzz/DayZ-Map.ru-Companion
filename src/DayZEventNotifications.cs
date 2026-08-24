using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CrosshairMarker;

internal sealed class DayZCaptureZone
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public DayZCaptureZone() { }
    public DayZCaptureZone(double x, double y, double width, double height) { X = x; Y = y; Width = width; Height = height; Normalize(); }
    public void Normalize() { X = Math.Clamp(X, 0, .95); Y = Math.Clamp(Y, 0, .95); Width = Math.Clamp(Width, .05, 1 - X); Height = Math.Clamp(Height, .05, 1 - Y); }
    public DayZCaptureZone Clone() => new(X, Y, Width, Height);
    public Rectangle ToScreenRectangle(Rectangle window) => new(window.Left + (int)Math.Round(window.Width * X), window.Top + (int)Math.Round(window.Height * Y), Math.Max(1, (int)Math.Round(window.Width * Width)), Math.Max(1, (int)Math.Round(window.Height * Height)));
}

internal sealed class DayZEventNotificationSettings
{
    public bool Enabled { get; set; }
    public string BackendUrl { get; set; } = "https://dayz-map.ru/profiles-api";
    public string DeviceTokenProtected { get; set; } = "";
    public string ConnectedUser { get; set; } = "";
    public string DeviceId { get; set; } = "";
    public DateTimeOffset? LastDeliveryAt { get; set; }
    public string LastError { get; set; } = "";
    public bool MilitaryConvoy { get; set; } = true;
    public bool Camp { get; set; } = true;
    public bool Loading { get; set; } = true;
    public bool AreaClearance { get; set; } = true;
    public bool SendFullFrame { get; set; }
    public string WindowTitle { get; set; } = "";
    public DayZCaptureZone TopLeftZone { get; set; } = new(.04, .06, .30, .11);
    public DayZCaptureZone TopCenterZone { get; set; } = new(.38, .06, .24, .11);
    public int PollIntervalMs { get; set; } = 350;
    public int DuplicateIntervalSeconds { get; set; } = 15;
    public void Normalize() { PollIntervalMs = Math.Clamp(PollIntervalMs, 200, 2000); DuplicateIntervalSeconds = Math.Clamp(DuplicateIntervalSeconds, 5, 3600); BackendUrl = string.IsNullOrWhiteSpace(BackendUrl) ? "https://dayz-map.ru/profiles-api" : BackendUrl.TrimEnd('/'); WindowTitle ??= ""; TopLeftZone ??= new(.04, .06, .30, .11); TopCenterZone ??= new(.38, .06, .24, .11); if (IsLegacyZone(TopLeftZone, 0, 0, .42, .30) || IsLegacyZone(TopLeftZone, .03, .05, .32, .14)) TopLeftZone = new(.04, .06, .30, .11); if (IsLegacyZone(TopCenterZone, .28, 0, .44, .24) || IsLegacyZone(TopCenterZone, .35, .05, .30, .14)) TopCenterZone = new(.38, .06, .24, .11); TopLeftZone.Normalize(); TopCenterZone.Normalize(); }
    public DayZEventNotificationSettings Clone() => (DayZEventNotificationSettings)MemberwiseClone();
    public void CopyFrom(DayZEventNotificationSettings source) { Enabled = source.Enabled; BackendUrl = source.BackendUrl; DeviceTokenProtected = source.DeviceTokenProtected; ConnectedUser = source.ConnectedUser; DeviceId = source.DeviceId; LastDeliveryAt = source.LastDeliveryAt; LastError = source.LastError; MilitaryConvoy = source.MilitaryConvoy; Camp = source.Camp; Loading = source.Loading; AreaClearance = source.AreaClearance; SendFullFrame = source.SendFullFrame; WindowTitle = source.WindowTitle; TopLeftZone = source.TopLeftZone?.Clone() ?? new(.04, .06, .30, .11); TopCenterZone = source.TopCenterZone?.Clone() ?? new(.38, .06, .24, .11); PollIntervalMs = source.PollIntervalMs; DuplicateIntervalSeconds = source.DuplicateIntervalSeconds; Normalize(); }
    private static bool IsLegacyZone(DayZCaptureZone zone, double x, double y, double width, double height) => Math.Abs(zone.X - x) < .001 && Math.Abs(zone.Y - y) < .001 && Math.Abs(zone.Width - width) < .001 && Math.Abs(zone.Height - height) < .001;
}

internal sealed record DayZEventLogEntry(DateTimeOffset At, string Kind, string Message);
internal sealed record DayZOcrStatus(bool Ready, string Message, string? ExecutablePath = null);
internal sealed record DayZCapturePreview(string? DataUri, string Message);

internal sealed class DayZEventDuplicateGate
{
    private readonly Dictionary<string, DateTimeOffset> accepted = new(StringComparer.Ordinal);
    public bool TryAccept(string type, DateTimeOffset now, TimeSpan interval) { if (accepted.TryGetValue(type, out var previous) && now - previous < interval) return false; accepted[type] = now; return true; }
}

internal sealed class DayZEventNotifications : IDisposable
{
    private readonly DayZEventNotificationSettings settings;
    private readonly HttpClient http;
    private readonly Dictionary<string, DateTimeOffset> recent = new(StringComparer.Ordinal);
    private readonly DayZEventDuplicateGate duplicateGate = new();
    private readonly List<DayZEventLogEntry> log = new();
    private readonly object logLock = new();
    private CancellationTokenSource? monitorCancellation;
    private string? pairingState;
    private ulong? topLeftHash, topCenterHash;
    private DayZOcrStatus? ocrStatus;
    private DayZCapturePreview capturePreview = new(null, "Предпросмотр ещё не создан.");
    public event Action? Changed;
    public DayZEventNotifications(DayZEventNotificationSettings settings) : this(settings, null) { }
    internal DayZEventNotifications(DayZEventNotificationSettings settings, HttpMessageHandler? handler)
    {
        this.settings = settings;
        settings.Normalize();
        http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: true);
        http.Timeout = TimeSpan.FromSeconds(20);
    }
    public DayZEventNotificationSettings Settings => settings;
    public bool IsMonitoring => monitorCancellation is not null;
    public IReadOnlyList<DayZEventLogEntry> GetLog()
    {
        lock (logLock) return log.ToArray();
    }
    public void ClearLog()
    {
        lock (logLock) log.Clear();
        Changed?.Invoke();
    }
    public DayZOcrStatus GetOcrStatus() => ocrStatus ??= DetectOcr();
    public void RefreshOcrStatus() { ocrStatus = null; Changed?.Invoke(); }
    public void InstallOcr()
    {
        try { Process.Start(new ProcessStartInfo("winget.exe", "install --id tesseract-ocr.tesseract --exact --accept-package-agreements --accept-source-agreements") { UseShellExecute = true }); AddLog("info", "Запущена установка официального Tesseract OCR через winget."); }
        catch { Process.Start(new ProcessStartInfo("https://github.com/tesseract-ocr/tesseract/releases/latest") { UseShellExecute = true }); AddLog("info", "Открыт последний официальный релиз Tesseract OCR."); }
        Changed?.Invoke();
    }
    public DayZCapturePreview GetCapturePreview() => capturePreview;
    public void RefreshCapturePreview()
    {
        try
        {
            var windows = GameWindows.Find();
            if (windows.Count == 0) throw new DayZCompanionException("Окно DayZ не найдено.");
            var selected = string.IsNullOrWhiteSpace(settings.WindowTitle) ? windows[0] : windows.FirstOrDefault(item => string.Equals(item.Title, settings.WindowTitle, StringComparison.Ordinal));
            if (selected is null) throw new DayZCompanionException("Выбранное окно DayZ не найдено.");
            using var image = CaptureWindow(selected) ?? throw new DayZCompanionException("Windows не смогла получить кадр окна DayZ.");
            using (var graphics = Graphics.FromImage(image))
            {
                DrawZone(graphics, settings.TopLeftZone.ToScreenRectangle(selected.Bounds), selected.Bounds, Color.FromArgb(255, 181, 71), "Верхняя левая");
                DrawZone(graphics, settings.TopCenterZone.ToScreenRectangle(selected.Bounds), selected.Bounds, Color.FromArgb(85, 196, 255), "Верхняя центральная");
            }
            capturePreview = new("data:image/png;base64," + Convert.ToBase64String(Png(image)), "Границы зон наложены на текущий кадр окна DayZ.");
        }
        catch (Exception ex) { capturePreview = new(null, ex.Message); }
        Changed?.Invoke();
    }
    public void Start()
    {
        if (!settings.Enabled || string.IsNullOrEmpty(Token()) || IsMonitoring) return;
        monitorCancellation = new CancellationTokenSource(); _ = MonitorAsync(monitorCancellation.Token);
        AddLog("info", "Мониторинг игровых уведомлений запущен."); AppRuntimeLog.Info("DayZ event notification monitoring started."); Changed?.Invoke();
    }
    public void Stop() { monitorCancellation?.Cancel(); monitorCancellation?.Dispose(); monitorCancellation = null; Changed?.Invoke(); }
    public string BeginPairing(int port)
    {
        pairingState = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var api = new Uri(settings.BackendUrl); var site = new Uri(api.GetLeftPart(UriPartial.Authority));
        var callback = Uri.EscapeDataString($"http://127.0.0.1:{port}/companion/callback");
        return new Uri(site, $"companion-connect.html?callback={callback}&state={pairingState}").ToString();
    }
    public async Task CompletePairingAsync(string code, string state, CancellationToken cancellationToken)
    {
        var expected = Interlocked.Exchange(ref pairingState, null);
        if (string.IsNullOrWhiteSpace(expected) || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(state))) throw new DayZCompanionException("Состояние привязки не совпадает. Повторите вход.");
        using var request = new HttpRequestMessage(HttpMethod.Post, settings.BackendUrl + "/companion/pairings/consume")
        {
            Content = new StringContent(JsonSerializer.Serialize(new { code }), Encoding.UTF8, "application/json")
        };
        // A dev backend rebuild may invalidate a pooled HTTPS connection. Pairing
        // is rare, so a fresh connection is preferable to a misleading timeout.
        request.Headers.ConnectionClose = true;
        HttpResponseMessage response;
        try { response = await http.SendAsync(request, cancellationToken); }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested) { throw new DayZCompanionException("DayZ-Map API не ответил вовремя. Проверьте доступность dev API и повторите вход."); }
        using (response)
        {
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode) throw new DayZCompanionException("Привязка отклонена: " + Problem(body));
        using var json = JsonDocument.Parse(body); var token = json.RootElement.GetProperty("token").GetString();
        if (string.IsNullOrWhiteSpace(token)) throw new DayZCompanionException("Сервер не вернул токен устройства.");
        settings.DeviceTokenProtected = Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(token), null, DataProtectionScope.CurrentUser));
        settings.DeviceId = json.RootElement.GetProperty("device_id").GetString() ?? ""; settings.ConnectedUser = json.RootElement.GetProperty("display_name").GetString() ?? ""; settings.LastError = ""; AddLog("success", "DayZ-Map устройство подключено.");
        Changed?.Invoke(); Start();
        }
    }
    public async Task SendTestAsync(CancellationToken ct)
    {
        using var request = Authorized(HttpMethod.Post, "companion/test"); using var response = await http.SendAsync(request, ct);
        HandleServerRevocation(response);
        if (!response.IsSuccessStatusCode) throw new DayZCompanionException("Тест не доставлен: " + Problem(await response.Content.ReadAsStringAsync(ct)));
        settings.LastDeliveryAt = DateTimeOffset.Now; settings.LastError = ""; AddLog("success", "Тестовое Discord-уведомление доставлено."); Changed?.Invoke();
    }
    public async Task DisconnectAsync(CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(Token()))
        {
            using var request = Authorized(HttpMethod.Delete, "companion/devices/current");
            using var response = await http.SendAsync(request, ct);
            if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
            {
                HandleServerRevocation(response);
                return;
            }
            if (!response.IsSuccessStatusCode) throw new DayZCompanionException("Не удалось отключить устройство: " + Problem(await response.Content.ReadAsStringAsync(ct)));
        }
        ClearLocalConnection();
        AddLog("info", "Устройство DayZ-Map отключено и отозвано на сервере.");
        Changed?.Invoke();
    }
    private async Task MonitorAsync(CancellationToken ct)
    {
        try { while (!ct.IsCancellationRequested) { try { await MonitorOnceAsync(ct); } catch (Exception ex) when (ex is not OperationCanceledException) { if (!string.Equals(settings.LastError, ex.Message, StringComparison.Ordinal)) AddLog("error", ex.Message); settings.LastError = ex.Message; AppRuntimeLog.Error("DayZ event monitor error", ex); Changed?.Invoke(); } await Task.Delay(settings.PollIntervalMs, ct); } } catch (OperationCanceledException) { } finally { monitorCancellation = null; Changed?.Invoke(); }
    }
    private async Task MonitorOnceAsync(CancellationToken ct)
    {
        var windows = GameWindows.Find();
        if (windows.Count == 0) throw new DayZCompanionException("Окно DayZ не найдено.");
        var selected = string.IsNullOrWhiteSpace(settings.WindowTitle) ? windows[0] : windows.FirstOrDefault(item => string.Equals(item.Title, settings.WindowTitle, StringComparison.Ordinal));
        if (selected is null) throw new DayZCompanionException("Выбранное окно DayZ не найдено. Выберите его снова.");
        using var frame = CaptureWindow(selected);
        if (frame is null) throw new DayZCompanionException("Windows не смогла получить кадр окна DayZ.");
        var window = selected.Bounds;
        foreach (var (screenZone, first) in new[] { (settings.TopLeftZone.ToScreenRectangle(window), true), (settings.TopCenterZone.ToScreenRectangle(window), false) })
        {
            var zone = new Rectangle(screenZone.Left - window.Left, screenZone.Top - window.Top, screenZone.Width, screenZone.Height);
            using var image = Crop(frame, zone); var hash = Hash(image);
            if ((first ? topLeftHash : topCenterHash) == hash) continue; if (first) topLeftHash = hash; else topCenterHash = hash;
            var text = await OcrAsync(image); var type = Classify(text); if (type is null) { LogUnrecognizedOcr(text); continue; } if (!Enabled(type)) { AddLog("filtered", $"{EventName(type)}: отключено в настройках."); continue; }
            if (!duplicateGate.TryAccept(type, DateTimeOffset.Now, TimeSpan.FromSeconds(settings.DuplicateIntervalSeconds))) { AddLog("filtered", $"{EventName(type)}: повтор отфильтрован."); continue; } AddLog("detected", $"Распознано: {EventName(type)}.");
            await SendEventAsync(type, CleanEventText(type, text), settings.SendFullFrame ? (Bitmap)frame.Clone() : (Bitmap)image.Clone(), ct);
        }
    }
    internal async Task SendEventAsync(string type, string text, Bitmap image, CancellationToken ct)
    {
        using (image)
        using (var request = Authorized(HttpMethod.Post, "companion/events"))
        {
            var form = new MultipartFormDataContent { { new StringContent(type), "event_type" }, { new StringContent(text), "text" }, { new StringContent(DateTimeOffset.UtcNow.ToString("O")), "detected_at" } };
            var file = new ByteArrayContent(Png(image)); file.Headers.ContentType = new MediaTypeHeaderValue("image/png"); form.Add(file, "image", "dayz-event.png"); request.Content = form;
            using var response = await http.SendAsync(request, ct); HandleServerRevocation(response); if (!response.IsSuccessStatusCode) throw new DayZCompanionException("Событие не доставлено: " + Problem(await response.Content.ReadAsStringAsync(ct)));
        }
        settings.LastDeliveryAt = DateTimeOffset.Now; settings.LastError = ""; AddLog("success", $"{EventName(type)}: Discord-уведомление доставлено."); Changed?.Invoke();
    }
    private HttpRequestMessage Authorized(HttpMethod method, string path) { var token = Token(); if (string.IsNullOrEmpty(token)) throw new DayZCompanionException("Companion не привязан к DayZ-Map."); var request = new HttpRequestMessage(method, settings.BackendUrl + "/" + path); request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token); return request; }
    private void HandleServerRevocation(HttpResponseMessage response)
    {
        if (response.StatusCode is not (System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)) return;
        ClearLocalConnection();
        settings.LastError = "Сессия Companion отозвана сервером. Подключите устройство снова.";
        AddLog("error", settings.LastError);
        Changed?.Invoke();
    }
    private void ClearLocalConnection()
    {
        Stop();
        settings.DeviceTokenProtected = settings.DeviceId = settings.ConnectedUser = "";
        settings.LastDeliveryAt = null;
        settings.LastError = "";
    }
    private string Token() { try { return string.IsNullOrEmpty(settings.DeviceTokenProtected) ? "" : Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(settings.DeviceTokenProtected), null, DataProtectionScope.CurrentUser)); } catch { return ""; } }
    private bool Enabled(string type) => type switch { "military_convoy" => settings.MilitaryConvoy, "camp" => settings.Camp, "loading" => settings.Loading, "area_clearance" => settings.AreaClearance, _ => false };
    private static string EventName(string type) => type switch { "military_convoy" => "Военный конвой", "camp" => "Лагерь", "loading" => "Погрузка", "area_clearance" => "Зачистка местности", _ => type };
    internal static string? Classify(string text) { var value = Normalize(text); if (value.Contains("военн") && value.Contains("конво")) return "military_convoy"; if (value.Contains("лагер")) return "camp"; if (value.Contains("погруз")) return "loading"; return value.Contains("зачист") && value.Contains("местност") ? "area_clearance" : null; }
    internal static string CleanEventText(string type, string text)
    {
        var lines = text.Replace('\r', '\n').Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var title = EventName(type);
        var start = Array.FindIndex(lines, line => line.Contains(title, StringComparison.OrdinalIgnoreCase));
        if (start < 0) return text.Trim();

        // OCR often puts the game's HUD and the notification on one line. Keep
        // the notification from its title onward, but remove a title-only line.
        lines[start] = lines[start][lines[start].IndexOf(title, StringComparison.OrdinalIgnoreCase)..];
        var body = lines.Skip(start).Where(line => !string.Equals(Normalize(line), Normalize(title), StringComparison.Ordinal));
        var result = string.Join(Environment.NewLine, body).Trim();
        return string.IsNullOrWhiteSpace(result) ? title : result;
    }
    internal static string Normalize(string text) => string.Join(' ', new string(text.ToLowerInvariant().Where(c => char.IsLetterOrDigit(c) || char.IsWhiteSpace(c)).ToArray()).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    private async Task<string> OcrAsync(Bitmap image)
    {
        var status = GetOcrStatus();
        // The installer can replace Tesseract while Companion is running. Do not
        // keep using a cached path that no longer exists after that update.
        if (!IsOcrExecutableAvailable(status))
        {
            RefreshOcrStatus();
            status = GetOcrStatus();
        }

        if (!status.Ready || !IsOcrExecutableAvailable(status)) throw new DayZCompanionException(status.Message);
        var path = Path.Combine(Path.GetTempPath(), "dayz-ocr-" + Guid.NewGuid().ToString("N") + ".png");
        try
        {
            using var prepared = PrepareForOcr(image);
            await File.WriteAllBytesAsync(path, Png(prepared));
            var start = new ProcessStartInfo(status.ExecutablePath!, $"\"{path}\" stdout -l rus+eng --psm 6 --dpi 192") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8, CreateNoWindow = true };
            using var process = Process.Start(start) ?? throw new DayZCompanionException("Не удалось запустить OCR.");
            var output = await process.StandardOutput.ReadToEndAsync();
            var error = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            if (process.ExitCode != 0) throw new DayZCompanionException("OCR недоступен: " + error.Trim());
            return output;
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    private static bool IsOcrExecutableAvailable(DayZOcrStatus status) =>
        status.Ready && !string.IsNullOrWhiteSpace(status.ExecutablePath) &&
        (string.Equals(status.ExecutablePath, "tesseract.exe", StringComparison.OrdinalIgnoreCase) || File.Exists(status.ExecutablePath));
    private static DayZOcrStatus DetectOcr() { var candidates = new[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Tesseract-OCR", "tesseract.exe"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Tesseract-OCR", "tesseract.exe"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Tesseract-OCR", "tesseract.exe"), "tesseract.exe" }; foreach (var executable in candidates.Where(item => item == "tesseract.exe" || File.Exists(item))) { try { var start = new ProcessStartInfo(executable, "--list-langs") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true }; using var process = Process.Start(start); if (process is null || !process.WaitForExit(5000) || process.ExitCode != 0) continue; var languages = process.StandardOutput.ReadToEnd().Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries); return languages.Contains("rus") && languages.Contains("eng") ? new(true, "Tesseract OCR готов: rus и eng найдены.", executable) : new(false, "В Tesseract должны быть установлены языки rus и eng.", executable); } catch (System.ComponentModel.Win32Exception) { } } return new(false, "Tesseract OCR не установлен."); }
    private static Bitmap Capture(Rectangle bounds) { var image = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb); using var g = Graphics.FromImage(image); g.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size); return image; }
    private static Bitmap? CaptureWindow(GameWindow window) { var image = new Bitmap(window.Bounds.Width, window.Bounds.Height, PixelFormat.Format32bppArgb); using var graphics = Graphics.FromImage(image); var hdc = graphics.GetHdc(); try { if (!GameWindows.Print(window.Handle, hdc)) { image.Dispose(); return null; } } finally { graphics.ReleaseHdc(hdc); } return image; }
    private static Bitmap Crop(Bitmap source, Rectangle bounds) { var image = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb); using var graphics = Graphics.FromImage(image); graphics.DrawImage(source, new Rectangle(0, 0, bounds.Width, bounds.Height), bounds, GraphicsUnit.Pixel); return image; }
    private static Bitmap PrepareForOcr(Bitmap source) { var image = new Bitmap(source.Width * 2, source.Height * 2, PixelFormat.Format24bppRgb); using var graphics = Graphics.FromImage(image); graphics.InterpolationMode = InterpolationMode.HighQualityBicubic; graphics.PixelOffsetMode = PixelOffsetMode.HighQuality; graphics.CompositingQuality = CompositingQuality.HighQuality; graphics.DrawImage(source, new Rectangle(0, 0, image.Width, image.Height)); return image; }
    private static void DrawZone(Graphics graphics, Rectangle screenZone, Rectangle window, Color color, string label) { var zone = new Rectangle(screenZone.Left - window.Left, screenZone.Top - window.Top, screenZone.Width, screenZone.Height); using var pen = new Pen(color, 4); using var brush = new SolidBrush(Color.FromArgb(190, color)); using var font = new Font(FontFamily.GenericSansSerif, 18, FontStyle.Bold); graphics.DrawRectangle(pen, zone); graphics.FillRectangle(brush, zone.Left, zone.Top, Math.Min(zone.Width, graphics.MeasureString(label, font).Width + 16), 34); graphics.DrawString(label, font, Brushes.Black, zone.Left + 8, zone.Top + 6); }
    private static byte[] Png(Bitmap image) { using var stream = new MemoryStream(); image.Save(stream, ImageFormat.Png); return stream.ToArray(); }
    private static ulong Hash(Bitmap source) { using var image = new Bitmap(8, 8); using (var g = Graphics.FromImage(image)) g.DrawImage(source, 0, 0, 8, 8); var values = new byte[64]; var sum = 0; for (var y = 0; y < 8; y++) for (var x = 0; x < 8; x++) { var c = image.GetPixel(x, y); values[y * 8 + x] = (byte)((c.R * 299 + c.G * 587 + c.B * 114) / 1000); sum += values[y * 8 + x]; } ulong hash = 0; for (var i = 0; i < 64; i++) if (values[i] >= sum / 64) hash |= 1UL << i; return hash; }
    private static string Problem(string body) { try { using var json = JsonDocument.Parse(body); return json.RootElement.TryGetProperty("detail", out var value) ? value.GetString() ?? "Ошибка сервера" : "Ошибка сервера"; } catch { return "Ошибка сервера"; } }
    private void LogUnrecognizedOcr(string text) { var normalized = Normalize(text); if (string.IsNullOrEmpty(normalized) || !new[] { "военн", "конво", "лагер", "погруз", "зачист", "местност" }.Any(normalized.Contains)) return; var key = "ocr:" + normalized; if (recent.TryGetValue(key, out var at) && DateTimeOffset.Now - at < TimeSpan.FromSeconds(15)) return; recent[key] = DateTimeOffset.Now; var shown = text.Trim().Replace('\r', ' ').Replace('\n', ' '); if (shown.Length > 180) shown = shown[..180] + "…"; AddLog("ocr", "OCR: похожее на событие, но нераспознанное: " + shown); }
    private void AddLog(string kind, string message) { lock (logLock) { log.Insert(0, new DayZEventLogEntry(DateTimeOffset.Now, kind, message)); if (log.Count > 100) log.RemoveRange(100, log.Count - 100); } }
    public void Dispose() { Stop(); http.Dispose(); }
}

internal sealed record GameWindow(nint Handle, string Title, Rectangle Bounds);

internal static class GameWindows
{
    private delegate bool Callback(nint window, nint parameter); [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool EnumWindows(Callback callback, nint parameter); [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window); [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool PrintWindow(nint window, nint hdc, uint flags); [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] private static extern int GetWindowText(nint window, StringBuilder text, int max); [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out Rect rect); [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint processId); [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    public static List<GameWindow> Find() { var result = new List<GameWindow>(); EnumWindows((handle, _) => { if (!IsWindowVisible(handle) || !IsDayZProcess(handle)) return true; var text = new StringBuilder(260); if (GetWindowText(handle, text, text.Capacity) > 0 && GetWindowRect(handle, out var r)) { var bounds = Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom); if (bounds.Width > 100 && bounds.Height > 100) result.Add(new GameWindow(handle, text.ToString(), bounds)); } return true; }, 0); return result; }
    public static bool Print(nint window, nint hdc) => PrintWindow(window, hdc, 2);
    private static bool IsDayZProcess(nint window) { try { GetWindowThreadProcessId(window, out var processId); if (processId == 0) return false; var name = Process.GetProcessById((int)processId).ProcessName; return string.Equals(name, "DayZ", StringComparison.OrdinalIgnoreCase) || string.Equals(name, "DayZ_x64", StringComparison.OrdinalIgnoreCase); } catch (ArgumentException) { return false; } catch (System.ComponentModel.Win32Exception) { return false; } }
}
