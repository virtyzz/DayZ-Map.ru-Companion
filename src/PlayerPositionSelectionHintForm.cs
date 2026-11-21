using System.Drawing;
using System.Windows.Forms;

namespace CrosshairMarker;

internal sealed class PlayerPositionSelectionHintForm : Form
{
    private PlayerPositionSelectionHintForm()
    {
        Text = "Настройка области координат";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 10.5f, FontStyle.Regular);
        // A hint is a Companion panel, not a separate black canvas. Match the
        // main editor surface and retain the darker shades for controls.
        BackColor = Color.FromArgb(23, 23, 26);
        ForeColor = Color.FromArgb(238, 238, 239);
        ClientSize = new Size(700, 790);
        Padding = new Padding(24);

        var title = new Label
        {
            AutoSize = true,
            Text = "Выделите только строку координат",
            Font = new Font("Segoe UI", 13.5f, FontStyle.Bold),
            ForeColor = Color.FromArgb(238, 238, 239),
            Location = new Point(24, 22)
        };
        var description = new Label
        {
            AutoSize = false,
            Text = "Координаты X Y Z находятся над мини-картой. Выделите только числа, например: 2700 214 5599. Не включайте карту, скорость или другие элементы HUD.",
            Font = new Font("Segoe UI", 10.5f, FontStyle.Regular),
            ForeColor = Color.FromArgb(155, 155, 163),
            Location = new Point(24, 58),
            Size = new Size(652, 48)
        };
        var imagePanel = new RoundedPanel
        {
            BackColor = Color.FromArgb(23, 23, 26),
            BorderColor = Color.FromArgb(40, 40, 46),
            Location = new Point(24, 122),
            Size = new Size(652, 560),
            Padding = new Padding(8)
        };
        var picture = new PictureBox
        {
            Dock = DockStyle.Fill,
            Image = LoadExampleImage(),
            SizeMode = PictureBoxSizeMode.Zoom,
            BackColor = Color.FromArgb(12, 12, 12)
        };
        imagePanel.Controls.Add(picture);

        var confirm = new RoundedButton
        {
            Text = "Понятно",
            DialogResult = DialogResult.OK,
            Radius = 8,
            BackColor = Color.FromArgb(25, 25, 29),
            HoverBackColor = Color.FromArgb(32, 32, 37),
            PressedBackColor = Color.FromArgb(25, 25, 29),
            ForeColor = Color.FromArgb(238, 238, 239),
            BorderColor = Color.FromArgb(52, 52, 58),
            Font = new Font("Segoe UI", 10.5f, FontStyle.Regular),
            Location = new Point(546, 712),
            Size = new Size(130, 38)
        };

        Controls.AddRange([title, description, imagePanel, confirm]);
        AcceptButton = confirm;
    }

    public static void ShowHint(IWin32Window owner)
    {
        using var dialog = new PlayerPositionSelectionHintForm();
        dialog.ShowDialog(owner);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);

        WindowAppearance.ApplyDarkCaption(Handle);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach (var picture in Controls.OfType<PictureBox>()) picture.Image?.Dispose();
        }
        base.Dispose(disposing);
    }

    private static Image LoadExampleImage()
    {
        var assembly = typeof(PlayerPositionSelectionHintForm).Assembly;
        using var stream = assembly.GetManifestResourceStream("DayZMapCompanion.PlayerPositionSelectionExample")
            ?? throw new InvalidOperationException("Player position selection example image is missing.");
        using var source = Image.FromStream(stream);
        return new Bitmap(source);
    }

}
