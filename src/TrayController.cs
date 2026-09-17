using System.Drawing;
using System.Windows.Forms;

namespace CrosshairMarker;

internal sealed class TrayController : IDisposable
{
    private readonly NotifyIcon notifyIcon;
    private readonly Action onOpenGeneral;
    private readonly Action onOpenTreasures;
    private readonly Action onOpenTasks;
    private readonly Action onOpenCrosshair;
    private readonly Action onExit;
    private TrayMenuForm? popup;

    public TrayController(
        Action onOpenGeneral,
        Action onOpenTreasures,
        Action onOpenTasks,
        Action onOpenCrosshair,
        Action onExit)
    {
        this.onOpenGeneral = onOpenGeneral;
        this.onOpenTreasures = onOpenTreasures;
        this.onOpenTasks = onOpenTasks;
        this.onOpenCrosshair = onOpenCrosshair;
        this.onExit = onExit;

        notifyIcon = new NotifyIcon
        {
            Text = AppIdentity.DisplayName,
            Icon = AppIcons.Tray(),
            Visible = true
        };
        notifyIcon.DoubleClick += (_, _) => onOpenGeneral();
        notifyIcon.MouseUp += (_, eventArgs) =>
        {
            if (eventArgs.Button == MouseButtons.Right) ShowPopup();
        };
    }

    public void Dispose()
    {
        popup?.Close();
        notifyIcon.Visible = false;
        notifyIcon.Dispose();
    }

    private void ShowPopup()
    {
        popup?.Close();
        popup = new TrayMenuForm(
            onOpenGeneral,
            onOpenTreasures,
            onOpenTasks,
            onOpenCrosshair,
            onExit);
        popup.FormClosed += (_, _) => popup = null;
        popup.ShowAboveTaskbar(Cursor.Position);
    }
}
