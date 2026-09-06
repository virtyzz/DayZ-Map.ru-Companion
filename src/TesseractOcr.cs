using System.Diagnostics;

namespace CrosshairMarker;

internal sealed record OcrStatus(bool Ready, string Message, string? ExecutablePath = null);

internal static class TesseractOcr
{
    public static void Install()
    {
        try
        {
            Process.Start(new ProcessStartInfo("winget.exe", "install --id tesseract-ocr.tesseract --exact --accept-package-agreements --accept-source-agreements") { UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception)
        {
            Process.Start(new ProcessStartInfo("https://github.com/tesseract-ocr/tesseract/releases/latest") { UseShellExecute = true });
        }
    }

    public static OcrStatus Detect()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Tesseract-OCR", "tesseract.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Tesseract-OCR", "tesseract.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Tesseract-OCR", "tesseract.exe"),
            "tesseract.exe"
        };
        foreach (var executable in candidates.Where(path => path == "tesseract.exe" || File.Exists(path)))
        {
            try
            {
                using var process = Process.Start(new ProcessStartInfo(executable, "--list-langs")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                });
                if (process is null || !process.WaitForExit(5000) || process.ExitCode != 0) continue;
                var languages = process.StandardOutput.ReadToEnd().Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                return languages.Contains("rus") && languages.Contains("eng")
                    ? new(true, "Tesseract OCR готов: rus и eng найдены.", executable)
                    : new(false, "В Tesseract должны быть установлены языки rus и eng.", executable);
            }
            catch (System.ComponentModel.Win32Exception) { }
        }
        return new(false, "Tesseract OCR не установлен.");
    }
}
