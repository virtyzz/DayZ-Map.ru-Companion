using System.Drawing;
using System.Windows.Forms;

namespace CrosshairMarker;

internal sealed class TrayController : IDisposable
{
    private readonly NotifyIcon notifyIcon;
    private readonly ContextMenuStrip menu;
    private readonly ToolStripMenuItem toggleItem;
    private readonly ToolStripMenuItem profilesItem;
    private readonly Action<string> onSelectProfile;

    public TrayController(
        Action onToggleOverlay,
        Action onOpenEditor,
        Action onOpenUpdates,
        Action<string> onSelectProfile,
        Action onOpenBattlePass,
        Action onScanBattlePass,
        Action onExit)
    {
        this.onSelectProfile = onSelectProfile;
        toggleItem = new ToolStripMenuItem("Скрыть оверлей", null, (_, _) => onToggleOverlay());
        var editorItem = new ToolStripMenuItem("Редактор", null, (_, _) => onOpenEditor());
        var updatesItem = new ToolStripMenuItem("Обновление", null, (_, _) => onOpenUpdates());
        profilesItem = new ToolStripMenuItem("Профили");
        var exitItem = new ToolStripMenuItem("Выход", null, (_, _) => onExit());

        menu = new ContextMenuStrip();
        menu.Items.Add(toggleItem);
        menu.Items.Add(profilesItem);
        menu.Items.Add(editorItem);
        menu.Items.Add(updatesItem);
        menu.Items.Add(new ToolStripMenuItem("Battle Pass: настройки", null, (_, _) => onOpenBattlePass()));
        menu.Items.Add(new ToolStripMenuItem("Battle Pass: считать экран", null, (_, _) => onScanBattlePass()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(exitItem);

        notifyIcon = new NotifyIcon
        {
            Text = AppIdentity.DisplayName,
            Icon = AppIcons.Tray(),
            ContextMenuStrip = menu,
            Visible = true
        };
        notifyIcon.DoubleClick += (_, _) => onOpenEditor();
    }

    public void SetOverlayVisible(bool visible)
    {
        toggleItem.Text = visible ? "Скрыть оверлей" : "Показать оверлей";
    }

    public void SetProfiles(IEnumerable<CrosshairProfile> profiles, string activeProfileId)
    {
        profilesItem.DropDownItems.Clear();

        foreach (var profile in profiles)
        {
            var item = new ToolStripMenuItem(profile.Name)
            {
                Checked = profile.Id == activeProfileId,
                Tag = profile.Id
            };
            item.Click += (_, _) =>
            {
                if (item.Tag is string profileId)
                {
                    menu.BeginInvoke(() => onSelectProfile(profileId));
                }
            };
            profilesItem.DropDownItems.Add(item);
        }

        profilesItem.Enabled = profilesItem.DropDownItems.Count > 0;
    }

    public void Dispose()
    {
        notifyIcon.Visible = false;
        notifyIcon.Dispose();
    }
}
