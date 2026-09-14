using ICDgenerator.Fom;

namespace ICDgenerator;

/// <summary>
/// Lets the reader assign each class its ID, Port and Rate.
///
/// None of the three comes from a FOM, and they used to be typed straight into the workbook — which
/// lost them on every regeneration, the index sheet writing all three blank each time. Holding them
/// here means they persist beside the FOM and reach both the sheet and, for the id, the generated
/// C++.
///
/// **Every publishable class is listed, not only the ticked ones.** A class id is an agreement
/// between the two ends of the wire; if the list followed the selection, ids would shift as classes
/// were ticked and a number already fielded could quietly come to mean something else.
/// </summary>
public sealed class ClassBindingsForm : Form
{
    readonly ClassBindings _bindings;
    readonly DataGridView _grid = new();
    readonly NumericUpDown _start = new();
    readonly ComboBox _target = new();
    readonly CheckBox _overwrite = new();
    readonly Label _summary = new();

    public ClassBindingsForm(FomModel model, ClassBindings bindings)
    {
        _bindings = bindings;

        Text = "クラスの ID / Port / Rate";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(820, 520);
        MinimumSize = new Size(640, 380);

        var note = new Label
        {
            Dock = DockStyle.Top,
            Height = 56,
            Padding = new Padding(8, 6, 8, 0),
            Text = "ID はデータグラムヘッダの classId で、生成する C++ にも埋め込まれます。"
                 + "両端で一致していないと通信できません。\n"
                 + "Port と Rate は ICD シートに書かれるだけで、生成コードには入りません"
                 + "（配備先で変わるものなので、変更に再生成を要求しないためです）。\n"
                 + "空欄のままでも生成はできます。その場合 ID は実行時引数のままになります。"
        };

        var bottom = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 44,
            Padding = new Padding(8, 8, 8, 8)
        };

        _target.DropDownStyle = ComboBoxStyle.DropDownList;
        _target.Items.AddRange(new object[] { "ID", "Port" });
        _target.SelectedIndex = 0;
        _target.Width = 70;

        _start.Minimum = 0;
        _start.Maximum = 65535;
        _start.Value = 1;
        _start.Width = 80;

        var fill = new Button { Text = "連番を振る", Width = 100 };
        fill.Click += (_, _) => AutoNumber();

        _overwrite.Text = "既存の値も上書き";
        _overwrite.AutoSize = true;
        _overwrite.Padding = new Padding(8, 4, 8, 0);

        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Width = 90 };
        var cancel = new Button { Text = "キャンセル", DialogResult = DialogResult.Cancel, Width = 90 };

        _summary.AutoSize = true;
        _summary.Padding = new Padding(16, 6, 16, 0);

        bottom.Controls.Add(new Label { Text = "対象:", AutoSize = true, Padding = new Padding(0, 6, 4, 0) });
        bottom.Controls.Add(_target);
        bottom.Controls.Add(new Label { Text = "開始:", AutoSize = true, Padding = new Padding(8, 6, 4, 0) });
        bottom.Controls.Add(_start);
        bottom.Controls.Add(fill);
        bottom.Controls.Add(_overwrite);
        bottom.Controls.Add(_summary);
        bottom.Controls.Add(ok);
        bottom.Controls.Add(cancel);

        _grid.Dock = DockStyle.Fill;
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.RowHeadersVisible = false;
        _grid.SelectionMode = DataGridViewSelectionMode.CellSelect;
        _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _grid.Columns.Add(Column("name", "クラス", readOnly: true, fill: 58));
        _grid.Columns.Add(Column("kind", "種別", readOnly: true, fill: 14));
        _grid.Columns.Add(Column("id", "ID", readOnly: false, fill: 9));
        _grid.Columns.Add(Column("port", "Port", readOnly: false, fill: 10));
        _grid.Columns.Add(Column("rate", "Rate(Hz)", readOnly: false, fill: 12));
        _grid.CellEndEdit += (_, e) => Commit(e.RowIndex);

        Controls.Add(_grid);
        Controls.Add(bottom);
        Controls.Add(note);
        AcceptButton = ok;
        CancelButton = cancel;

        Populate(model);
    }

    static DataGridViewTextBoxColumn Column(string name, string header, bool readOnly, int fill) =>
        new()
        {
            Name = name,
            HeaderText = header,
            ReadOnly = readOnly,
            FillWeight = fill,
            SortMode = DataGridViewColumnSortMode.NotSortable
        };

    /// <summary>
    /// Publishable classes in the FOM's own declaration order, objects then interactions.
    ///
    /// The order matters: it is what "start at N and number the rest" numbers along, so the same
    /// FOM always produces the same ids from the same starting value.
    /// </summary>
    void Populate(FomModel model)
    {
        foreach (var cls in model.AllObjectClasses.Where(c => c.IsPublishable))
        {
            AddRow(cls.FullName, "オブジェクト");
        }
        foreach (var cls in model.AllInteractionClasses.Where(c => c.IsPublishable))
        {
            AddRow(cls.FullName, "インタラクション");
        }
        Recalculate();
    }

    void AddRow(string fullName, string kind)
    {
        var binding = _bindings.For(fullName);
        int row = _grid.Rows.Add(fullName, kind,
            binding?.Id?.ToString() ?? "",
            binding?.Port?.ToString() ?? "",
            binding?.Rate?.ToString() ?? "");
        _grid.Rows[row].Tag = fullName;
    }

    void Commit(int row)
    {
        if (row < 0) return;

        var fullName = (string)_grid.Rows[row].Tag!;
        var binding = _bindings.For(fullName)?.Clone() ?? new ClassBinding();

        // The wire field is a uint32, but the value is kept in an int, so the ceiling is int's.
        // No ICD is going to need more than a couple of hundred class ids anyway.
        if (!TryRead(row, "id", 0, int.MaxValue, out var id)) { Restore(row, binding); return; }
        if (!TryRead(row, "port", 1, 65535, out var port)) { Restore(row, binding); return; }
        if (!TryRead(row, "rate", 0, 10000, out var rate)) { Restore(row, binding); return; }

        binding.Id = id;
        binding.Port = port;
        binding.Rate = rate;
        _bindings.Set(fullName, binding);

        Restore(row, binding);
        Recalculate();
    }

    /// <summary>Reads one cell, empty meaning "not set". False when the text is not a value in range.</summary>
    bool TryRead(int row, string column, long min, long max, out int? value)
    {
        value = null;
        var text = _grid.Rows[row].Cells[column].Value?.ToString()?.Trim() ?? "";
        if (text.Length == 0) return true;

        if (long.TryParse(text, out var parsed) && parsed >= min && parsed <= max)
        {
            value = (int)parsed;
            return true;
        }

        // Reverting in silence would leave a number on screen that is not in force.
        MessageBox.Show(this,
            $"{_grid.Columns[column].HeaderText} は {min}〜{max} の整数で入力してください（空欄なら未設定）。",
            Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        return false;
    }

    void Restore(int row, ClassBinding binding)
    {
        _grid.Rows[row].Cells["id"].Value = binding.Id?.ToString() ?? "";
        _grid.Rows[row].Cells["port"].Value = binding.Port?.ToString() ?? "";
        _grid.Rows[row].Cells["rate"].Value = binding.Rate?.ToString() ?? "";
    }

    /// <summary>
    /// Numbers the chosen column down the list from the starting value.
    ///
    /// Blanks only unless 上書き is ticked, which is what keeps an id stable once it has been
    /// fielded: adding a class later gives it the next free number instead of shifting everyone
    /// below it onto numbers that already mean something else.
    /// </summary>
    void AutoNumber()
    {
        bool port = _target.SelectedIndex == 1;
        bool overwrite = _overwrite.Checked;
        long next = (long)_start.Value;
        int filled = 0;

        foreach (DataGridViewRow row in _grid.Rows)
        {
            var fullName = (string)row.Tag!;
            var binding = _bindings.For(fullName)?.Clone() ?? new ClassBinding();
            var current = port ? binding.Port : binding.Id;

            if (current is not null && !overwrite)
            {
                // Skipping past a number already in use keeps the run free of collisions.
                if (current.Value >= next) next = current.Value + 1;
                continue;
            }

            if (next > 65535) break;

            if (port) binding.Port = (int)next;
            else binding.Id = (int)next;

            _bindings.Set(fullName, binding);
            Restore(row.Index, binding);
            ++next;
            ++filled;
        }

        Recalculate();
        _summary.Text = $"{filled} 件に採番　" + _summary.Text;
    }

    void Recalculate()
    {
        var duplicateIds = _bindings.DuplicateIds();
        var duplicatePorts = _bindings.DuplicatePorts();

        foreach (DataGridViewRow row in _grid.Rows)
        {
            var binding = _bindings.For((string)row.Tag!);

            // A class with nothing set is not an error — it simply is not carried on this ICD —
            // so it is greyed rather than flagged.
            row.DefaultCellStyle.ForeColor =
                binding is null ? SystemColors.GrayText : SystemColors.ControlText;

            Flag(row, "id", binding?.Id is int id && duplicateIds.Contains(id));
            Flag(row, "port", binding?.Port is int p && duplicatePorts.Contains(p));
        }

        var assigned = _bindings.AssignedCount;
        var text = $"{_grid.Rows.Count} クラス中 {assigned} 件に ID";

        if (duplicateIds.Count > 0) text += $"　⚠ ID重複: {string.Join(", ", duplicateIds)}";
        if (duplicatePorts.Count > 0) text += $"　⚠ Port重複: {string.Join(", ", duplicatePorts)}";

        _summary.Text = text;
        _summary.ForeColor = duplicateIds.Count + duplicatePorts.Count > 0
            ? Color.Firebrick
            : SystemColors.ControlText;
    }

    static void Flag(DataGridViewRow row, string column, bool duplicate)
    {
        row.Cells[column].Style.BackColor = duplicate ? Color.MistyRose : Color.Empty;
    }
}
