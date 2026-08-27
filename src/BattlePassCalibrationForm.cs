using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace CrosshairMarker;

internal sealed class BattlePassCalibrationForm : Form
{
    private readonly CalibrationCanvas canvas;
    private readonly BattlePassSettings settings;
    public event Action<BattlePassSettings>? Saved;

    public BattlePassCalibrationForm(Bitmap screenshot, BattlePassSettings source)
    {
        settings = source.Clone(); settings.Normalize();
        Text = "Battle Pass — настройка OCR-зон"; StartPosition = FormStartPosition.CenterScreen; Width = 1280; Height = 800;
        canvas = new CalibrationCanvas(screenshot, settings.OcrLayout!.Clone()) { Dock = DockStyle.Fill };
        var hint = new Label { AutoSize = true, Text = "Перетаскивайте зону за центр; тяните за правый нижний угол, чтобы изменить размер. Все строки одного типа изменятся вместе." };
        var save = new Button { Text = "Сохранить зоны", AutoSize = true }; save.Click += (_, _) => { settings.OcrLayout = canvas.OcrLayout.Clone(); settings.OcrLayout.Normalize(); Saved?.Invoke(settings); Close(); };
        var cancel = new Button { Text = "Отмена", AutoSize = true }; cancel.Click += (_, _) => Close();
        var footer = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 44, Padding = new Padding(8) }; footer.Controls.Add(hint); footer.Controls.Add(save); footer.Controls.Add(cancel);
        Controls.Add(canvas); Controls.Add(footer);
    }
}

internal sealed class CalibrationCanvas : Control
{
    private readonly Bitmap image;
    private readonly Dictionary<string, (BattlePassOcrZone Zone, Color Color)> zones;
    private string? active;
    private int activeRow;
    private Point start;
    private BattlePassOcrZone before = new();
    private bool resizing;

    public BattlePassOcrLayout OcrLayout { get; }

    public CalibrationCanvas(Bitmap image, BattlePassOcrLayout layout)
    {
        this.image = image; OcrLayout = layout; DoubleBuffered = true; BackColor = Color.Black;
        zones = new()
        {
            ["Название"] = (OcrLayout.Title, Color.Lime), ["Описание"] = (OcrLayout.Description, Color.Cyan), ["Прогресс"] = (OcrLayout.Progress, Color.Orange), ["XP"] = (OcrLayout.Experience, Color.MediumPurple), ["Статус"] = (OcrLayout.Status, Color.Red)
        };
        MouseDown += StartDrag; MouseMove += Drag; MouseUp += (_, _) => { active = null; Invalidate(); };
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e); e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic; e.Graphics.DrawImage(image, ClientRectangle);
        foreach (var item in zones)
        for (var row = 0; row < 5; row++)
        {
            var rect = ToClient(Offset(item.Value.Zone, row));
            using var pen = new Pen(item.Value.Color, active == item.Key && activeRow == row ? 4 : 2);
            e.Graphics.DrawRectangle(pen, rect);
            if (row == 0)
            {
                using var brush = new SolidBrush(Color.FromArgb(210, item.Value.Color));
                e.Graphics.FillRectangle(brush, rect.Left, rect.Top, Math.Min(rect.Width, 100), 23);
                e.Graphics.DrawString(item.Key, Font, Brushes.Black, rect.Left + 3, rect.Top + 3);
            }
            e.Graphics.FillRectangle(Brushes.White, rect.Right - 7, rect.Bottom - 7, 7, 7);
        }
    }

    private void StartDrag(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        foreach (var item in zones.Reverse())
        for (var row = 4; row >= 0; row--)
        {
            var rect = ToClient(Offset(item.Value.Zone, row));
            if (!rect.Contains(e.Location)) continue;
            active = item.Key; activeRow = row; start = e.Location; before = item.Value.Zone.Clone(); resizing = e.X >= rect.Right - 14 && e.Y >= rect.Bottom - 14; return;
        }
    }

    private void Drag(object? sender, MouseEventArgs e)
    {
        if (active is null || e.Button != MouseButtons.Left) return;
        var zone = zones[active].Zone; var dx = (e.X - start.X) / (double)Math.Max(1, Width); var dy = (e.Y - start.Y) / (double)Math.Max(1, Height);
        if (resizing) { zone.Width = before.Width + dx; zone.Height = before.Height + dy; }
        else { zone.X = before.X + dx; zone.Y = before.Y + dy; }
        zone.Normalize(); Invalidate();
    }

    private Rectangle ToClient(BattlePassOcrZone zone) => new((int)Math.Round(zone.X * Width), (int)Math.Round(zone.Y * Height), Math.Max(2, (int)Math.Round(zone.Width * Width)), Math.Max(2, (int)Math.Round(zone.Height * Height)));
    private BattlePassOcrZone Offset(BattlePassOcrZone zone, int row) => new(zone.X, zone.Y + row * OcrLayout.RowStep, zone.Width, zone.Height);

    protected override void Dispose(bool disposing) { if (disposing) image.Dispose(); base.Dispose(disposing); }
}
