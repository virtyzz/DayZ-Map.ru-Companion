using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CrosshairMarker;

internal sealed class PlayerPositionCaptureService
{
    private static readonly Regex StrictPosition = new(@"^\s*(?:x\s*[:=]?\s*)?(?<x>\d{1,6})\s+\s*(?:y\s*[:=]?\s*)?(?<y>\d{1,6})\s+\s*(?:z\s*[:=]?\s*)?(?<z>\d{1,6})\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private string? tesseractExecutable;
    private readonly object diagnosticsSync = new();
    private readonly string diagnosticsDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DayZMarkerCompanion", "player-position-diagnostics");
    private PlayerPositionDiagnostics? diagnostics;
    private DateTimeOffset lastDiagnosticsSavedAt;

    public string DiagnosticsDirectory => diagnosticsDirectory;
    public PlayerPositionDiagnostics? GetDiagnostics() { lock (diagnosticsSync) return diagnostics; }

    public async Task<PlayerPositionOcrResult> TestAsync(PlayerPositionTrackingSettings settings, TreasureMapOption? map)
    {
        if (!settings.HasRegion) return new PlayerPositionOcrResult("", null, "Сначала выделите область HUD с X Y Z.");
        var bounds = SystemInformation.VirtualScreen;
        var region = settings.GetScreenRectangle(bounds);
        if (region.Width <= 0 || region.Height <= 0) return new PlayerPositionOcrResult("", null, "Сохранённая область вне текущих экранов.");
        using var image = new Bitmap(region.Width, region.Height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(image)) graphics.CopyFromScreen(region.Location, Point.Empty, region.Size);
        var executable = GetTesseractExecutable();
        if (string.IsNullOrWhiteSpace(executable)) return new PlayerPositionOcrResult("", null, "Tesseract с языками rus и eng не найден.");
        return await RecognizeAsync(executable, image, map, CancellationToken.None, saveDiagnostics: true);
    }

    public async Task<PlayerPositionOcrResult> CaptureAndRecognizeAsync(PlayerPositionTrackingSettings settings, TreasureMapOption? map, CancellationToken cancellationToken)
    {
        if (!settings.HasRegion) return new PlayerPositionOcrResult("", null, "Не настроена область координат.");
        var bounds = SystemInformation.VirtualScreen;
        var region = settings.GetScreenRectangle(bounds);
        if (region.Width <= 0 || region.Height <= 0) return new PlayerPositionOcrResult("", null, "Область координат недоступна на текущем экране.");
        using var image = new Bitmap(region.Width, region.Height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(image)) graphics.CopyFromScreen(region.Location, Point.Empty, region.Size);
        var executable = GetTesseractExecutable();
        if (string.IsNullOrWhiteSpace(executable)) return new PlayerPositionOcrResult("", null, "Tesseract с языками rus и eng не найден.");
        return await RecognizeAsync(executable, image, map, cancellationToken, saveDiagnostics: false);
    }

    private async Task<PlayerPositionOcrResult> RecognizeAsync(string executable, Bitmap image, TreasureMapOption? map, CancellationToken cancellationToken, bool saveDiagnostics)
    {
        var temporary = Path.Combine(Path.GetTempPath(), "dayz-position-" + Guid.NewGuid().ToString("N") + ".png");
        var fallbackTemporary = Path.Combine(Path.GetTempPath(), "dayz-position-fallback-" + Guid.NewGuid().ToString("N") + ".png");
        try
        {
            using var prepared = PrepareHighContrast(image);
            prepared.Save(temporary, ImageFormat.Png);
            var raw = await TesseractOcr.RunAsync(executable, temporary, cancellationToken);
            var result = Parse(raw, map);
            if (result.Position is null)
            {
                using var fallback = PrepareFallback(image);
                fallback.Save(fallbackTemporary, ImageFormat.Png);
                var fallbackRaw = await TesseractOcr.RunAsync(executable, fallbackTemporary, cancellationToken);
                var fallbackResult = Parse(fallbackRaw, map);
                result = fallbackResult.Position is not null
                    ? fallbackResult
                    : new PlayerPositionOcrResult($"contrast: {raw.Trim()} | fallback: {fallbackRaw.Trim()}", null, fallbackResult.Message);
            }
            SaveDiagnosticsIfNeeded(image, prepared, result, saveDiagnostics);
            return result;
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
            if (File.Exists(fallbackTemporary)) File.Delete(fallbackTemporary);
        }
    }

    private string? GetTesseractExecutable()
    {
        if (!string.IsNullOrWhiteSpace(tesseractExecutable)) return tesseractExecutable;
        var status = TesseractOcr.Detect();
        if (status.Ready) tesseractExecutable = status.ExecutablePath;
        return tesseractExecutable;
    }

    internal static PlayerPositionOcrResult Parse(string rawText, TreasureMapOption? map)
    {
        var text = string.Join(' ', rawText.Replace('\r', ' ').Replace('\n', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries));
        // Tesseract often treats the HUD border under the last digit as a
        // trailing full stop (for example: "3032 344 7476."). It is not part
        // of a coordinate and should not invalidate an otherwise exact triple.
        text = text.TrimEnd('.', ',', ';', ':');
        var match = StrictPosition.Match(text);
        if (!match.Success) return new PlayerPositionOcrResult(text, null, "Ожидались ровно три целых числа X Y Z.");
        var x = int.Parse(match.Groups["x"].Value); var y = int.Parse(match.Groups["y"].Value); var z = int.Parse(match.Groups["z"].Value);
        if (map is not null && map.Width > 0 && map.Height > 0 && (x < 0 || x > map.Width || z < 0 || z > map.Height))
            return new PlayerPositionOcrResult(text, null, $"Координаты вне карты: X 0–{map.Width}, Z 0–{map.Height}.");
        return new PlayerPositionOcrResult(text, new PlayerPosition(x, y, z, DateTimeOffset.UtcNow), "Координаты распознаны.");
    }

    private static Bitmap PrepareFallback(Bitmap source)
    {
        var output = new Bitmap(source.Width * 3, source.Height * 3, PixelFormat.Format24bppRgb);
        using var graphics = Graphics.FromImage(output);
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.DrawImage(source, new Rectangle(Point.Empty, output.Size));
        return output;
    }

    private static Bitmap PrepareHighContrast(Bitmap source)
    {
        const int scale = 4;
        var output = new Bitmap(source.Width * scale, source.Height * scale, PixelFormat.Format24bppRgb);
        using var graphics = Graphics.FromImage(output);
        graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
        graphics.PixelOffsetMode = PixelOffsetMode.Half;
        graphics.DrawImage(source, new Rectangle(Point.Empty, output.Size));
        for (var y = 0; y < output.Height; y++)
        for (var x = 0; x < output.Width; x++)
        {
            var pixel = output.GetPixel(x, y);
            var maximum = Math.Max(pixel.R, Math.Max(pixel.G, pixel.B));
            var minimum = Math.Min(pixel.R, Math.Min(pixel.G, pixel.B));
            var luminance = (pixel.R * 299 + pixel.G * 587 + pixel.B * 114) / 1000;
            // DayZ HUD coordinates are near-white and nearly neutral. This
            // removes terrain, roads and coloured map details before OCR.
            output.SetPixel(x, y, luminance >= 165 && maximum - minimum <= 95 ? Color.White : Color.Black);
        }
        return output;
    }

    private void SaveDiagnosticsIfNeeded(Bitmap source, Bitmap prepared, PlayerPositionOcrResult result, bool forced)
    {
        var now = DateTimeOffset.Now;
        lock (diagnosticsSync)
        {
            if (!forced && result.Position is not null) return;
            if (!forced && now - lastDiagnosticsSavedAt < TimeSpan.FromSeconds(10)) return;
            Directory.CreateDirectory(diagnosticsDirectory);
            var sourcePath = Path.Combine(diagnosticsDirectory, "last-source.png");
            var processedPath = Path.Combine(diagnosticsDirectory, "last-processed.png");
            source.Save(sourcePath, ImageFormat.Png);
            prepared.Save(processedPath, ImageFormat.Png);
            diagnostics = new PlayerPositionDiagnostics(now, result.RawText, result.Message, result.Position is not null, sourcePath, processedPath);
            File.WriteAllText(Path.Combine(diagnosticsDirectory, "last-result.json"), JsonSerializer.Serialize(diagnostics, new JsonSerializerOptions { WriteIndented = true }));
            lastDiagnosticsSavedAt = now;
        }
    }
}
