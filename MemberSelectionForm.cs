using ICDgenerator.Cpp;
using ICDgenerator.Excel;
using ICDgenerator.Fom;

namespace ICDgenerator;

/// <summary>
/// Lets the reader leave attributes or parameters of a ticked class off the wire.
///
/// A FOM class carries everything any federate might want; a gateway usually needs a few of them,
/// and a class with several large fixed arrays can exceed a datagram outright. Trimming it here —
/// rather than editing the generated struct — keeps the sheet, the size check and the C++ all
/// describing the same record, and survives regeneration because it is saved with the bindings.
///
/// Only the ticked classes are listed, those being the ones about to be generated. Exclusions on
/// other classes are kept as they are.
///
/// Each member shows the bytes it occupies, and the footer the record's total against the payload
/// for the chosen MTU, so it is clear which ones to drop before generating rather than after a refusal.
/// </summary>
public sealed class MemberSelectionForm : Form
{
    /// <summary>One attribute or parameter, and the space it takes on the wire.</summary>
    sealed record MemberItem(string Name, string DataType, long? Bytes)
    {
        public override string ToString() =>
            $"{Name}    {(Bytes is long b ? $"{b:N0} B" : "可変")}    {DataType}";
    }

    /// <summary>One ticked class and every member it declares, excluded or not.</summary>
    sealed class ClassItem
    {
        public required IcdSelection Selection { get; init; }
        public required List<MemberItem> Members { get; init; }
        public string Summary { get; set; } = "";
        public override string ToString() => Summary;
    }

    readonly ClassBindings _bindings;
    readonly int _room;
    readonly ListBox _classes = new();
    readonly CheckedListBox _members = new();
    readonly Label _summary = new();

    /// <summary>Set while the member list is being refilled, so doing so does not count as editing.</summary>
    bool _loading;

    public MemberSelectionForm(FomModel model, FomTypeResolver resolver, ArrayLimits limits,
        ClassBindings bindings, IReadOnlyList<IcdSelection> selected)
    {
        _bindings = bindings;
        _room = PayloadBytes(bindings.Mtu) - CppGenerator.RuntimeConstants.HeaderSize;

        Text = "送る属性・パラメータ";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(900, 580);
        MinimumSize = new Size(720, 420);

        var note = new Label
        {
            Dock = DockStyle.Top,
            Height = 56,
            Padding = new Padding(8, 6, 8, 0),
            Text = "チェックを外した属性・パラメータは送りません。抽出シートの行、1レコードのサイズ、生成する C++ の構造体から除かれます。\n"
                 + "並び順は FOM のままで、外した分だけ詰まります。設定は ID/Port/Rate と同じファイルに保存されます。\n"
                 + $"上限は MTU {bindings.Mtu} の1データグラムからヘッダ12バイトを除いた {_room:N0} B です（MTU は ID/Port/Rate の画面で変えられます）。"
        };

        var flattener = new FomFlattener(model, resolver, limits);
        foreach (var selection in selected)
        {
            var members = selection.IsInteraction
                ? model.AllInteractionClasses.First(c => c.FullName == selection.FullName)
                    .AllParameters.Select(p => (p.Name, p.DataType, p.Semantics))
                : model.AllObjectClasses.First(c => c.FullName == selection.FullName)
                    .AllAttributes.Select(a => (a.Name, a.DataType, a.Semantics));

            var item = new ClassItem
            {
                Selection = selection,
                Members = members
                    .Select(m => new MemberItem(m.Name, m.DataType,
                        FomFlattener.WireBytes(flattener.Flatten(m.Name, m.DataType, m.Semantics))))
                    .ToList()
            };
            item.Summary = Describe(item);
            _classes.Items.Add(item);
        }

        _classes.Dock = DockStyle.Left;
        _classes.Width = 320;
        _classes.IntegralHeight = false;
        _classes.SelectedIndexChanged += (_, _) => ShowMembers();
        // Drawn by hand so a line can change by invalidating it. A plain ListBox caches each item's
        // text, and the usual way round that — reassigning the item — resets the selection, which
        // would refill the member list from inside its own check event.
        _classes.DrawMode = DrawMode.OwnerDrawFixed;
        _classes.DrawItem += (_, e) =>
        {
            if (e.Index < 0) return;
            e.DrawBackground();
            var item = (ClassItem)_classes.Items[e.Index];
            var selected = (e.State & DrawItemState.Selected) != 0;
            var color = selected ? SystemColors.HighlightText
                : item.Summary.StartsWith("✕") ? Color.Firebrick : SystemColors.ControlText;
            TextRenderer.DrawText(e.Graphics, item.Summary, e.Font, e.Bounds, color,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            e.DrawFocusRectangle();
        };

        _members.Dock = DockStyle.Fill;
        _members.CheckOnClick = true;
        _members.IntegralHeight = false;
        // ItemCheck fires before the box changes, so the new state is read from the event, not the box.
        _members.ItemCheck += (_, e) =>
        {
            if (_loading || Current is not ClassItem cls) return;
            var member = (MemberItem)_members.Items[e.Index];
            var excluded = cls.Members
                .Where(m => m == member ? e.NewValue != CheckState.Checked
                                        : _bindings.IsExcluded(cls.Selection.FullName, m.Name))
                .Select(m => m.Name);
            _bindings.SetExcluded(cls.Selection.FullName, excluded);
            UpdateSummary(cls);
        };

        var all = new Button { Text = "全て送る", Size = new Size(90, 26) };
        all.Click += (_, _) =>
        {
            if (Current is not ClassItem cls) return;
            _bindings.SetExcluded(cls.Selection.FullName, Array.Empty<string>());
            ShowMembers();
        };

        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Size = new Size(90, 26) };
        var cancel = new Button { Text = "キャンセル", DialogResult = DialogResult.Cancel, Size = new Size(90, 26) };

        // Same arrangement as the bindings dialog, for the same reason: the buttons dock to the
        // right edge and the summary takes what is left, so neither can push the other out of view.
        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 44 };
        var buttonFlow = new FlowLayoutPanel
        {
            Dock = DockStyle.Right,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            AutoSize = true,
            Padding = new Padding(0, 8, 8, 8)
        };
        buttonFlow.Controls.Add(cancel);
        buttonFlow.Controls.Add(ok);
        buttonFlow.Controls.Add(all);

        _summary.AutoSize = false;
        _summary.AutoEllipsis = true;
        _summary.Dock = DockStyle.Fill;
        _summary.TextAlign = ContentAlignment.MiddleLeft;
        _summary.Padding = new Padding(12, 0, 12, 0);

        bottom.Controls.Add(_summary);
        bottom.Controls.Add(buttonFlow);

        // Fill first, edges after: docking is applied in reverse of the order added.
        Controls.Add(_members);
        Controls.Add(new Splitter { Dock = DockStyle.Left, Width = 4 });
        Controls.Add(_classes);
        Controls.Add(bottom);
        Controls.Add(note);

        AcceptButton = ok;
        CancelButton = cancel;

        if (_classes.Items.Count > 0) _classes.SelectedIndex = FirstOversized();
    }

    ClassItem? Current => _classes.SelectedItem as ClassItem;

    /// <summary>Opens on the class that needs attention, when there is one.</summary>
    int FirstOversized()
    {
        for (int i = 0; i < _classes.Items.Count; i++)
        {
            if (SentBytes((ClassItem)_classes.Items[i]) is long b && b > _room) return i;
        }
        return 0;
    }

    void ShowMembers()
    {
        _loading = true;
        _members.BeginUpdate();
        _members.Items.Clear();
        if (Current is ClassItem cls)
        {
            foreach (var member in cls.Members)
            {
                _members.Items.Add(member, !_bindings.IsExcluded(cls.Selection.FullName, member.Name));
            }
            UpdateSummary(cls);
        }
        _members.EndUpdate();
        _loading = false;
    }

    /// <summary>Recomputes one class's line on the left and the footer.</summary>
    void UpdateSummary(ClassItem cls)
    {
        cls.Summary = Describe(cls);
        _classes.Invalidate();

        var sent = cls.Members.Count(m => !_bindings.IsExcluded(cls.Selection.FullName, m.Name));
        var bytes = SentBytes(cls);
        _summary.Text = $"{cls.Selection.ShortName}：送る {sent} / {cls.Members.Count}　"
            + (bytes is long b ? $"1レコード {b:N0} B / 上限 {_room:N0} B" : "1レコード 要確認（可変長の項目あり）")
            + (bytes > _room ? $"　{bytes - _room:N0} B 超過" : "")
            + (sent == 0 ? "　送るものが無いと生成できません" : "");
        _summary.ForeColor = bytes > _room || sent == 0 ? Color.Firebrick : SystemColors.ControlText;
    }

    string Describe(ClassItem cls)
    {
        var bytes = SentBytes(cls);
        var mark = bytes > _room ? "✕ " : "";
        var kind = cls.Selection.IsInteraction ? "I" : "O";
        return $"{mark}[{kind}] {cls.Selection.ShortName}    {(bytes is long b ? $"{b:N0} B" : "要確認")}";
    }

    /// <summary>The record's size with the current exclusions; null when a member has no fixed size.</summary>
    long? SentBytes(ClassItem cls)
    {
        long total = 0;
        foreach (var member in cls.Members)
        {
            if (_bindings.IsExcluded(cls.Selection.FullName, member.Name)) continue;
            if (member.Bytes is not long b) return null;
            total += b;
        }
        return total;
    }

    /// <summary>The two MTUs the runtime has a payload constant for; anything else reads as the standard one.</summary>
    static int PayloadBytes(int mtu) => mtu == 9000
        ? CppGenerator.RuntimeConstants.JumboPayload
        : CppGenerator.RuntimeConstants.DefaultPayload;
}
