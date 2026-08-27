using System.Windows.Forms;

namespace CrosshairMarker;

internal sealed class BattlePassTasksForm : Form
{
    private readonly BattlePassStore store;
    private readonly DataGridView grid = new() { Dock = DockStyle.Fill, AutoGenerateColumns = false, AllowUserToAddRows = false, AllowUserToDeleteRows = false };
    private BattlePassSnapshot snapshot;
    public event Action? Changed;

    public BattlePassTasksForm(BattlePassStore store)
    {
        this.store = store; snapshot = store.LoadSnapshot();
        Text = "Battle Pass — проверка OCR"; StartPosition = FormStartPosition.CenterScreen; Size = new Size(900, 460);
        grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(BattlePassTask.Page), HeaderText = "Страница", ReadOnly = true, Width = 110 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(BattlePassTask.Slot), HeaderText = "Строка", ReadOnly = true, Width = 55 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(BattlePassTask.Title), HeaderText = "Название", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
        grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(BattlePassTask.Description), HeaderText = "Описание", Width = 190 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(BattlePassTask.Current), HeaderText = "Текущий", Width = 70 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(BattlePassTask.Target), HeaderText = "Цель", Width = 60 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(BattlePassTask.ExperienceReward), HeaderText = "XP", Width = 55 });
        grid.Columns.Add(new DataGridViewCheckBoxColumn { DataPropertyName = nameof(BattlePassTask.Completed), HeaderText = "Готово", Width = 60 });
        grid.Columns.Add(new DataGridViewCheckBoxColumn { DataPropertyName = nameof(BattlePassTask.Pinned), HeaderText = "Закрепить", Width = 75 });
        grid.DataSource = new BindingSource { DataSource = snapshot.Tasks.OrderBy(item => item.Page).ThenBy(item => item.Slot).ToList() };
        var save = new Button { Text = "Сохранить исправления", AutoSize = true }; save.Click += (_, _) => Save();
        var close = new Button { Text = "Закрыть", AutoSize = true }; close.Click += (_, _) => Close();
        var footer = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 46, Padding = new Padding(8) }; footer.Controls.Add(save); footer.Controls.Add(close);
        Controls.Add(grid); Controls.Add(footer);
    }
    private void Save()
    {
        grid.EndEdit();
        var rows = ((BindingSource)grid.DataSource).List.Cast<BattlePassTask>().ToList();
        snapshot.Tasks = rows; snapshot.UpdatedAt = DateTimeOffset.Now; store.SaveSnapshot(snapshot); Changed?.Invoke();
    }
}
