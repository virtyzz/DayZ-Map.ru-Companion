using System.ComponentModel;
using System.Diagnostics;

namespace CrosshairMarker;

internal sealed class UpdateHostForm : Form
{
    private readonly UpdatePlan plan;
    private readonly Label status;
    private readonly ProgressBar progress;
    private readonly Button reopen;
    private readonly Button close;
    private bool running = true;

    public UpdateHostForm(UpdatePlan plan)
    {
        this.plan = plan;
        Text = "Обновление Companion";
        Icon = AppIcons.MainIcon();
        ClientSize = new Size(520, 238);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = true;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(22, 22, 26);
        ForeColor = Color.WhiteSmoke;
        Font = new Font("Segoe UI", 10);
        var heading = new Label { Text = "Обновление DayZ-Map.ru Companion", AutoSize = false,
            Location = new Point(24, 22), Size = new Size(472, 32), Font = new Font("Segoe UI", 13, FontStyle.Bold) };
        status = new Label { Text = "Подготовка установки…", Location = new Point(24, 65), Size = new Size(472, 70) };
        progress = new ProgressBar { Location = new Point(24, 144), Size = new Size(472, 8), Style = ProgressBarStyle.Marquee };
        reopen = new Button { Text = "Запустить Companion", Location = new Point(24, 180), Size = new Size(225, 34), Visible = false };
        close = new Button { Text = "Закрыть", Location = new Point(366, 180), Size = new Size(130, 34), Visible = false };
        close.Click += (_, _) => Close();
        reopen.Click += (_, _) => Reopen();
        Controls.AddRange([heading, status, progress, reopen, close]);
        FormClosing += (_, e) => { if (running) e.Cancel = true; };
        Shown += async (_, _) => await InstallAsync();
    }

    private async Task InstallAsync()
    {
        try
        {
            await AppUpdater.VerifyInstallerAsync(plan.Installer, plan.Sha256);
            using (var ready = EventWaitHandle.OpenExisting(plan.ReadyEvent)) ready.Set();
            status.Text = "Завершение работы Companion…";
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60)))
                await AppUpdater.WaitForParentAsync(plan, timeout.Token);
            status.Text = "Установка обновления…\nПодтвердите запрос Windows на внесение изменений.";
            Refresh();
            using var installer = Process.Start(AppUpdater.InstallerStart(plan.Installer))
                ?? throw new IOException("Не удалось запустить установку.");
            await installer.WaitForExitAsync();
            if (installer.ExitCode == 3010)
                throw new IOException("Обновление установлено. Для завершения потребуется перезагрузка Windows.");
            if (installer.ExitCode != 0)
                throw new IOException($"Установка не завершена (код {installer.ExitCode}). Можно запустить Companion и повторить обновление. Журнал установки находится в папке TEMP.");
            status.Text = "Обновление установлено. Запуск Companion…";
            var name = @"Local\DayZCompanionRestart." + Guid.NewGuid().ToString("N");
            using var started = new EventWaitHandle(false, EventResetMode.ManualReset, name);
            var start = new ProcessStartInfo(plan.RestartExecutable) { UseShellExecute = true };
            start.ArgumentList.Add(AppUpdater.ReadyArgument);
            start.ArgumentList.Add(name);
            using var app = Process.Start(start) ?? throw new IOException("Не удалось запустить Companion.");
            if (!await Task.Run(() => started.WaitOne(TimeSpan.FromSeconds(60))))
                throw new IOException("Companion не подтвердил запуск. Попробуйте открыть его кнопкой ниже.");
            running = false;
            Close();
        }
        catch (Exception ex)
        {
            AppRuntimeLog.Error("Update installation or restart failed", ex);
            running = false;
            progress.Style = ProgressBarStyle.Blocks;
            status.Text = ex is Win32Exception { NativeErrorCode: 1223 }
                ? "Установка отменена в запросе Windows. Можно снова запустить Companion."
                : ex is OperationCanceledException ? "Companion не завершил работу. Закройте его и повторите обновление." : ex.Message;
            reopen.Visible = File.Exists(plan.RestartExecutable) || File.Exists(plan.OriginalExecutable);
            close.Visible = true;
        }
    }

    private void Reopen()
    {
        try
        {
            var path = File.Exists(plan.RestartExecutable) ? plan.RestartExecutable : plan.OriginalExecutable;
            using var app = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            Close();
        }
        catch (Exception ex) { status.Text = "Не удалось запустить Companion: " + ex.Message; }
    }
}
