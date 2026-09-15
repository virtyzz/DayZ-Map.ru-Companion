using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace CrosshairMarker;

internal sealed class TreasureCapturesForm : Form
{
    private readonly TreasureCaptureStore store;
    private readonly TreasureCaptureService captureService;
    private readonly TreasureMapBridge mapBridge;
    private readonly List<TreasureCapture> captures;
    private readonly ListView list = new() { Dock = DockStyle.Fill, FullRowSelect = true, View = View.Details, MultiSelect = false };
    private readonly PictureBox preview = new() { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.Black };
    private readonly TextBox rawText = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical };
    private readonly Label status = new() { AutoSize = true };
    private readonly Button destinationButton = new() { Text = "Отправить на карту…", AutoSize = true };

    public TreasureCapturesForm(TreasureCaptureStore store, TreasureCaptureService captureService, TreasureMapBridge mapBridge)
    {
        this.store = store;
        this.captureService = captureService;
        this.mapBridge = mapBridge;
        captures = store.Load();

        Text = "DayZ-Map — Клады";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(920, 580);
        Size = new Size(1120, 720);
        Icon = AppIcons.MainIcon();

        list.Columns.Add("Время", 145);
        list.Columns.Add("Координаты", 180);
        list.Columns.Add("Статус", 150);
        list.SelectedIndexChanged += (_, _) => ShowSelected();
        list.DoubleClick += (_, _) => EditSelected();

        var captureButton = new Button { Text = "Выделить область", AutoSize = true };
        captureButton.Click += async (_, _) => await CaptureAsync();
        var editButton = new Button { Text = "Изменить координаты", AutoSize = true };
        editButton.Click += (_, _) => EditSelected();
        var openButton = new Button { Text = "Открыть PNG", AutoSize = true };
        openButton.Click += (_, _) => OpenSelectedImage();
        var openFolderButton = new Button { Text = "Открыть папку PNG", AutoSize = true };
        openFolderButton.Click += (_, _) => OpenImagesFolder();
        var deleteButton = new Button { Text = "Удалить", AutoSize = true };
        deleteButton.Click += (_, _) => DeleteSelected();
        var confirmButton = new Button { Text = "Подтвердить выбранное", AutoSize = true };
        confirmButton.Click += (_, _) => ConfirmSelected();
        destinationButton.Click += (_, _) => SendToMap();

        var actions = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 48, Padding = new Padding(8), AutoSize = false };
        actions.Controls.AddRange([captureButton, editButton, confirmButton, openButton, openFolderButton, deleteButton, destinationButton]);
        var right = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterDistance = 370 };
        right.Panel1.Controls.Add(preview);
        right.Panel2.Controls.Add(rawText);
        var content = new SplitContainer { Dock = DockStyle.Fill, SplitterDistance = 500 };
        content.Panel1.Controls.Add(list);
        content.Panel2.Controls.Add(right);
        var footer = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 32, Padding = new Padding(8, 4, 8, 4) };
        footer.Controls.Add(status);
        Controls.Add(content);
        Controls.Add(footer);
        Controls.Add(actions);
        RefreshList();
    }

    public async Task CaptureAsync()
    {
        Hide();
        try
        {
            await Task.Delay(100);
            var capture = await captureService.CaptureAsync();
            if (capture is null) return;
            captures.Insert(0, capture);
            store.Save(captures);
            RefreshList(capture.Id);
        }
        catch (Exception ex)
        {
            AppRuntimeLog.Error("Treasure capture failed", ex);
            MessageBox.Show(this, ex.Message, "Клады", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            Show();
            Activate();
        }
    }

    private void RefreshList(string? selectId = null)
    {
        list.BeginUpdate();
        list.Items.Clear();
        foreach (var capture in captures.OrderByDescending(item => item.CapturedAt))
        {
            var item = new ListViewItem(capture.CapturedAt.LocalDateTime.ToString("dd.MM.yyyy HH:mm:ss")) { Tag = capture };
            item.SubItems.Add(capture.Coordinates);
            item.SubItems.Add(Describe(capture));
            list.Items.Add(item);
            if (capture.Id == selectId) item.Selected = true;
        }
        list.EndUpdate();
        ShowSelected();
    }

    private void ShowSelected()
    {
        preview.Image?.Dispose();
        preview.Image = null;
        var capture = Selected;
        if (capture is null)
        {
            rawText.Text = "";
            status.Text = captures.Count == 0 ? "Очередь пуста. Нажмите «Выделить область» после появления уведомления в игре." : "Выберите запись для проверки.";
            return;
        }
        if (File.Exists(capture.ImagePath))
        {
            using var image = Image.FromFile(capture.ImagePath);
            preview.Image = new Bitmap(image);
        }
        rawText.Text = capture.RawText;
        status.Text = capture.ManuallyEdited ? "Координаты изменены вручную." : Describe(capture);
        UpdateDestinationButton();
    }

    private TreasureCapture? Selected => list.SelectedItems.Count == 1 ? list.SelectedItems[0].Tag as TreasureCapture : null;

    private void EditSelected()
    {
        var capture = Selected;
        if (capture is null) return;
        using var dialog = new TreasureCoordinatesDialog(capture);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        capture.X = dialog.X;
        capture.Z = dialog.Z;
        capture.ManuallyEdited = true;
        capture.Confirmed = true;
        capture.Status = TreasureRecognitionStatus.Recognized;
        store.Save(captures);
        RefreshList(capture.Id);
    }

    private void ConfirmSelected()
    {
        var capture = Selected;
        if (capture is null || !capture.HasCoordinates) return;
        capture.Confirmed = true;
        store.Save(captures);
        RefreshList(capture.Id);
    }

    private void OpenSelectedImage()
    {
        var capture = Selected;
        if (capture is null || !File.Exists(capture.ImagePath)) return;
        Process.Start(new ProcessStartInfo(capture.ImagePath) { UseShellExecute = true });
    }

    private void OpenImagesFolder()
    {
        Process.Start(new ProcessStartInfo(store.ImagesDirectory) { UseShellExecute = true });
    }

    private void DeleteSelected()
    {
        var capture = Selected;
        if (capture is null) return;
        if (MessageBox.Show(this, "Удалить выбранный захват и его PNG?", "Клады", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        captures.Remove(capture);
        store.Save(captures);
        RefreshList();
    }

    private void UpdateDestinationButton()
    {
        destinationButton.Enabled = mapBridge.GetSession() is not null && captures.Any(item => item.HasCoordinates);
    }

    private void SendToMap()
    {
        var session = mapBridge.GetSession();
        if (session is null)
        {
            MessageBox.Show(this, "Откройте DayZ-Map в браузере и подключите Companion.", "Клады", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        using var dialog = new TreasureDestinationDialog(session);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            var profile = session.Profiles.Single(item => item.MapId == dialog.MapId && item.Id == dialog.ProfileId);
            mapBridge.Queue(dialog.MapId, dialog.ProfileId, profile.MarkerName, profile.MarkerType, profile.MarkerColor, captures);
            MessageBox.Show(this, "Пакет передан в подключённую вкладку DayZ-Map.", "Клады", MessageBoxButtons.OK, MessageBoxIcon.Information);
            UpdateDestinationButton();
        }
        catch (DayZCompanionException ex)
        {
            MessageBox.Show(this, ex.Message, "Клады", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private static string Describe(TreasureCapture capture)
    {
        var prefix = capture.Status switch
        {
            TreasureRecognitionStatus.Recognized => "Распознано",
            TreasureRecognitionStatus.NeedsReview => "Проверить OCR",
            _ => "Не распознано"
        };
        return capture.Confirmed ? prefix + " · подтверждено" : prefix;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) preview.Image?.Dispose();
        base.Dispose(disposing);
    }
}

internal sealed class TreasureDestinationDialog : Form
{
    private readonly ComboBox maps = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 360 };
    private readonly ComboBox profiles = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 360 };
    private readonly TreasureDestinationSession session;
    public string MapId => (maps.SelectedItem as TreasureMapOption)?.Id ?? "";
    public string ProfileId => (profiles.SelectedItem as TreasureProfileOption)?.Id ?? "";

    public TreasureDestinationDialog(TreasureDestinationSession session)
    {
        this.session = session;
        Text = "Куда отправить клады";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(440, 190);
        maps.DisplayMember = nameof(TreasureMapOption.Name);
        maps.DataSource = session.Maps;
        maps.SelectedIndexChanged += (_, _) => LoadProfiles();
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(14), ColumnCount = 1, RowCount = 5 };
        panel.Controls.Add(new Label { Text = "Карта", AutoSize = true }, 0, 0); panel.Controls.Add(maps, 0, 1);
        panel.Controls.Add(new Label { Text = "Профиль и его текущий шаблон метки", AutoSize = true }, 0, 2); panel.Controls.Add(profiles, 0, 3);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        buttons.Controls.Add(new Button { Text = "Отмена", DialogResult = DialogResult.Cancel, AutoSize = true });
        buttons.Controls.Add(new Button { Text = "Отправить", DialogResult = DialogResult.OK, AutoSize = true });
        panel.Controls.Add(buttons, 0, 4);
        Controls.Add(panel);
        LoadProfiles();
    }

    private void LoadProfiles()
    {
        var available = session.Profiles.Where(item => item.MapId == MapId && item.Writable).ToList();
        profiles.DisplayMember = nameof(TreasureProfileOption.Name);
        profiles.DataSource = available;
    }
}

internal sealed class TreasureCoordinatesDialog : Form
{
    private readonly NumericUpDown x = new() { Minimum = 0, Maximum = 999999, Width = 160 };
    private readonly NumericUpDown z = new() { Minimum = 0, Maximum = 999999, Width = 160 };
    public int X => Decimal.ToInt32(x.Value);
    public int Z => Decimal.ToInt32(z.Value);

    public TreasureCoordinatesDialog(TreasureCapture capture)
    {
        Text = "Координаты клада";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(300, 130);
        x.Value = capture.X ?? 0;
        z.Value = capture.Z ?? 0;
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 2, RowCount = 3 };
        panel.Controls.Add(new Label { Text = "X", AutoSize = true }, 0, 0); panel.Controls.Add(x, 1, 0);
        panel.Controls.Add(new Label { Text = "Z", AutoSize = true }, 0, 1); panel.Controls.Add(z, 1, 1);
        var ok = new Button { Text = "Сохранить", DialogResult = DialogResult.OK, AutoSize = true };
        var cancel = new Button { Text = "Отмена", DialogResult = DialogResult.Cancel, AutoSize = true };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        buttons.Controls.Add(cancel); buttons.Controls.Add(ok); panel.Controls.Add(buttons, 0, 2); panel.SetColumnSpan(buttons, 2);
        Controls.Add(panel); AcceptButton = ok; CancelButton = cancel;
    }
}
