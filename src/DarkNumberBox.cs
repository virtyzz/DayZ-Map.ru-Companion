using System.Windows.Forms;

namespace CrosshairMarker;

internal sealed class DarkNumberBox : TextBox
{
    private int value;

    public event EventHandler? ValueChanged;

    public int Minimum { get; set; }
    public int Maximum { get; set; } = 100;

    public int Value
    {
        get => value;
        set
        {
            var next = Math.Clamp(value, Minimum, Maximum);
            if (this.value == next && Text == next.ToString())
            {
                return;
            }

            this.value = next;
            Text = next.ToString();
            ValueChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public DarkNumberBox()
    {
        BackColor = Color.FromArgb(32, 32, 32);
        ForeColor = Color.White;
        BorderStyle = BorderStyle.FixedSingle;
        TextAlign = HorizontalAlignment.Center;
    }

    protected override void OnTextChanged(EventArgs e)
    {
        base.OnTextChanged(e);
        if (!int.TryParse(Text, out var parsed))
        {
            return;
        }

        var next = Math.Clamp(parsed, Minimum, Maximum);
        if (value == next)
        {
            return;
        }

        value = next;
        ValueChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnLeave(EventArgs e)
    {
        Text = value.ToString();
        base.OnLeave(e);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        Value += e.Delta > 0 ? 1 : -1;
        base.OnMouseWheel(e);
    }
}
