using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Text;
using System.Text.RegularExpressions;

namespace CrosshairMarker;

internal sealed class BattlePassTracker
{
    private static readonly Regex ProgressPattern = new(@"(?<!\d)(\d{1,3})\s*/\s*(\d{1,3})(?!\d)", RegexOptions.Compiled);
    private readonly BattlePassStore store;

    public BattlePassTracker(BattlePassStore store) => this.store = store;

    public async Task<BattlePassScanResult> ScanAsync(BattlePassPage page, BattlePassSettings settings, CancellationToken ct = default)
    {
        var ocr = FindTesseract();
        if (ocr is null) return new(false, "Tesseract OCR с языками rus и eng не найден.", 0);

        var screen = MonitorInfo.ResolveScreen(settings.MonitorDeviceName);
        var layout = settings.OcrLayout ?? BattlePassOcrLayout.FromLegacy(settings);
        using var screenshot = Capture(screen.Bounds);
        if (settings.SaveDebugScreenshot)
        {
            using var debug = (Bitmap)screenshot.Clone();
            DrawDebugZones(debug, settings);
            store.SaveDebugScreenshot(debug);
        }
        var tasks = new List<BattlePassTask>();
        const int slots = 5;
        for (var slot = 0; slot < slots; slot++)
        {
            ct.ThrowIfCancellationRequested();
            using var titleImage = CropRelative(screenshot, Offset(layout.Title, slot, layout.RowStep));
            using var descriptionImage = CropRelative(screenshot, Offset(layout.Description, slot, layout.RowStep));
            using var progressImage = CropRelative(screenshot, Offset(layout.Progress, slot, layout.RowStep));
            using var xpImage = CropRelative(screenshot, Offset(layout.Experience, slot, layout.RowStep));
            using var statusImage = CropRelative(screenshot, Offset(layout.Status, slot, layout.RowStep));
            var title = Clean(await RunOcrAsync(ocr, titleImage, 7, ct));
            var description = Clean(await RunOcrAsync(ocr, descriptionImage, 6, ct));
            var progress = ProgressPattern.Match(await RunOcrAsync(ocr, progressImage, 7, ct));
            var xp = Regex.Match(await RunOcrAsync(ocr, xpImage, 7, ct), @"\d+");
            var status = Clean(await RunOcrAsync(ocr, statusImage, 7, ct));
            if (!progress.Success && string.IsNullOrWhiteSpace(title)) continue;
            var current = progress.Success ? int.Parse(progress.Groups[1].Value) : 0;
            var target = progress.Success ? int.Parse(progress.Groups[2].Value) : 0;
            tasks.Add(new BattlePassTask { Page = page, Slot = slot, Title = string.IsNullOrWhiteSpace(title) ? "Задание не распознано" : title, Description = description, ExperienceReward = xp.Success ? int.Parse(xp.Value) : null, Current = current, Target = target, Completed = status.Contains("ВЫПОЛ", StringComparison.OrdinalIgnoreCase) || (target > 0 && current >= target), UpdatedAt = DateTimeOffset.Now });
        }

        if (tasks.Count == 0) return new(false, "Задания не найдены. Откройте вкладку Battle Pass и проверьте выбранный монитор.", 0);
        var snapshot = store.LoadSnapshot();
        var previous = snapshot.Tasks.Where(task => task.Page == page).ToDictionary(task => task.Slot);
        foreach (var task in tasks)
        {
            if (previous.TryGetValue(task.Slot, out var old))
            {
                task.DescriptionExpanded = old.DescriptionExpanded;
                task.Pinned = old.Pinned;
            }
        }
        snapshot.Tasks.RemoveAll(task => task.Page == page);
        snapshot.Tasks.AddRange(tasks);
        snapshot.UpdatedAt = DateTimeOffset.Now;
        store.SaveSnapshot(snapshot);
        return new(true, $"Считано заданий: {tasks.Count}.", tasks.Count);
    }

    public void CreateZonesPreview(BattlePassSettings settings)
    {
        var screen = MonitorInfo.ResolveScreen(settings.MonitorDeviceName);
        using var screenshot = Capture(screen.Bounds);
        DrawDebugZones(screenshot, settings);
        store.SaveDebugScreenshot(screenshot);
    }

    public Bitmap CaptureCalibrationImage(BattlePassSettings settings)
    {
        var screen = MonitorInfo.ResolveScreen(settings.MonitorDeviceName);
        return Capture(screen.Bounds);
    }

    private static void DrawDebugZones(Bitmap image, BattlePassSettings settings)
    {
        using var graphics = Graphics.FromImage(image);
        using var titlePen = new Pen(Color.Lime, 3);
        using var descriptionPen = new Pen(Color.Cyan, 3);
        using var progressPen = new Pen(Color.Orange, 3);
        using var xpPen = new Pen(Color.MediumPurple, 3);
        using var statusPen = new Pen(Color.Red, 3);
        using var font = new Font(FontFamily.GenericSansSerif, Math.Max(14, image.Width / 90), FontStyle.Bold);
        for (var slot = 0; slot < 5; slot++)
        {
            var layout = settings.OcrLayout ?? BattlePassOcrLayout.FromLegacy(settings);
            DrawZone(graphics, titlePen, font, "Название", Relative(image, Offset(layout.Title, slot, layout.RowStep)));
            DrawZone(graphics, descriptionPen, font, "Описание", Relative(image, Offset(layout.Description, slot, layout.RowStep)));
            DrawZone(graphics, progressPen, font, "Прогресс", Relative(image, Offset(layout.Progress, slot, layout.RowStep)));
            DrawZone(graphics, xpPen, font, "XP", Relative(image, Offset(layout.Experience, slot, layout.RowStep)));
            DrawZone(graphics, statusPen, font, "Статус", Relative(image, Offset(layout.Status, slot, layout.RowStep)));
        }
    }

    private static void DrawZone(Graphics graphics, Pen pen, Font font, string label, Rectangle bounds)
    {
        graphics.DrawRectangle(pen, bounds);
        var labelSize = graphics.MeasureString(label, font);
        using var background = new SolidBrush(Color.FromArgb(200, pen.Color));
        graphics.FillRectangle(background, bounds.Left, bounds.Top, labelSize.Width + 10, labelSize.Height + 4);
        graphics.DrawString(label, font, Brushes.Black, bounds.Left + 5, bounds.Top + 2);
    }

    private static Rectangle Relative(Bitmap image, double x, double y, double width, double height) => new((int)(image.Width * x), (int)(image.Height * y), (int)(image.Width * width), (int)(image.Height * height));
    private static Rectangle Relative(Bitmap image, BattlePassOcrZone zone) => Relative(image, zone.X, zone.Y, zone.Width, zone.Height);

    private static Bitmap Capture(Rectangle bounds)
    {
        var image = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(image);
        graphics.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
        return image;
    }

    private static Bitmap CropRelative(Bitmap source, double x, double y, double width, double height)
    {
        var bounds = new Rectangle((int)(source.Width * x), (int)(source.Height * y), Math.Max(1, (int)(source.Width * width)), Math.Max(1, (int)(source.Height * height)));
        bounds.Intersect(new Rectangle(Point.Empty, source.Size));
        var result = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format24bppRgb);
        using var graphics = Graphics.FromImage(result);
        graphics.DrawImage(source, new Rectangle(Point.Empty, result.Size), bounds, GraphicsUnit.Pixel);
        return result;
    }

    private static Bitmap CropRelative(Bitmap source, BattlePassOcrZone zone) => CropRelative(source, zone.X, zone.Y, zone.Width, zone.Height);
    private static BattlePassOcrZone Offset(BattlePassOcrZone zone, int slot, double rowStep) => new(zone.X, zone.Y + slot * rowStep, zone.Width, zone.Height);

    private static async Task<string> RunOcrAsync(string executable, Bitmap source, int psm, CancellationToken ct)
    {
        var path = Path.Combine(Path.GetTempPath(), "dayz-bp-" + Guid.NewGuid().ToString("N") + ".png");
        try
        {
            using var prepared = Prepare(source);
            prepared.Save(path, ImageFormat.Png);
            var start = new ProcessStartInfo(executable, $"\"{path}\" stdout -l rus+eng --psm {psm} --dpi 192") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, CreateNoWindow = true };
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Не удалось запустить Tesseract.");
            var output = await process.StandardOutput.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            return process.ExitCode == 0 ? output : "";
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    private static Bitmap Prepare(Bitmap source)
    {
        var output = new Bitmap(source.Width * 3, source.Height * 3, PixelFormat.Format24bppRgb);
        using var graphics = Graphics.FromImage(output);
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.DrawImage(source, new Rectangle(Point.Empty, output.Size));
        return output;
    }

    private static string Clean(string value) => string.Join(' ', value.Replace('\r', ' ').Replace('\n', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries)).Trim();

    private static string? FindTesseract()
    {
        var candidates = new[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Tesseract-OCR", "tesseract.exe"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Tesseract-OCR", "tesseract.exe"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Tesseract-OCR", "tesseract.exe"), "tesseract.exe" };
        foreach (var candidate in candidates.Where(path => path == "tesseract.exe" || File.Exists(path)))
        {
            try
            {
                using var process = Process.Start(new ProcessStartInfo(candidate, "--list-langs") { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true });
                if (process is null || !process.WaitForExit(5000) || process.ExitCode != 0) continue;
                var languages = process.StandardOutput.ReadToEnd();
                if (languages.Contains("rus") && languages.Contains("eng")) return candidate;
            }
            catch (System.ComponentModel.Win32Exception) { }
        }
        return null;
    }
}

internal sealed record BattlePassScanResult(bool Success, string Message, int TaskCount);
