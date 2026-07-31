using System.Windows.Forms;

namespace CrosshairMarker;

internal sealed class HotkeyBindings
{
    public HotkeyBinding ToggleOverlay { get; set; } = DefaultToggleOverlay();
    public HotkeyBinding PreviousProfile { get; set; } = DefaultPreviousProfile();
    public HotkeyBinding NextProfile { get; set; } = DefaultNextProfile();
    public HotkeyBinding OpacityUp { get; set; } = DefaultOpacityUp();
    public HotkeyBinding OpacityDown { get; set; } = DefaultOpacityDown();
    public HotkeyBinding SizeUp { get; set; } = DefaultSizeUp();
    public HotkeyBinding SizeDown { get; set; } = DefaultSizeDown();

    public static HotkeyBindings Default() => new();

    public static HotkeyBinding DefaultToggleOverlay() => new(Keys.X);
    public static HotkeyBinding DefaultPreviousProfile() => new(Keys.Left);
    public static HotkeyBinding DefaultNextProfile() => new(Keys.Right);
    public static HotkeyBinding DefaultOpacityUp() => new(Keys.Up);
    public static HotkeyBinding DefaultOpacityDown() => new(Keys.Down);
    public static HotkeyBinding DefaultSizeUp() => new(Keys.PageUp);
    public static HotkeyBinding DefaultSizeDown() => new(Keys.PageDown);

    public void Normalize()
    {
        ToggleOverlay ??= DefaultToggleOverlay();
        PreviousProfile ??= DefaultPreviousProfile();
        NextProfile ??= DefaultNextProfile();
        OpacityUp ??= DefaultOpacityUp();
        OpacityDown ??= DefaultOpacityDown();
        SizeUp ??= DefaultSizeUp();
        SizeDown ??= DefaultSizeDown();
    }

    public HotkeyBindings Clone() => new()
    {
        ToggleOverlay = ToggleOverlay.Clone(),
        PreviousProfile = PreviousProfile.Clone(),
        NextProfile = NextProfile.Clone(),
        OpacityUp = OpacityUp.Clone(),
        OpacityDown = OpacityDown.Clone(),
        SizeUp = SizeUp.Clone(),
        SizeDown = SizeDown.Clone()
    };
}

internal sealed class HotkeyBinding
{
    public bool Enabled { get; set; } = true;
    public string Key { get; set; } = Keys.None.ToString();
    public bool Control { get; set; } = true;
    public bool Alt { get; set; } = true;
    public bool Shift { get; set; }
    public bool Win { get; set; }

    public HotkeyBinding()
    {
    }

    public HotkeyBinding(Keys key)
    {
        Key = key.ToString();
    }

    public HotkeyBinding Clone() => new()
    {
        Enabled = Enabled,
        Key = Key,
        Control = Control,
        Alt = Alt,
        Shift = Shift,
        Win = Win
    };

    public bool TryGetKeys(out Keys key)
    {
        return Enum.TryParse(Key, ignoreCase: true, out key) && key != Keys.None;
    }

    public HotkeyModifiers ToModifiers()
    {
        var modifiers = HotkeyModifiers.NoRepeat;
        if (Control)
        {
            modifiers |= HotkeyModifiers.Control;
        }
        if (Alt)
        {
            modifiers |= HotkeyModifiers.Alt;
        }
        if (Shift)
        {
            modifiers |= HotkeyModifiers.Shift;
        }
        if (Win)
        {
            modifiers |= HotkeyModifiers.Win;
        }
        return modifiers;
    }

    public string Signature => Enabled
        ? $"{Control}:{Alt}:{Shift}:{Win}:{Key.ToUpperInvariant()}"
        : string.Empty;

    public string DisplayText
    {
        get
        {
            if (!Enabled || !TryGetKeys(out var key))
            {
                return "Отключено";
            }

            var parts = new List<string>();
            if (Control)
            {
                parts.Add("Ctrl");
            }
            if (Alt)
            {
                parts.Add("Alt");
            }
            if (Shift)
            {
                parts.Add("Shift");
            }
            if (Win)
            {
                parts.Add("Win");
            }
            parts.Add(KeyName(key));
            return string.Join("+", parts);
        }
    }

    public static HotkeyBinding FromKeyEvent(KeyEventArgs e)
    {
        if (e.KeyCode is Keys.ControlKey or Keys.Menu or Keys.ShiftKey or Keys.LWin or Keys.RWin)
        {
            return new HotkeyBinding { Enabled = false };
        }

        return new HotkeyBinding
        {
            Enabled = true,
            Key = e.KeyCode.ToString(),
            Control = e.Control,
            Alt = e.Alt,
            Shift = e.Shift,
            Win = (e.Modifiers & Keys.LWin) == Keys.LWin || (e.Modifiers & Keys.RWin) == Keys.RWin
        };
    }

    private static string KeyName(Keys key) => key switch
    {
        Keys.PageUp => "Стр. вверх",
        Keys.PageDown => "Стр. вниз",
        Keys.Left => "Влево",
        Keys.Right => "Вправо",
        Keys.Up => "Вверх",
        Keys.Down => "Вниз",
        _ => key.ToString()
    };
}
