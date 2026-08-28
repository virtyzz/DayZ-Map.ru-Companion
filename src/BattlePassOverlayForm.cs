using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace CrosshairMarker;

internal sealed class BattlePassOverlayForm : Form
{
    private const int WmNcHitTest = 0x0084;
    private const int WmMouseActivate = 0x0021;
    private const int CollapsedHeight = 52;
    private const int WsExNoActivate = 0x08000000;
    private const int WsExToolWindow = 0x00000080;
    private static readonly IntPtr MaNoActivate = new(3);
    private static readonly IntPtr HtClient = new(1);
    private BattlePassSettings settings = new();
    private BattlePassSnapshot snapshot = new();
    private List<ManualTask> manualTasks = [];
    private bool editing;
    private Point dragOrigin;
    private bool dragging;
    private bool resizing;
    private ResizeEdges resizeEdges;
    private Rectangle resizeStartBounds;
    private Point resizeStartScreen;
    private readonly Dictionary<string, Rectangle> taskBounds = [];
    private readonly Dictionary<string, Rectangle> actionBounds = [];
    private string? clickedAction;
    private int scrollOffset;
    private int contentHeight;
    private Rectangle scrollTrack;
    private Rectangle scrollThumb;
    private bool scrolling;
    private bool manualTasksCollapsed;
    private readonly Dictionary<string, ManualTaskEditors> manualEditors = [];
    private BattlePassTask? clickedTask;
    private Point mouseDownPoint;
    private string? interceptedAction;
    private bool actionInputArmed;

    [Flags]
    private enum ResizeEdges { None = 0, Left = 1, Top = 2, Right = 4, Bottom = 8 }

    public event Action<BattlePassSettings>? SettingsChanged;
    public event Action<BattlePassSettings>? SettingsPersistRequested;
    public event Action<BattlePassSnapshot>? SnapshotChanged;
    public event Action<BattlePassPage>? ScanRequested;
    public event Action<IReadOnlyList<ManualTask>>? ManualTasksChanged;
    public bool Editing => editing;

    public bool TryInterceptActionMouseDown(Point screenPoint)
    {
        if (!Visible) return false;
        var action = HitTestAction(PointToClient(screenPoint));
        if (action is null || (action != "manual:add" && !action.StartsWith("scan:", StringComparison.Ordinal))) return false;
        ArmActionInput();
        interceptedAction = action;
        AppRuntimeLog.Info($"Battle Pass mouse down intercepted for {action}.");
        return true;
    }

    public bool TryInterceptActionMouseUp(Point screenPoint)
    {
        var action = interceptedAction;
        if (action is null) return false;
        interceptedAction = null;
        if (HitTestAction(PointToClient(screenPoint)) != action)
        {
            AppRuntimeLog.Info($"Battle Pass mouse up intercepted outside {action}.");
            return true;
        }
        AppRuntimeLog.Info($"Battle Pass mouse up intercepted for {action}.");
        BeginInvoke(() => ActivateAction(action));
        return true;
    }

    public BattlePassOverlayForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        DoubleBuffered = true;
        ResizeRedraw = true;
        BackColor = Color.Black;
        Opacity = .9;
        MouseDown += OnMouseDown;
        MouseMove += OnMouseMove;
        MouseUp += OnMouseUp;
        MouseWheel += (_, e) => ScrollBy(-Math.Sign(e.Delta) * 42);
    }

    protected override bool ShowWithoutActivation => !editing;

    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.ExStyle |= WsExToolWindow;
            if (!editing && !actionInputArmed) parameters.ExStyle |= WsExNoActivate;
            return parameters;
        }
    }

    public void Apply(BattlePassSettings next, BattlePassSnapshot nextSnapshot, IEnumerable<ManualTask>? nextManualTasks = null)
    {
        settings = next.Clone(); settings.Normalize(); snapshot = nextSnapshot;
        if (nextManualTasks is not null) manualTasks = nextManualTasks.ToList();
        var screen = MonitorInfo.ResolveScreen(settings.MonitorDeviceName);
        var height = settings.OverlayCollapsed ? CollapsedHeight : settings.Height;
        Bounds = new Rectangle(screen.Bounds.Left + settings.Left, screen.Bounds.Top + settings.Top, settings.Width, height);
        ConstrainToVirtualDesktop();
        Opacity = settings.Opacity / 255d;
        Invalidate();
    }

    public void SetEditing(bool value)
    {
        if (editing == value) return;
        editing = value;
        Cursor = Cursors.Default;
        RecreateHandle();
        Invalidate();
    }

    public void RestoreOverlayBounds(Rectangle bounds)
    {
        Bounds = bounds;
        SyncCurrentBoundsToSettings();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.Clear(Color.Black);
        using var background = new SolidBrush(Color.FromArgb(218, 14, 16, 18));
        e.Graphics.FillRectangle(background, ClientRectangle);
        if (editing) using (var pen = new Pen(Color.FromArgb(255, 255, 142, 0), 2)) e.Graphics.DrawRectangle(pen, 1, 1, Width - 3, Height - 3);

        taskBounds.Clear();
        actionBounds.Clear();
        var tasks = FilteredTasks().ToList();
        using var titleFont = new Font("Segoe UI", settings.FontSize + 2, FontStyle.Bold);
        using var taskFont = new Font("Segoe UI", settings.FontSize, FontStyle.Regular);
        using var smallFont = new Font("Segoe UI", Math.Max(9, settings.FontSize - 2), FontStyle.Regular);
        e.Graphics.DrawString("BATTLE PASS", titleFont, Brushes.Orange, 14, 10);
        var titleWidth = (int)Math.Ceiling(e.Graphics.MeasureString("BATTLE PASS", titleFont).Width);
        var collapseButton = new Rectangle(14 + titleWidth + 4, 10, 24, settings.FontSize + 10);
        actionBounds["toggle:overlay"] = collapseButton;
        e.Graphics.DrawString(settings.OverlayCollapsed ? "▶" : "▼", titleFont, Brushes.Orange, collapseButton.Left, collapseButton.Top);
        if (settings.OverlayCollapsed)
        {
            scrollOffset = 0;
            contentHeight = Height;
            scrollTrack = scrollThumb = Rectangle.Empty;
            HideUnusedManualEditors(new HashSet<string>());
            return;
        }
        var sync = snapshot.UpdatedAt.HasValue ? $"обновлено {snapshot.UpdatedAt.Value.LocalDateTime:HH:mm}" : "нет синхронизации";
        e.Graphics.DrawString(sync, smallFont, Brushes.LightGray, 16, 38);

        // The title area is fixed.  Only the task list below it is scrollable;
        // without a clip, rows scrolled above the viewport are painted over
        // the title and update time.
        var contentClip = e.Graphics.Save();
        e.Graphics.SetClip(new Rectangle(0, 60, Width, Math.Max(0, Height - 60)));
        var y = 60 - scrollOffset;
        y = DrawGroup("daily", "ЕЖЕДНЕВНЫЕ ЗАДАНИЯ", tasks.Where(task => task.Page == BattlePassPage.Daily), settings.DailyCollapsed, ["scan:Daily"]);
        y = DrawGroup("weekly", "ЕЖЕНЕДЕЛЬНЫЕ ЗАДАНИЯ", tasks.Where(task => task.Page is BattlePassPage.WeeklyPage1 or BattlePassPage.WeeklyPage2), settings.WeeklyCollapsed, ["scan:WeeklyPage1", "scan:WeeklyPage2"]);
        y = DrawGroup("seasonal", "СЕЗОННЫЕ ЗАДАНИЯ", tasks.Where(task => task.Page == BattlePassPage.Seasonal), settings.SeasonalCollapsed, ["scan:Seasonal"]);
        y = DrawManualTasksGroup();
        contentHeight = y + scrollOffset + 10;
        if (tasks.Count == 0) e.Graphics.DrawString("Нет подходящих заданий", taskFont, Brushes.LightGray, 14, y);
        e.Graphics.Restore(contentClip);
        DrawScrollBar(e.Graphics);

        int DrawGroup(string key, string label, IEnumerable<BattlePassTask> groupTasks, bool collapsed, string[] scans)
        {
            const int scanButtonWidth = 70;
            const int buttonGap = 6;
            var buttonsWidth = scanButtonWidth * scans.Length + buttonGap * (scans.Length - 1);
            var buttonX = Width - buttonsWidth - 14;
            var header = new Rectangle(10, y, Math.Max(40, buttonX - 16), settings.FontSize + 14);
            actionBounds["toggle:" + key] = header;
            e.Graphics.DrawString((collapsed ? "▶ " : "▼ ") + label, taskFont, Brushes.Orange, 14, y);
            foreach (var scan in scans)
            {
                var button = new Rectangle(buttonX, y, scanButtonWidth, settings.FontSize + 10);
                actionBounds[scan] = button;
                using var buttonBrush = new SolidBrush(Color.FromArgb(210, 80, 50, 5));
                e.Graphics.FillRectangle(buttonBrush, button);
                e.Graphics.DrawString(scans.Length == 2 ? (scan.EndsWith("1") ? "Скан 1" : "Скан 2") : "Скан", smallFont, Brushes.Orange, button.Left + 4, button.Top + 2);
                buttonX += scanButtonWidth + buttonGap;
            }
            y += settings.FontSize + 18;
            if (collapsed) return y;
            foreach (var task in groupTasks)
            {
                var color = task.Completed ? Color.FromArgb(90, 220, 125) : Color.White;
                using var brush = new SolidBrush(color);
                e.Graphics.DrawString(task.Title, taskFont, brush, new RectangleF(14, y, Width - 92, settings.FontSize * 2.2f));
                e.Graphics.DrawString(task.ProgressText, taskFont, brush, Width - 74, y);
                var descriptionHeight = 0;
                if (settings.ShowTaskDescriptions && task.DescriptionExpanded && !string.IsNullOrWhiteSpace(task.Description))
                {
                    descriptionHeight = (int)Math.Ceiling(e.Graphics.MeasureString(task.Description, smallFont, Width - 28).Height) + 3;
                    e.Graphics.DrawString(task.Description, smallFont, Brushes.LightGray, new RectangleF(14, y + settings.FontSize * 2, Width - 28, descriptionHeight));
                }
                var bar = new Rectangle(14, y + settings.FontSize * 2 + descriptionHeight + 3, Width - 28, 5);
                using var track = new SolidBrush(Color.FromArgb(90, 140, 140, 140)); using var fill = new SolidBrush(task.Completed ? Color.LimeGreen : Color.Orange);
                e.Graphics.FillRectangle(track, bar); if (task.Target > 0) e.Graphics.FillRectangle(fill, bar.Left, bar.Top, (int)Math.Round(bar.Width * Math.Clamp(task.Current / (double)task.Target, 0, 1)), bar.Height);
                var rowHeight = settings.FontSize * 2 + descriptionHeight + 20; taskBounds[task.Id] = new Rectangle(0, y, Width, rowHeight); y += rowHeight;
            }
            return y;
        }

        int DrawManualTasksGroup()
        {
            var button = new Rectangle(Width - 84, y, 70, settings.FontSize + 10);
            var header = new Rectangle(10, y, Math.Max(40, button.Left - 16), settings.FontSize + 14);
            actionBounds["toggle:manual"] = header;
            actionBounds["manual:add"] = button;
            e.Graphics.DrawString((manualTasksCollapsed ? "▶ " : "▼ ") + "СПИСОК ЛИЧНЫХ ЗАДАЧ", taskFont, Brushes.Orange, 14, y);
            using (var buttonBrush = new SolidBrush(Color.FromArgb(210, 80, 50, 5))) e.Graphics.FillRectangle(buttonBrush, button);
            e.Graphics.DrawString("Добавить", smallFont, Brushes.Orange, button.Left + 4, button.Top + 2);
            y += settings.FontSize + 18;
            var visibleEditors = new HashSet<string>();
            if (!manualTasksCollapsed)
            {
                foreach (var task in manualTasks.ToList())
                {
                    // The row uses the same 14px right inset as every Scan/
                    // Add button above it, with 6px gaps between controls.
                    var title = new Rectangle(14, y, Math.Max(90, Width - 200), 23);
                    var current = new Rectangle(Width - 180, y, 42, 23);
                    var target = new Rectangle(Width - 132, y, 42, 23);
                    var save = new Rectangle(Width - 84, y, 32, 23);
                    var delete = new Rectangle(Width - 46, y, 32, 23);
                    var rowVisible = y >= 60 && y + 52 <= Height;
                    EnsureManualEditors(task, title, current, target, save, delete, rowVisible);
                    visibleEditors.Add(task.Id);
                    if (!task.IsEditing)
                    {
                        var color = task.Target > 0 && task.Current >= task.Target ? Color.FromArgb(90, 220, 125) : Color.White;
                        using var brush = new SolidBrush(color);
                        e.Graphics.DrawString(task.Title, taskFont, brush, new RectangleF(14, y, Width - 126, settings.FontSize * 2.2f));
                        e.Graphics.DrawString($"{task.Current}/{task.Target}", taskFont, brush, Width - 158, y);
                    }
                    var ratio = task.Target > 0 ? Math.Clamp(task.Current / (double)task.Target, 0, 1) : 0;
                    var bar = new Rectangle(14, y + 31, Width - 28, 5);
                    using var track = new SolidBrush(Color.FromArgb(90, 140, 140, 140));
                    using var fill = new SolidBrush(task.Target > 0 && task.Current >= task.Target ? Color.LimeGreen : Color.Orange);
                    e.Graphics.FillRectangle(track, bar);
                    e.Graphics.FillRectangle(fill, bar.Left, bar.Top, (int)Math.Round(bar.Width * ratio), bar.Height);
                    y += 50;
                }
            }
            HideUnusedManualEditors(visibleEditors);
            return y;
        }
    }

    private IEnumerable<BattlePassTask> FilteredTasks() => snapshot.Tasks
        .Where(task => task.Page switch { BattlePassPage.Daily => settings.ShowDaily, BattlePassPage.Seasonal => settings.ShowSeasonal, _ => settings.ShowWeekly })
        .Where(task => settings.ShowCompleted || !task.Completed)
        .OrderBy(task => task.Page).ThenBy(task => task.Slot);

    private int CalculateHeight()
    {
        var rows = FilteredTasks().ToList();
        var descriptions = settings.ShowTaskDescriptions ? rows.Where(task => task.DescriptionExpanded && !string.IsNullOrWhiteSpace(task.Description)).Sum(DescriptionHeight) : 0;
        return Math.Clamp(68 + Math.Max(1, rows.Count) * (settings.FontSize * 2 + 20) + descriptions, 92, 850);
    }

    private void EnsureManualEditors(ManualTask task, Rectangle title, Rectangle current, Rectangle target, Rectangle save, Rectangle delete, bool visible)
    {
        if (!manualEditors.TryGetValue(task.Id, out var editors))
        {
            var titleBox = new TextBox { BorderStyle = BorderStyle.FixedSingle };
            var currentBox = new TextBox { BorderStyle = BorderStyle.FixedSingle, TextAlign = HorizontalAlignment.Center };
            var targetBox = new TextBox { BorderStyle = BorderStyle.FixedSingle, TextAlign = HorizontalAlignment.Center };
            var saveButton = new OverlayIconButton { FlatStyle = FlatStyle.Flat, UseVisualStyleBackColor = false };
            var deleteButton = new OverlayIconButton { Kind = ManualTaskButtonKind.Delete, FlatStyle = FlatStyle.Flat, UseVisualStyleBackColor = false };
            editors = new ManualTaskEditors(titleBox, currentBox, targetBox, saveButton, deleteButton);
            manualEditors[task.Id] = editors;
            Controls.AddRange([titleBox, currentBox, targetBox, saveButton, deleteButton]);
            titleBox.TextChanged += (_, _) => { task.Title = titleBox.Text; Invalidate(); };
            currentBox.TextChanged += (_, _) => { if (int.TryParse(currentBox.Text, out var value)) task.Current = Math.Max(0, value); Invalidate(); };
            targetBox.TextChanged += (_, _) => { if (int.TryParse(targetBox.Text, out var value)) task.Target = Math.Max(0, value); Invalidate(); };
            saveButton.Click += (_, _) => SaveManualTask(task, editors);
            deleteButton.Click += (_, _) => DeleteManualTask(task);
        }
        if (!editors.Title.Focused) editors.Title.Text = task.Title;
        if (!editors.Current.Focused) editors.Current.Text = task.Current.ToString();
        if (!editors.Target.Focused) editors.Target.Text = task.Target > 0 ? task.Target.ToString() : "";
        editors.Title.Bounds = title;
        editors.Current.Bounds = current;
        editors.Target.Bounds = target;
        editors.Save.Bounds = save;
        editors.Delete.Bounds = delete;
        editors.Save.Kind = task.IsEditing ? ManualTaskButtonKind.Save : ManualTaskButtonKind.Edit;
        editors.Title.Visible = editors.Current.Visible = editors.Target.Visible = visible && task.IsEditing;
        editors.Save.Visible = editors.Delete.Visible = visible;
    }

    private void HideUnusedManualEditors(ISet<string> visibleIds)
    {
        foreach (var (id, editors) in manualEditors)
        {
            if (visibleIds.Contains(id)) continue;
            editors.Title.Visible = editors.Current.Visible = editors.Target.Visible = editors.Save.Visible = editors.Delete.Visible = false;
        }
    }

    private void SaveManualTask(ManualTask task, ManualTaskEditors editors)
    {
        if (!task.IsEditing)
        {
            task.IsEditing = true;
            Invalidate();
            return;
        }
        if (string.IsNullOrWhiteSpace(editors.Title.Text) ||
            !int.TryParse(editors.Current.Text, out var current) || current < 0 ||
            !int.TryParse(editors.Target.Text, out var target) || target <= 0)
        {
            MessageBox.Show("Укажите название и корректный прогресс (текущее значение и цель).", "Личные задачи", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        task.Title = editors.Title.Text.Trim();
        task.Current = current;
        task.Target = target;
        task.IsEditing = false;
        ManualTasksChanged?.Invoke(manualTasks);
        Invalidate();
    }

    private void DeleteManualTask(ManualTask task)
    {
        manualTasks.Remove(task);
        if (manualEditors.Remove(task.Id, out var editors))
        {
            Controls.Remove(editors.Title); Controls.Remove(editors.Current); Controls.Remove(editors.Target); Controls.Remove(editors.Save); Controls.Remove(editors.Delete);
            editors.Dispose();
        }
        ManualTasksChanged?.Invoke(manualTasks);
        Invalidate();
    }

    private sealed class ManualTaskEditors(TextBox title, TextBox current, TextBox target, OverlayIconButton save, OverlayIconButton delete) : IDisposable
    {
        public TextBox Title { get; } = title;
        public TextBox Current { get; } = current;
        public TextBox Target { get; } = target;
        public OverlayIconButton Save { get; } = save;
        public OverlayIconButton Delete { get; } = delete;
        public void Dispose() { Title.Dispose(); Current.Dispose(); Target.Dispose(); Save.Dispose(); Delete.Dispose(); }
    }

    private enum ManualTaskButtonKind { Save, Edit, Delete }

    private sealed class OverlayIconButton : Button
    {
        public ManualTaskButtonKind Kind { get; set; } = ManualTaskButtonKind.Save;

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Color.FromArgb(210, 24, 28, 31));
            using var border = new Pen(Color.FromArgb(185, 255, 142, 0));
            using var icon = new Pen(Color.Orange, 2) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            e.Graphics.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
            var area = new Rectangle(7, 4, Math.Max(1, Width - 14), Math.Max(1, Height - 8));
            switch (Kind)
            {
                case ManualTaskButtonKind.Save:
                    e.Graphics.DrawRectangle(icon, area);
                    e.Graphics.DrawLine(icon, area.Left + 3, area.Top, area.Left + 3, area.Top + 6);
                    e.Graphics.DrawRectangle(icon, area.Left + 3, area.Bottom - 5, Math.Max(2, area.Width - 6), 4);
                    break;
                case ManualTaskButtonKind.Edit:
                    e.Graphics.DrawLine(icon, area.Left + 2, area.Bottom - 2, area.Right - 2, area.Top + 2);
                    e.Graphics.DrawLine(icon, area.Left + 2, area.Bottom - 5, area.Left + 5, area.Bottom - 2);
                    break;
                default:
                    e.Graphics.DrawLine(icon, area.Left + 2, area.Top + 2, area.Right - 2, area.Bottom - 2);
                    e.Graphics.DrawLine(icon, area.Right - 2, area.Top + 2, area.Left + 2, area.Bottom - 2);
                    break;
            }
        }
    }

    private void OnMouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        // A drag may only be armed from the dedicated title area below.
        // In particular, never reuse the coordinates of a preceding click on
        // a group header after that click changed the layout.
        dragging = false;
        resizing = false;
        resizeEdges = ResizeEdges.None;
        clickedAction = null;
        clickedTask = null;
        if (scrollTrack.Contains(e.Location))
        {
            scrolling = true;
            Capture = true;
            UpdateScrollFromPointer(e.Y);
            return;
        }
        clickedAction = HitTestAction(e.Location);
        if (clickedAction is not null)
        {
            Capture = true;
            mouseDownPoint = e.Location;
            clickedTask = null;
            return;
        }
        if (!editing)
        {
            Capture = true;
            resizing = false;
            mouseDownPoint = e.Location;
            clickedTask = HitTestTask(e.Location);
            return;
        }
        resizeEdges = settings.OverlayCollapsed ? ResizeEdges.None : HitTestResizeEdges(e.Location);
        resizing = resizeEdges != ResizeEdges.None;
        if (resizing)
        {
            resizeStartBounds = Bounds;
            resizeStartScreen = PointToScreen(e.Location);
            Capture = true;
            mouseDownPoint = e.Location;
            return;
        }
        if (e.Y >= 60)
        {
            // Content is reserved for task/group interactions. Do not turn a
            // missed first hit-test after repaint into a window drag.
            Capture = true;
            clickedTask = HitTestTask(e.Location);
            mouseDownPoint = e.Location;
            return;
        }
        dragOrigin = e.Location;
        mouseDownPoint = e.Location;
        dragging = !resizing;
        clickedTask = resizing ? null : HitTestTask(e.Location);
    }
    private void OnMouseUp(object? sender, MouseEventArgs e)
    {
        Capture = false;
        if (scrolling)
        {
            scrolling = false;
            dragging = false;
            return;
        }
        if (clickedAction is not null && Math.Abs(e.X - mouseDownPoint.X) < 4 && Math.Abs(e.Y - mouseDownPoint.Y) < 4)
        {
            dragging = false;
            resizing = false;
            ActivateAction(clickedAction);
            clickedAction = null;
            return;
        }
        clickedAction = null;
        var wasClick = clickedTask is not null && !resizing && Math.Abs(e.X - mouseDownPoint.X) < 4 && Math.Abs(e.Y - mouseDownPoint.Y) < 4;
        var shouldSaveLocation = dragging || resizing;
        resizing = false;
        resizeEdges = ResizeEdges.None;
        dragging = false;
        if (wasClick && settings.ShowTaskDescriptions)
        {
            clickedTask!.DescriptionExpanded = !clickedTask.DescriptionExpanded;
            SnapshotChanged?.Invoke(snapshot);
            Apply(settings, snapshot);
            return;
        }
        if (shouldSaveLocation) SaveLocation();
    }

    private BattlePassTask? HitTestTask(Point point) => point.Y < 60
        ? null
        : FilteredTasks().FirstOrDefault(task => taskBounds.TryGetValue(task.Id, out var bounds) && bounds.Contains(point));

    private string? HitTestAction(Point point) => actionBounds.FirstOrDefault(item => item.Value.Contains(point)).Key;

    private ResizeEdges HitTestResizeEdges(Point point)
    {
        const int border = 8;
        const int rightBorder = 3;
        var result = ResizeEdges.None;
        if (point.X <= border) result |= ResizeEdges.Left;
        else if (point.X >= Width - rightBorder) result |= ResizeEdges.Right;
        if (point.Y <= border) result |= ResizeEdges.Top;
        else if (point.Y >= Height - border) result |= ResizeEdges.Bottom;
        return result;
    }

    private static Cursor ResizeCursor(ResizeEdges edges) => edges switch
    {
        ResizeEdges.Left or ResizeEdges.Right => Cursors.SizeWE,
        ResizeEdges.Top or ResizeEdges.Bottom => Cursors.SizeNS,
        ResizeEdges.Top | ResizeEdges.Left or ResizeEdges.Bottom | ResizeEdges.Right => Cursors.SizeNWSE,
        ResizeEdges.Top | ResizeEdges.Right or ResizeEdges.Bottom | ResizeEdges.Left => Cursors.SizeNESW,
        _ => Cursors.Default
    };

    private void DrawScrollBar(Graphics graphics)
    {
        var viewport = Math.Max(1, Height - 60);
        var maximum = Math.Max(0, contentHeight - Height + 10);
        if (maximum == 0) { scrollOffset = 0; scrollTrack = scrollThumb = Rectangle.Empty; return; }
        scrollOffset = Math.Clamp(scrollOffset, 0, maximum);
        scrollTrack = new Rectangle(Width - 9, 60, 6, viewport - 8);
        var thumbHeight = Math.Max(24, (int)Math.Round(scrollTrack.Height * viewport / (double)Math.Max(viewport, contentHeight)));
        var range = Math.Max(1, scrollTrack.Height - thumbHeight);
        var thumbTop = scrollTrack.Top + (int)Math.Round(range * scrollOffset / (double)maximum);
        scrollThumb = new Rectangle(scrollTrack.Left, thumbTop, scrollTrack.Width, thumbHeight);
        using var trackBrush = new SolidBrush(Color.FromArgb(110, 120, 120, 120));
        using var thumbBrush = new SolidBrush(Color.FromArgb(220, 255, 142, 0));
        graphics.FillRectangle(trackBrush, scrollTrack); graphics.FillRectangle(thumbBrush, scrollThumb);
    }

    private void UpdateScrollFromPointer(int y)
    {
        if (scrollTrack.IsEmpty || scrollThumb.IsEmpty) return;
        var maximum = Math.Max(0, contentHeight - Height + 10);
        var range = Math.Max(1, scrollTrack.Height - scrollThumb.Height);
        scrollOffset = Math.Clamp((int)Math.Round((y - scrollTrack.Top - scrollThumb.Height / 2d) * maximum / range), 0, maximum);
        Invalidate();
    }

    private void ScrollBy(int delta)
    {
        var maximum = Math.Max(0, contentHeight - Height + 10);
        var next = Math.Clamp(scrollOffset + delta, 0, maximum);
        if (next == scrollOffset) return;
        scrollOffset = next;
        Invalidate();
    }

    private void ActivateAction(string action)
    {
        if (action == "manual:add")
        {
            BeginInvoke(() =>
            {
                manualTasks.Add(new ManualTask { IsEditing = true });
                Invalidate();
            });
            return;
        }
        if (action.StartsWith("scan:", StringComparison.Ordinal) && Enum.TryParse<BattlePassPage>(action[5..], out var page))
        {
            SyncCurrentBoundsToSettings();
            SettingsPersistRequested?.Invoke(settings.Clone());
            BeginInvoke(() =>
            {
                actionInputArmed = false;
                RecreateHandle();
                ScanRequested?.Invoke(page);
            });
            return;
        }
        switch (action)
        {
            case "toggle:overlay":
                // Keep the actual constrained position as the anchor while
                // changing height, otherwise Apply can restore stale bounds.
                SyncCurrentBoundsToSettings();
                settings.OverlayCollapsed = !settings.OverlayCollapsed;
                scrollOffset = 0;
                var location = Location;
                Height = settings.OverlayCollapsed ? CollapsedHeight : settings.Height;
                Location = location;
                Invalidate();
                SettingsPersistRequested?.Invoke(settings.Clone());
                return;
            case "toggle:daily": settings.DailyCollapsed = !settings.DailyCollapsed; break;
            case "toggle:weekly": settings.WeeklyCollapsed = !settings.WeeklyCollapsed; break;
            case "toggle:seasonal": settings.SeasonalCollapsed = !settings.SeasonalCollapsed; break;
            case "toggle:manual": manualTasksCollapsed = !manualTasksCollapsed; break;
            default: return;
        }
        // Persist without applying the settings back to this form.  This keeps
        // the current bounds intact while retaining the collapsed state.
        SettingsPersistRequested?.Invoke(settings.Clone());
        Invalidate();
    }

    private int DescriptionHeight(BattlePassTask task)
    {
        var availableWidth = Math.Max(80, Width - 28);
        var approximateCharacters = Math.Max(18, availableWidth / Math.Max(5, settings.FontSize / 2));
        var lines = Math.Clamp((int)Math.Ceiling(task.Description.Length / (double)approximateCharacters), 1, 4);
        return lines * (settings.FontSize + 3);
    }

    protected override void WndProc(ref Message m)
    {
        if (!editing && !actionInputArmed && m.Msg == WmMouseActivate)
        {
            // Keep DayZ focused, but retain the mouse message in this overlay
            // rather than handing it to the window underneath.
            m.Result = MaNoActivate;
            return;
        }
        if (m.Msg == WmNcHitTest)
        {
            // The overlay owns every mouse hit in its bounds.  Passing empty
            // areas through to the game allowed a single physical click to be
            // handled by both windows, which could turn a section click into
            // an unintended drag after the layout changed.
            m.Result = HtClient;
            return;
        }
        base.WndProc(ref m);
    }
    private void OnMouseMove(object? sender, MouseEventArgs e)
    {
        ArmActionInputIfNeeded(e.Location);
        if (scrolling && e.Button == MouseButtons.Left)
        {
            UpdateScrollFromPointer(e.Y);
            return;
        }
        if (!editing) return;
        if (resizing && e.Button == MouseButtons.Left)
        {
            ResizeFromPointer(PointToScreen(e.Location));
            return;
        }
        if (e.Button != MouseButtons.Left)
        {
            Cursor = settings.OverlayCollapsed ? Cursors.Default : ResizeCursor(HitTestResizeEdges(e.Location));
            return;
        }
        if (resizing) return;
        if (!dragging) return;
        Location = new Point(Left + e.X - dragOrigin.X, Top + e.Y - dragOrigin.Y);
        ConstrainToVirtualDesktop();
    }

    private void ArmActionInputIfNeeded(Point point)
    {
        if (editing || actionInputArmed || !Visible) return;
        var action = HitTestAction(point);
        if (action is null || (action != "manual:add" && !action.StartsWith("scan:", StringComparison.Ordinal))) return;
        ArmActionInput();
    }

    private void ArmActionInput()
    {
        if (editing || actionInputArmed || !Visible) return;
        actionInputArmed = true;
        RecreateHandle();
        Activate();
        AppRuntimeLog.Info("Battle Pass overlay activated for an action button.");
    }

    private void ResizeFromPointer(Point screenPoint)
    {
        var deltaX = screenPoint.X - resizeStartScreen.X;
        var deltaY = screenPoint.Y - resizeStartScreen.Y;
        var left = resizeStartBounds.Left;
        var top = resizeStartBounds.Top;
        var right = resizeStartBounds.Right;
        var bottom = resizeStartBounds.Bottom;
        if (resizeEdges.HasFlag(ResizeEdges.Left)) left = Math.Clamp(left + deltaX, right - 900, right - 220);
        if (resizeEdges.HasFlag(ResizeEdges.Right)) right = Math.Clamp(right + deltaX, left + 220, left + 900);
        if (resizeEdges.HasFlag(ResizeEdges.Top)) top = Math.Clamp(top + deltaY, bottom - 850, bottom - 92);
        if (resizeEdges.HasFlag(ResizeEdges.Bottom)) bottom = Math.Clamp(bottom + deltaY, top + 92, top + 850);
        Bounds = Rectangle.FromLTRB(left, top, right, bottom);
        ConstrainToVirtualDesktop();
        settings.Width = Width;
        settings.Height = Height;
        Invalidate();
    }
    private void SaveLocation()
    {
        if (!editing) return;
        SyncCurrentBoundsToSettings();
        SettingsChanged?.Invoke(settings.Clone());
    }

    private void SyncCurrentBoundsToSettings()
    {
        var screen = MonitorInfo.ResolveScreen(settings.MonitorDeviceName);
        settings.Left = Math.Max(0, Left - screen.Bounds.Left); settings.Top = Math.Max(0, Top - screen.Bounds.Top);
        settings.Width = Width;
        if (!settings.OverlayCollapsed) settings.Height = Height;
    }

    private void ConstrainToVirtualDesktop()
    {
        const int minimumVisible = 60;
        var desktop = Screen.AllScreens.Select(screen => screen.Bounds).Aggregate(Rectangle.Union);
        var minLeft = desktop.Left - Width + minimumVisible;
        var maxLeft = Math.Max(minLeft, desktop.Right - minimumVisible);
        var minTop = desktop.Top - Height + minimumVisible;
        var maxTop = Math.Max(minTop, desktop.Bottom - minimumVisible);
        Location = new Point(Math.Clamp(Left, minLeft, maxLeft), Math.Clamp(Top, minTop, maxTop));
    }
}
