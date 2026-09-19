using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Text;
using System.Text.RegularExpressions;

namespace CrosshairMarker;

internal sealed class TreasureCaptureService
{
    private static readonly Regex LabeledCoordinates = new(@"[xх]\s*[:=]?\s*(?<x>\d{3,6})\D{0,48}?[zз]\s*[:=]?\s*(?<z>\d{3,6})", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Number = new(@"(?<!\d)(\d{3,6})(?!\d)", RegexOptions.Compiled);
    private readonly TreasureCaptureStore store;

    public TreasureCaptureService(TreasureCaptureStore store) => this.store = store;

    public async Task<TreasureCapture?> CaptureAsync(CancellationToken cancellationToken = default)
    {
        var capture = CaptureImage();
        if (capture is null) return null;
        await RecognizeCaptureAsync(capture, cancellationToken);
        return capture;
    }

    public TreasureCapture? CaptureImage()
    {
        var screenBounds = SystemInformation.VirtualScreen;
        using var screen = Capture(screenBounds);
        var region = ScreenRegionSelector.SelectRegion(screen);
        if (!region.HasValue) return null;
        var capture = new TreasureCapture { Status = TreasureRecognitionStatus.Queued };
        var imagePath = store.CreateImagePath(capture.Id);
        try
        {
            var selection = Rectangle.Intersect(screenBounds, region.Value);
            if (selection.Width == 0 || selection.Height == 0) return null;
            var source = new Rectangle(selection.Left - screenBounds.Left, selection.Top - screenBounds.Top, selection.Width, selection.Height);
            using var image = screen.Clone(source, PixelFormat.Format32bppArgb);
            image.Save(imagePath, ImageFormat.Png);
            capture.ImagePath = imagePath;
            return capture;
        }
        catch
        {
            if (File.Exists(imagePath)) File.Delete(imagePath);
            throw;
        }
    }

    public async Task RecognizeCaptureAsync(TreasureCapture capture, CancellationToken cancellationToken = default)
    {
        var executable = FindTesseract();
        if (executable is null) throw new InvalidOperationException("Tesseract OCR с языками rus и eng не найден.");
        using var source = new Bitmap(capture.ImagePath);
        var ocr = await RecognizeAsync(executable, source, cancellationToken);
        capture.RawText = ocr.RawText;
        capture.X = ocr.X;
        capture.Z = ocr.Z;
        capture.Status = ocr.Status;
    }

    public async Task<TreasureOcrResult> RecognizeAsync(string executable, Bitmap image, CancellationToken cancellationToken = default)
    {
        var temporary = Path.Combine(Path.GetTempPath(), "dayz-treasure-" + Guid.NewGuid().ToString("N") + ".png");
        try
        {
            using (var prepared = Prepare(image)) prepared.Save(temporary, ImageFormat.Png);
            var start = new ProcessStartInfo(executable, $"\"{temporary}\" stdout -l rus+eng --psm 6 --dpi 192")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                CreateNoWindow = true
            };
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Не удалось запустить Tesseract.");
            var raw = await process.StandardOutput.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            if (process.ExitCode != 0) return new TreasureOcrResult("", null, null, TreasureRecognitionStatus.Ambiguous, "Tesseract не смог распознать выбранную область.");
            return Parse(raw);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    internal static TreasureOcrResult Parse(string rawText)
    {
        var text = string.Join(' ', rawText.Replace('\r', ' ').Replace('\n', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries));
        var labeled = LabeledCoordinates.Match(text);
        if (labeled.Success)
        {
            return new TreasureOcrResult(text, int.Parse(labeled.Groups["x"].Value), int.Parse(labeled.Groups["z"].Value), TreasureRecognitionStatus.Recognized, "Координаты распознаны.");
        }

        var values = Number.Matches(text).Select(item => int.Parse(item.Value)).ToList();
        if (values.Count == 2)
        {
            return new TreasureOcrResult(text, values[0], values[1], TreasureRecognitionStatus.Recognized, "Найдена пара чисел.");
        }
        return new TreasureOcrResult(text, null, null, TreasureRecognitionStatus.Ambiguous, "Координаты не распознаны однозначно.");
    }

    private static Bitmap Capture(Rectangle bounds)
    {
        var image = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(image);
        graphics.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
        return image;
    }

    private static Bitmap Prepare(Bitmap source)
    {
        var output = new Bitmap(source.Width * 3, source.Height * 3, PixelFormat.Format24bppRgb);
        using var graphics = Graphics.FromImage(output);
        graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        graphics.DrawImage(source, new Rectangle(Point.Empty, output.Size));
        return output;
    }

    private static string? FindTesseract()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Tesseract-OCR", "tesseract.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Tesseract-OCR", "tesseract.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Tesseract-OCR", "tesseract.exe"),
            "tesseract.exe"
        };
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
