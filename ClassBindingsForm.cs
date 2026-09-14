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
/// between the two ends of the wire, and one already fielded must stay put; being able to see and
/// edit any class's id — and to spot a duplicate against a class not currently ticked — is what
/// keeps that possible.
///
/// Numbering, though, works on what is *shown*: the list starts filtered to the ticked classes,
/// because those are the ones being carried, and untucking the filter widens the run to everything.
/// One rule — "連番を振る numbers the rows you can see" — rather than a separate scope to reason
/// about.
/// </summary>
public sealed class ClassBindingsForm : Form
{
    readonly ClassBindings _bindings;
    readonly HashSet<string> _selected;
    readonly DataGridView _grid = new();
    readonly NumericUpDown _start = new();
    readonly ComboBox _target = new();
    readonly CheckBox _overwrite = new();
    readonly CheckBox _selectedOnly = new();
    readonly Label _summary = new();
    readonly ToolTip _tip = new();

    /// <summary>
    /// Asks before a bulk clear. A field rather than a call so the off-screen render-and-check
    /// harness can answer it — a modal dialog has nothing to click it.
    /// </summary>
    Func<string, bool> _confirm;

    /// <param name="selected">
    /// Full names of the classes ticked in the main window. Numbering starts scoped to these.
    /// </param>
    public ClassBindingsForm(FomModel model, ClassBindings bindings, IEnumerable<string> selected)
    {
        _bindings = bindings;
        _selected = new HashSet<string>(selected, StringComparer.Ordinal);
        _confirm = question => MessageBox.Show(this, question, Text,
            MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) == DialogResult.OK;

        Text = "クラスの ID / Port / Rate";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(820, 560);
        MinimumSize = new Size(780, 420);

        var note = new Label
        {
            Dock = DockStyle.Top,
            Height = 56,
            Padding = new Padding(8, 6, 8, 0),
            Text = "ID はデータグラムヘッダの classId で、生成する C++ にも埋め込まれます。"
                 + "両端で一致していないと通信できません。\n"
                 + "Port と Rate は ICD シートに書かれるだけで、生成コードには入りません"
                 + "（配備先で変わるものなので、変更に再生成を要求しないためです）。\n"
                 + "「連番を振る」「消去」はどちらも表示中の行が対象です。既定では選択したクラスだけが表示されています。"
        };

        // Two rows, not one. A single FlowLayoutPanel held everything, and at the default width the
        // buttons wrapped onto a second line the panel was too short to show — OK sat off the bottom
        // until the window was widened. Splitting the controls from the buttons means neither row
        // can push the other out of view.
        var numbering = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 40,
            WrapContents = false,
            Padding = new Padding(8, 6, 8, 0)
        };

        // Docked, not flowed and not anchored. Flowing put the summary — which grows when it has
        // duplicates to name — onto a second line the panel was too short to show, so it vanished;
        // anchoring it Left|Right then stretched it by however much the panel grew when *it* was
        // docked, which is 620px it did not have when the size was computed, and the label ended up
        // covering the buttons. Docking asks for neither number: the buttons take the right edge,
        // the summary takes what is left, whatever the width turns out to be.
        var buttons = new Panel { Dock = DockStyle.Bottom, Height = 44 };
        var buttonFlow = new FlowLayoutPanel
        {
            Dock = DockStyle.Right,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            AutoSize = true,
            Padding = new Padding(0, 8, 8, 8)
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

        var clear = new Button { Text = "消去", Width = 70 };
        clear.Click += (_, _) => ClearNumbers();

        _overwrite.Text = "既存の値も上書き";
        _overwrite.AutoSize = true;
        _overwrite.Padding = new Padding(8, 4, 8, 0);

        _selectedOnly.Text = "選択したクラスのみ";
        _selectedOnly.AutoSize = true;
        _selectedOnly.Padding = new Padding(8, 4, 0, 0);
        // Nothing ticked in the main window would filter the list down to nothing, which reads as a
        // broken dialog rather than an empty selection.
        _selectedOnly.Checked = _selected.Count > 0;
        _selectedOnly.Enabled = _selected.Count > 0;
        _selectedOnly.CheckedChanged += (_, _) => ApplyFilter();

        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Size = new Size(90, 26) };
        var cancel = new Button
        {
            Text = "キャンセル",
            DialogResult = DialogResult.Cancel,
            Size = new Size(90, 26)
        };

        // Takes whatever the buttons leave, and truncates rather than growing: a long run of
        // duplicate numbers is cut short with an ellipsis. The full text is on the tooltip, the
        // offending cells are shaded in the grid, and the numbers are logged on save.
        _summary.AutoSize = false;
        _summary.AutoEllipsis = true;
        _summary.Dock = DockStyle.Fill;
        _summary.TextAlign = ContentAlignment.MiddleRight;
        _summary.Padding = new Padding(12, 0, 12, 0);

        numbering.Controls.Add(new Label { Text = "対象:", AutoSize = true, Padding = new Padding(0, 6, 4, 0) });
        numbering.Controls.Add(_target);
        numbering.Controls.Add(new Label { Text = "開始:", AutoSize = true, Padding = new Padding(8, 6, 4, 0) });
        numbering.Controls.Add(_start);
        numbering.Controls.Add(fill);
        numbering.Controls.Add(clear);
        numbering.Controls.Add(_selectedOnly);
        numbering.Controls.Add(_overwrite);

        buttonFlow.Controls.Add(cancel);
        buttonFlow.Controls.Add(ok);

        // Fill first, edge second: docking is applied in reverse of the order added, so the flow
        // claims the right edge before the summary is given the remainder.
        buttons.Controls.Add(_summary);
        buttons.Controls.Add(buttonFlow);

        _grid.Dock = DockStyle.Fill;
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.RowHeadersVisible = false;
        _grid.SelectionMode = DataGridViewSelectionMode.CellSelect;
        _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _grid.Columns.Add(Column("name", "クラス", readOnly: true, fill: 52));
        _grid.Columns.Add(Column("kind", "種別", readOnly: true, fill: 13));
        _grid.Columns.Add(Column("picked", "選択", readOnly: true, fill: 7));
        _grid.Columns.Add(Column("id", "ID", readOnly: false, fill: 9));
        _grid.Columns.Add(Column("port", "Port", readOnly: false, fill: 10));
        _grid.Columns.Add(Column("rate", "Rate(Hz)", readOnly: false, fill: 12));
        _grid.CellEndEdit += (_, e) => Commit(e.RowIndex);

        Controls.Add(_grid);
        Controls.Add(numbering);
        Controls.Add(buttons);
        Controls.Add(note);
        AcceptButton = ok;
        CancelButton = cancel;

        Populate(model);
        ApplyFilter();
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
            _selected.Contains(fullName) ? "✓" : "",
            binding?.Id?.ToString() ?? "",
            binding?.Port?.ToString() ?? "",
            binding?.Rate?.ToString() ?? "");
        _grid.Rows[row].Tag = fullName;
    }

    /// <summary>Shows either the ticked classes or all of them; this is also what numbering walks.</summary>
    void ApplyFilter()
    {
        bool only = _selectedOnly.Checked;

        // A hidden row cannot be the current cell, and leaving it as such throws.
        _grid.CurrentCell = null;

        foreach (DataGridViewRow row in _grid.Rows)
        {
            row.Visible = !only || _selected.Contains((string)row.Tag!);
        }

        Recalculate();
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
    /// Numbers the chosen column down the visible rows from the starting value.
    ///
    /// Blanks only unless 上書き is ticked, which is what keeps an id stable once it has been
    /// fielded: adding a class later gives it the next free number instead of shifting everyone
    /// below it onto numbers that already mean something else.
    ///
    /// Numbers already in use — including on rows the filter is hiding — are stepped over, so a run
    /// scoped to the selection cannot collide with a class that is not currently ticked.
    /// </summary>
    void AutoNumber()
    {
        bool port = _target.SelectedIndex == 1;
        bool overwrite = _overwrite.Checked;
        long next = (long)_start.Value;
        int filled = 0;

        // Numbers that will still be in use after this run: every hidden row keeps its own, and so
        // does every visible one unless 上書き is on. Seeding the set with exactly those is what
        // stops the run landing on one — including a value held further down the list, which simply
        // advancing past what has been seen would walk straight into.
        //
        // It must not include the rows about to be reassigned, or an overwrite would skip past the
        // very numbers it is replacing and start above them instead of at the starting value.
        var taken = new HashSet<int>(_grid.Rows.Cast<DataGridViewRow>()
            .Where(r => !r.Visible || !overwrite)
            .Select(r => _bindings.For((string)r.Tag!))
            .OfType<ClassBinding>()
            .Select(b => port ? b.Port : b.Id)
            .OfType<int>());

        foreach (DataGridViewRow row in _grid.Rows)
        {
            if (!row.Visible) continue;

            var fullName = (string)row.Tag!;
            var binding = _bindings.For(fullName)?.Clone() ?? new ClassBinding();
            var current = port ? binding.Port : binding.Id;

            if (current is not null && !overwrite) continue;

            while (taken.Contains((int)next)) ++next;
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

    /// <summary>
    /// Blanks the chosen column on the visible rows.
    ///
    /// 上書き already lets a run write over existing numbers, but that is not the same thing: a
    /// clear leaves the rows empty, so a following run can number some and leave others unset.
    /// Without it the only way back to a blank column was to empty 122 cells by hand.
    ///
    /// Confirmed rather than immediate. Cancelling the dialog does undo it — the bindings are
    /// edited on a copy — but that also throws away every other edit made since opening, which is a
    /// poor thing to have to reach for after one mis-aimed click.
    /// </summary>
    void ClearNumbers()
    {
        bool port = _target.SelectedIndex == 1;
        var column = port ? "Port" : "ID";

        var rows = _grid.Rows.Cast<DataGridViewRow>()
            .Where(r => r.Visible)
            .Select(r => (Row: r, Binding: _bindings.For((string)r.Tag!)))
            .Where(x => x.Binding is not null && (port ? x.Binding.Port : x.Binding.Id) is not null)
            .ToList();

        if (rows.Count == 0)
        {
            _summary.Text = $"消去する {column} がありません　" + _summary.Text;
            return;
        }

        if (!_confirm($"表示中の {rows.Count} クラスの {column} を消去します。よろしいですか？")) return;

        foreach (var (row, existing) in rows)
        {
            var binding = existing!.Clone();
            if (port) binding.Port = null;
            else binding.Id = null;

            _bindings.Set((string)row.Tag!, binding);
            Restore(row.Index, binding);
        }

        Recalculate();
        _summary.Text = $"{rows.Count} 件の {column} を消去　" + _summary.Text;
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

        int shown = _grid.Rows.Cast<DataGridViewRow>().Count(r => r.Visible);
        var text = $"表示 {shown} / 全 {_grid.Rows.Count} クラス　ID {_bindings.AssignedCount} 件";

        if (duplicateIds.Count > 0) text += $"　⚠ ID重複: {string.Join(", ", duplicateIds)}";
        if (duplicatePorts.Count > 0) text += $"　⚠ Port重複: {string.Join(", ", duplicatePorts)}";

        _summary.Text = text;
        _summary.ForeColor = duplicateIds.Count + duplicatePorts.Count > 0
            ? Color.Firebrick
            : SystemColors.ControlText;
        _tip.SetToolTip(_summary, text);
    }

    static void Flag(DataGridViewRow row, string column, bool duplicate)
    {
        row.Cells[column].Style.BackColor = duplicate ? Color.MistyRose : Color.Empty;
    }
}
