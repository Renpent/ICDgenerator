using ICDgenerator.Fom;

namespace ICDgenerator;

/// <summary>
/// Lets the reader set a ceiling on each dynamic array.
///
/// A FOM gives no upper bound — "Dynamic", or a range whose top is 2^31-1 — so the worst-case size
/// of any class carrying one is unknowable until somebody decides. That decision is a federation
/// agreement, and it is the one that settles whether a class fits an MTU, so it belongs in front of
/// the person generating the ICD rather than buried in a config file.
/// </summary>
public sealed class ArrayLimitsForm : Form
{
    readonly ArrayLimits _limits;
    readonly DataGridView _grid = new();
    readonly NumericUpDown _default = new();
    readonly Label _summary = new();

    /// <summary>Element byte size per array type, so the dialog can show what a limit costs.</summary>
    readonly Dictionary<string, int?> _elementSize = new(StringComparer.Ordinal);

    public ArrayLimitsForm(FomModel model, FomTypeResolver resolver, ArrayLimits limits)
    {
        _limits = limits;

        Text = "配列の要素数上限";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(760, 480);
        MinimumSize = new Size(560, 360);

        var note = new Label
        {
            Dock = DockStyle.Top,
            Height = 40,
            Padding = new Padding(8, 6, 8, 0),
            Text = "FOMは可変長配列の上限を定めていません。レコードは常にこの上限ぶんの領域を取ります。\n"
                 + "空欄の型には既定値が適用されます。実際の要素数は _Count で送られるので、上限は器の大きさです。"
        };

        var defaultPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 44,
            Padding = new Padding(8, 8, 8, 8)
        };

        _default.Minimum = 1;
        _default.Maximum = 65535;
        _default.Value = Math.Clamp(limits.Default, 1, 65535);
        _default.Width = 90;
        _default.ValueChanged += (_, _) => { _limits.Default = (int)_default.Value; Recalculate(); };

        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Width = 90 };
        var cancel = new Button { Text = "キャンセル", DialogResult = DialogResult.Cancel, Width = 90 };

        defaultPanel.Controls.Add(new Label { Text = "既定値:", AutoSize = true, Padding = new Padding(0, 6, 4, 0) });
        defaultPanel.Controls.Add(_default);
        defaultPanel.Controls.Add(_summary);
        defaultPanel.Controls.Add(ok);
        defaultPanel.Controls.Add(cancel);

        _summary.AutoSize = true;
        _summary.Padding = new Padding(16, 6, 16, 0);

        _grid.Dock = DockStyle.Fill;
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.RowHeadersVisible = false;
        _grid.SelectionMode = DataGridViewSelectionMode.CellSelect;
        _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _grid.Columns.Add(Column("type", "配列型", readOnly: true, fill: 40));
        _grid.Columns.Add(Column("source", "出所", readOnly: true, fill: 8));
        _grid.Columns.Add(Column("element", "要素", readOnly: true, fill: 24));
        _grid.Columns.Add(Column("size", "要素Bytes", readOnly: true, fill: 11));
        _grid.Columns.Add(Column("limit", "上限", readOnly: false, fill: 10));
        _grid.Columns.Add(Column("max", "領域Bytes", readOnly: true, fill: 13));
        _grid.CellEndEdit += (_, e) => Commit(e.RowIndex);

        Controls.Add(_grid);
        Controls.Add(defaultPanel);
        Controls.Add(note);
        AcceptButton = ok;
        CancelButton = cancel;

        Populate(model, resolver);
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

    void Populate(FomModel model, FomTypeResolver resolver)
    {
        // Every dynamic array in reach, not only those the current selection uses: a limit is a
        // federation-wide agreement and should not appear and disappear as classes are ticked.
        //
        // The standard MIM's arrays belong here too — HLAASCIIstring, HLAopaqueData and the rest are
        // as unbounded as any the FOM declares, and a class referencing one would otherwise take the
        // default with nothing on screen to say so. A name the FOM redefines is the FOM's.
        var arrays = model.DataTypes.Values.Select(t => (Type: t, Source: "FOM"))
            .Concat(model.StandardDataTypes.Values
                .Where(t => !model.DataTypes.ContainsKey(t.Name))
                .Select(t => (Type: t, Source: "MIM")))
            .Where(x => x.Type.Kind == FomDataTypeKind.Array
                        && !int.TryParse(x.Type.Cardinality, out _))
            .OrderBy(x => x.Source, StringComparer.Ordinal)
            .ThenBy(x => x.Type.Name, StringComparer.Ordinal);

        foreach (var (array, source) in arrays)
        {
            var size = resolver.Resolve(array.ElementDataType).SizeInBytes;
            _elementSize[array.Name] = size;

            int row = _grid.Rows.Add(
                array.Name,
                source,
                array.ElementDataType,
                size is int bytes ? bytes : "可変",
                _limits.IsExplicit(array.Name) ? _limits.For(array.Name).ToString() : "",
                "");

            UpdateRow(row);
        }

        Recalculate();
    }

    void Commit(int row)
    {
        if (row < 0) return;

        var name = (string)_grid.Rows[row].Cells["type"].Value;
        var text = _grid.Rows[row].Cells["limit"].Value?.ToString()?.Trim() ?? "";

        if (text.Length == 0)
        {
            _limits.Set(name, null);
        }
        else if (int.TryParse(text, out var value) && value > 0 && value <= 65535)
        {
            _limits.Set(name, value);
        }
        else
        {
            // Silently reverting would leave a number on screen that is not in force.
            MessageBox.Show(this, "上限は 1〜65535 の整数で入力してください（空欄なら既定値）。",
                "配列の要素数上限", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        _grid.Rows[row].Cells["limit"].Value =
            _limits.IsExplicit(name) ? _limits.For(name).ToString() : "";

        UpdateRow(row);
        Recalculate();
    }

    void UpdateRow(int row)
    {
        var name = (string)_grid.Rows[row].Cells["type"].Value;
        var limit = _limits.For(name);

        _grid.Rows[row].Cells["max"].Value = _elementSize[name] is int size
            ? (long)size * limit + 2  // the 2-byte count travels with the array
            : "可変";  // an element holding an array of its own; its own limit decides the rest

        // A limit left at the default is worth seeing as such, since one edit to the default moves
        // every one of them at once.
        _grid.Rows[row].DefaultCellStyle.ForeColor =
            _limits.IsExplicit(name) ? SystemColors.ControlText : SystemColors.GrayText;
    }

    void Recalculate()
    {
        for (int row = 0; row < _grid.Rows.Count; row++) UpdateRow(row);

        int explicitCount = _grid.Rows.Cast<DataGridViewRow>()
            .Count(r => _limits.IsExplicit((string)r.Cells["type"].Value));

        _summary.Text = $"{_grid.Rows.Count} 型中 {explicitCount} 型に個別設定";
    }
}
