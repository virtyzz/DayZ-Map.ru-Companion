using System.Windows.Forms;

namespace CrosshairMarker;

internal sealed class HotkeyCaptureBox : TextBox
{
    public event Action<HotkeyBinding>? HotkeyChanged;

    public HotkeyCaptureBox()
    {
        ReadOnly = true;
        Width = 160;
    }

    public void SetBinding(HotkeyBinding binding)
    {
        Text = binding.DisplayText;
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData is Keys.Back or Keys.Delete)
        {
            HotkeyChanged?.Invoke(new HotkeyBinding { Enabled = false });
            return true;
        }

        var args = new KeyEventArgs(keyData);
        var binding = HotkeyBinding.FromKeyEvent(args);
        if (binding.Enabled)
        {
            HotkeyChanged?.Invoke(binding);
        }
        return true;
    }
}
