using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace CrosshairMarker;

internal sealed class ThemedComboBox : ComboBox
{
    private const int WM_ENABLE = 0x000A;
    private const int WM_SETFOCUS = 0x0007;
    private const int WM_KILLFOCUS = 0x0008;
    private const int WM_PAINT = 0x000F;
    private const int WM_NCPAINT = 0x0085;
    private const int WM_MOUSEMOVE = 0x0200;
    private const int WM_MOUSELEAVE = 0x02A3;
    private const int WM_MOUSEHOVER = 0x02A1;

    public Color SurfaceColor { get; set; } = Color.FromArgb(38, 38, 43);
    public Color BorderColor { get; set; } = Color.FromArgb(56, 56, 62);
    public Color TextColor { get; set; } = Color.FromArgb(235, 235, 238);
    public Color SelectionColor { get; set; } = Color.FromArgb(61, 45, 25);
    public Color SelectionTextColor { get; set; } = Color.FromArgb(235, 235, 238);

    public ThemedComboBox()
    {
        DrawMode = DrawMode.OwnerDrawFixed;
        DropDownStyle = ComboBoxStyle.DropDownList;
        FlatStyle = FlatStyle.Flat;
        IntegralHeight = false;
        ItemHeight = 28;
        BackColor = SurfaceColor;
        ForeColor = TextColor;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyNativeTheme();
    }

    protected override void OnDropDown(EventArgs e)
    {
        ApplyNativeTheme();
        base.OnDropDown(e);
    }

    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);

        if (DropDownStyle == ComboBoxStyle.DropDownList && ShouldRepaintClosedChrome(m.Msg))
        {
            PaintClosedBorder();
        }
    }

    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= Items.Count)
        {
            return;
        }

        var selected = (e.State & DrawItemState.Selected) == DrawItemState.Selected;
        var focused = (e.State & DrawItemState.Focus) == DrawItemState.Focus;
        using var backBrush = new SolidBrush(selected ? SelectionColor : SurfaceColor);
        using var textBrush = new SolidBrush(selected ? SelectionTextColor : TextColor);
        e.Graphics.FillRectangle(backBrush, e.Bounds);

        var text = GetItemText(Items[e.Index]);
        var textBounds = Rectangle.Inflate(e.Bounds, -8, 0);
        TextRenderer.DrawText(
            e.Graphics,
            text,
            Font,
            textBounds,
            textBrush.Color,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

        if (focused)
        {
            var focusBounds = Rectangle.Inflate(e.Bounds, -1, -1);
            using var focusPen = new Pen(selected ? BorderColor : SelectionColor, 1);
            e.Graphics.DrawRectangle(focusPen, focusBounds);
        }
    }

    private static bool ShouldRepaintClosedChrome(int message) =>
        message is WM_PAINT
            or WM_NCPAINT
            or WM_MOUSEMOVE
            or WM_MOUSELEAVE
            or WM_MOUSEHOVER
            or WM_SETFOCUS
            or WM_KILLFOCUS
            or WM_ENABLE;

    private void PaintClosedBorder()
    {
        using var graphics = Graphics.FromHwnd(Handle);
        var bounds = new Rectangle(0, 0, Width - 1, Height - 1);
        var buttonWidth = SystemInformation.HorizontalScrollBarArrowWidth + 1;
        var buttonBounds = new Rectangle(Width - buttonWidth - 1, 1, buttonWidth, Height - 2);

        using var surfaceBrush = new SolidBrush(SurfaceColor);
        using var borderPen = new Pen(BorderColor, 1);
        using var arrowBrush = new SolidBrush(TextColor);

        graphics.FillRectangle(surfaceBrush, buttonBounds);
        graphics.DrawLine(borderPen, buttonBounds.Left, buttonBounds.Top, buttonBounds.Left, buttonBounds.Bottom);
        graphics.DrawRectangle(borderPen, bounds);

        var centerX = buttonBounds.Left + buttonBounds.Width / 2;
        var centerY = buttonBounds.Top + buttonBounds.Height / 2 + 1;
        var arrow = new[]
        {
            new Point(centerX - 4, centerY - 2),
            new Point(centerX + 4, centerY - 2),
            new Point(centerX, centerY + 2)
        };
        graphics.FillPolygon(arrowBrush, arrow);
    }

    private void ApplyNativeTheme()
    {
        if (!IsHandleCreated)
        {
            return;
        }

        _ = NativeMethods.SetWindowTheme(Handle, "DarkMode_CFD", null);

        var info = new NativeMethods.ComboBoxInfo
        {
            cbSize = Marshal.SizeOf<NativeMethods.ComboBoxInfo>()
        };

        if (!NativeMethods.GetComboBoxInfo(Handle, ref info) || info.hwndList == IntPtr.Zero)
        {
            return;
        }

        _ = NativeMethods.SetWindowTheme(info.hwndList, "DarkMode_Explorer", null);
    }

    private static class NativeMethods
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct ComboBoxInfo
        {
            public int cbSize;
            public RECT rcItem;
            public RECT rcButton;
            public int stateButton;
            public IntPtr hwndCombo;
            public IntPtr hwndItem;
            public IntPtr hwndList;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        public static extern int SetWindowTheme(IntPtr hWnd, string? pszSubAppName, string? pszSubIdList);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetComboBoxInfo(IntPtr hwndCombo, ref ComboBoxInfo info);
    }
}
