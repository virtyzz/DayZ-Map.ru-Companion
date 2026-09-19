using System.Drawing;
using System.Windows.Forms;

namespace CrosshairMarker;

internal sealed class TrayMenuForm : Form
{
    private const int MenuWidth = 204;
    private readonly FlowLayoutPanel items = new()
    {
        Dock = DockStyle.Fill,
        FlowDirection = FlowDirection.TopDown,
        WrapContents = false,
        AutoScroll = true,
        Padding = new Padding(4),
        BackColor = Color.FromArgb(33, 33, 36)
    };

    public TrayMenuForm(
        Action onOpenGeneral,
        Action onOpenTreasures,
        Action onOpenPlayerPosition,
        Action onOpenTasks,
        Action onOpenCrosshair,
        Action onExit)
    {
        AutoScaleMode = AutoScaleMode.Dpi;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.FromArgb(64, 64, 68);
        Padding = new Padding(1);
        Width = MenuWidth;
        Controls.Add(items);

        AddItem("Открыть окно", onOpenGeneral);
        AddSeparator();
        AddItem("Позиция игрока", onOpenPlayerPosition);
        AddItem("Клады", onOpenTreasures);
        AddItem("Оверлей заданий", onOpenTasks);
        AddItem("Прицел", onOpenCrosshair);
        AddSeparator();
        AddItem("Выйти", onExit);

        var preferredHeight = Math.Min(items.PreferredSize.Height + Padding.Vertical, 620);
        Height = Math.Max(44, preferredHeight);
        Deactivate += (_, _) => Close();
    }

    public void ShowAboveTaskbar(Point cursorPosition)
    {
        var workingArea = Screen.FromPoint(cursorPosition).WorkingArea;
        var left = Math.Clamp(cursorPosition.X - Width + 14, workingArea.Left, workingArea.Right - Width);
        var top = Math.Max(workingArea.Top, workingArea.Bottom - Height);
        Location = new Point(left, top);
        Show();
        Activate();
    }

    private void AddItem(string text, Action action)
    {
        var normalBackColor = Color.FromArgb(33, 33, 36);
        var hoverBackColor = Color.FromArgb(67, 67, 73);
        var button = new Button
        {
            Text = text,
            FlatStyle = FlatStyle.Flat,
            FlatAppearance = { BorderSize = 0, MouseOverBackColor = hoverBackColor, MouseDownBackColor = hoverBackColor },
            BackColor = normalBackColor,
            ForeColor = Color.FromArgb(245, 245, 247),
            Font = SystemFonts.MenuFont,
            TextAlign = ContentAlignment.MiddleLeft,
            TextImageRelation = TextImageRelation.TextBeforeImage,
            Margin = new Padding(0, 0, 0, 1),
            Padding = new Padding(9, 0, 6, 0),
            Size = new Size(MenuWidth - 10, 30),
            TabStop = true
        };
        button.Click += (_, _) =>
        {
            Close();
            action();
        };
        items.Controls.Add(button);
    }

    private void AddSeparator()
    {
        items.Controls.Add(new Panel
        {
            BackColor = Color.FromArgb(64, 64, 68),
            Margin = new Padding(7, 4, 7, 4),
            Size = new Size(MenuWidth - 22, 1)
        });
    }
}
