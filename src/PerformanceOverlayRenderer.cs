using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;

namespace CrosshairMarker;

internal static class PerformanceOverlayRenderer
{
    private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;
    private sealed record Metric(string Text, string Color);

    private static Font CreateFont(PerformanceSettings settings)
    {
        var style = (settings.Bold ? FontStyle.Bold : FontStyle.Regular) | (settings.Italic ? FontStyle.Italic : FontStyle.Regular);
        try { return new Font(settings.FontFamily, settings.FontSize, style); }
        catch (ArgumentException) { return new Font("Segoe UI", settings.FontSize, style); }
    }

    private static List<Metric> Metrics(PerformanceSettings s, PerformanceSnapshot value)
    {
        static string Number(double? number, string format) => number?.ToString(format, Culture) ?? "—";
        var result = new List<Metric>();
        if (s.ShowFps) result.Add(new("FPS  " + Number(value.Fps, "0"), s.GetMetricColor(value.Fps, true)));
        if (s.ShowPing) result.Add(new("Пинг  " + Number(value.Ping, "0") + " мс", s.GetMetricColor(value.Ping, false)));
        if (s.ShowFrameTime) result.Add(new("Кадр  " + Number(value.FrameTime, "0.0") + " мс", s.TextColor));
        if (s.ShowOnePercentLow) result.Add(new("1% low  " + Number(value.OnePercentLow, "0"), s.GetMetricColor(value.OnePercentLow, true)));
        if (s.ShowJitter) result.Add(new("Jitter  " + Number(value.Jitter, "0.0") + " мс", s.TextColor));
        return result;
    }

    public static Size PreferredSize(PerformanceSettings settings)
    {
        using var bitmap = new Bitmap(1, 1);
        using var graphics = Graphics.FromImage(bitmap);
        using var font = CreateFont(settings);
        var metrics = Metrics(settings, new(Fps: 999, Ping: 999, FrameTime: 99.9, OnePercentLow: 999, Jitter: 99.9));
        var widths = metrics.Select(m => graphics.MeasureString(m.Text, font, PointF.Empty, StringFormat.GenericTypographic).Width).ToArray();
        var width = metrics.Count == 0 ? 260 : settings.VerticalLayout ? widths.Max() : widths.Sum() + (metrics.Count - 1) * 18;
        var height = metrics.Count == 0 ? 0 : font.GetHeight(graphics) * (settings.VerticalLayout ? metrics.Count : 1) + 8;
        return new Size(Math.Clamp((int)Math.Ceiling(width) + 24, settings.ShowFrameGraph ? 260 : 120, 1200),
            Math.Clamp((int)Math.Ceiling(height) + 12 + (settings.ShowFrameGraph ? 80 : 0), 32, 500));
    }

    public static void Fit(PerformanceSettings settings, bool onlyGrow = false)
    {
        var size = PreferredSize(settings);
        settings.Width = onlyGrow ? Math.Max(settings.Width, size.Width) : size.Width;
        settings.Height = onlyGrow ? Math.Max(settings.Height, size.Height) : size.Height;
    }

    public static Bitmap Render(Size size, PerformanceSettings settings, PerformanceSnapshot value)
    {
        var bitmap = new Bitmap(Math.Max(1, size.Width), Math.Max(1, size.Height), PixelFormat.Format32bppPArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.Transparent);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        var backgroundAlpha = (int)Math.Round((settings.BackgroundOpacity ?? settings.Opacity) * 2.55);
        // A minimally visible surface keeps the entire window draggable with a transparent background.
        if (settings.EditingEnabled) backgroundAlpha = Math.Max(1, backgroundAlpha);
        using (var brush = new SolidBrush(Color.FromArgb(backgroundAlpha, ColorTranslator.FromHtml(settings.BackgroundColor))))
            graphics.FillRectangle(brush, 0, 0, size.Width, size.Height);
        var alpha = (int)Math.Round((settings.TextOpacity ?? settings.Opacity) * 2.55);
        using var font = CreateFont(settings);
        var metrics = Metrics(settings, value);
        var graphHeight = settings.ShowFrameGraph ? Math.Min(80, Math.Max(30, size.Height / (metrics.Count > 0 ? 2 : 1))) : 0;
        var content = new RectangleF(10, 4, Math.Max(1, size.Width - 20), Math.Max(1, size.Height - 8 - graphHeight));
        graphics.SetClip(content);
        using var format = new StringFormat(StringFormat.GenericTypographic) { FormatFlags = StringFormatFlags.NoWrap, Trimming = StringTrimming.EllipsisCharacter };
        var lineHeight = font.GetHeight(graphics) + 4;
        var widths = metrics.Select(m => graphics.MeasureString(m.Text, font, PointF.Empty, format).Width).ToArray();
        var totalWidth = widths.Sum() + Math.Max(0, metrics.Count - 1) * 18;
        var x = content.Left + Math.Max(0, settings.Alignment == "Right" ? content.Width - totalWidth : settings.Alignment == "Center" ? (content.Width - totalWidth) / 2 : 0);
        var y = content.Top + Math.Max(0, (content.Height - lineHeight * (settings.VerticalLayout ? metrics.Count : 1)) / 2);
        for (var i = 0; i < metrics.Count; i++)
        {
            var metric = metrics[i];
            using var brush = new SolidBrush(Color.FromArgb(alpha, ColorTranslator.FromHtml(metric.Color)));
            if (settings.VerticalLayout)
            {
                format.Alignment = settings.Alignment switch { "Left" => StringAlignment.Near, "Right" => StringAlignment.Far, _ => StringAlignment.Center };
                graphics.DrawString(metric.Text, font, brush, new RectangleF(content.Left, y, content.Width, lineHeight), format);
                y += lineHeight;
            }
            else
            {
                graphics.DrawString(metric.Text, font, brush, new RectangleF(x, y, widths[i] + 2, lineHeight), format);
                x += widths[i] + 18;
            }
        }
        graphics.ResetClip();
        if (settings.ShowFrameGraph) DrawGraph(graphics, new RectangleF(10, size.Height - graphHeight, Math.Max(1, size.Width - 20), Math.Max(1, graphHeight - 6)), settings, value.FrameTimeHistory, alpha);
        if (settings.EditingEnabled)
        {
            using var pen = new Pen(Color.FromArgb(Math.Max(160, alpha), ColorTranslator.FromHtml(settings.TextColor))) { DashStyle = DashStyle.Dot };
            graphics.DrawRectangle(pen, 0, 0, size.Width - 1, size.Height - 1);
            graphics.DrawLine(pen, size.Width - 12, size.Height - 3, size.Width - 3, size.Height - 12);
        }
        return bitmap;
    }

    private static void DrawGraph(Graphics graphics, RectangleF area, PerformanceSettings settings, double?[]? history, int alpha)
    {
        var values = history ?? [];
        var scale = Math.Max(33.3, values.Where(v => v.HasValue).Select(v => v!.Value).DefaultIfEmpty(0).Max());
        using var font = new Font("Segoe UI", 8);
        using var brush = new SolidBrush(Color.FromArgb(alpha, ColorTranslator.FromHtml(settings.TextColor)));
        graphics.DrawString("Кадр: пик за секунду · 60 с", font, brush, area.Left, area.Top);
        var label = scale.ToString("0", Culture) + " мс";
        var labelWidth = graphics.MeasureString(label, font).Width;
        if (area.Width > 240) graphics.DrawString(label, font, brush, area.Right - labelWidth, area.Top);
        var plot = new RectangleF(area.Left, area.Top + 19, area.Width, Math.Max(1, area.Height - 20));
        using var axis = new Pen(Color.FromArgb(alpha / 3, ColorTranslator.FromHtml(settings.TextColor)));
        graphics.DrawLine(axis, plot.Left, plot.Bottom, plot.Right, plot.Bottom);
        graphics.DrawLine(axis, plot.Left, plot.Top, plot.Right, plot.Top);
        PointF? previous = null;
        for (var i = 0; i < values.Length; i++)
        {
            if (values[i] is not { } value || !double.IsFinite(value)) { previous = null; continue; }
            var point = new PointF(plot.Left + plot.Width * i / Math.Max(1, values.Length - 1), plot.Bottom - (float)(value / scale) * plot.Height);
            using var pen = new Pen(Color.FromArgb(alpha, ColorTranslator.FromHtml(settings.GetMetricColor(1000 / Math.Max(.001, value), true))), 1.5f);
            if (previous is { } start) graphics.DrawLine(pen, start, point);
            previous = point;
        }
    }
}
