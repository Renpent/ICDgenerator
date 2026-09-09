using ICDgenerator.Fom;

namespace ICDgenerator.Excel;

/// <summary>Identifies one class the user picked for extraction.</summary>
/// <param name="IsInteraction">false for an object class.</param>
public sealed record IcdSelection(string FullName, bool IsInteraction)
{
    public string ShortName => FullName[(FullName.LastIndexOf('.') + 1)..];
}

public static partial class IcdExporter
{
    /// <summary>One line of the 抽出概要 index.</summary>
    sealed record IndexRow(string Display, object? Size, string Description)
    {
        public string SheetName { get; init; } = "";
    }

    /// <summary>
    /// Writes the extraction view: an index listing each selected class, and one detail sheet per
    /// class enumerating everything it puts on the wire. The index links to the detail sheets.
    ///
    /// ID / Port / Rate have no counterpart in a FOM — HLA carries no port, and update rates are
    /// rarely declared — so they are emitted blank for the reader to fill in.
    /// </summary>
    static void WriteSelectionSheets(XlsxWorkbook workbook, FomModel model, FomTypeResolver resolver,
        IReadOnlyList<IcdSelection> selections, ArrayLimits limits)
    {
        if (selections.Count == 0) return;

        var flattener = new FomFlattener(model, resolver, limits);
        var index = workbook.AddSheet("抽出概要");

        index.AddHeader("Name", "ID", "Port", "Rate", "Size(Bytes)", "Descripter");

        // Sheets must exist before the index can link to them, and the index must come first in the
        // workbook, so add the index sheet up front and fill in its rows as the details are built.
        var rows = new List<IndexRow>();

        // A detail sheet names enumerated types but cannot say what values they take, which is
        // exactly what someone implementing the layout needs. Collected here so the listing that
        // follows covers the selected classes only, instead of the FOM's several thousand values.
        var enumUsage = new SortedDictionary<string, SortedSet<string>>(StringComparer.Ordinal);

        WriteHeaderSheet(workbook);

        for (int i = 0; i < selections.Count; i++)
        {
            var detail = workbook.AddSheet(selections[i].ShortName);

            // Row of this class on the index, counting its header row.
            int indexRow = i + 2;

            rows.Add(WriteDetailSheet(detail, model, resolver, flattener, selections[i], enumUsage,
                index.Name, indexRow) with
            {
                SheetName = detail.Name
            });
        }

        WriteSelectedEnumeratorSheet(workbook, model, resolver, enumUsage);

        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            index.AddRow(row.Display, null, null, null, row.Size, row.Description);
            index.LinkToSheet(i + 2, 1, row.SheetName);
        }

        index.FreezeHeader = true;
        // ID / Port / Rate are blank for the reader to fill in, so fitting them to an empty
        // column would leave nowhere to type. Everything else fits its content.
        index.SetColumnWidth(2, 12);
        index.SetColumnWidth(3, 12);
        index.SetColumnWidth(4, 12);
        index.WrapColumn(6);
    }

    /// <param name="indexSheet">Sheet holding this class's ID / Port / Rate.</param>
    /// <param name="indexRow">Row it occupies there.</param>
    static IndexRow WriteDetailSheet(XlsxSheet sheet, FomModel model, FomTypeResolver resolver,
        FomFlattener flattener, IcdSelection selection,
        IDictionary<string, SortedSet<string>> enumUsage, string indexSheet, int indexRow)
    {
        // ID / Port / Rate are entered by hand on 抽出概要, so this sheet points at them rather than
        // holding a second copy: filling one in there shows up here, and the two cannot disagree.
        sheet.AddRow("ID", XlsxRef.ToCell(indexSheet, "B", indexRow));
        sheet.AddRow("Port", XlsxRef.ToCell(indexSheet, "C", indexRow));
        sheet.AddRow("Rate", XlsxRef.ToCell(indexSheet, "D", indexRow));
        sheet.AddRow();

        // Only what the reader needs to lay bytes out. Transportation, 信頼配送 and Order describe
        // how a member is delivered rather than what it looks like on the wire, and the class-wide
        // value on 抽出概要 answers that; 選択子 stays out because the deployment FOM has no variant
        // records, so it would be blank on every row. FlatField still carries all four, so a sheet
        // that wants them back only has to render them.
        sheet.AddHeader("Name", "Type", "Size(Bytes)", "Amount", "繰り返し", "領域(Bytes)",
                        "長さ決定", "Units", "Description");

        var interaction = selection.IsInteraction
            ? model.AllInteractionClasses.First(c => c.FullName == selection.FullName)
            : null;
        var objectClass = interaction is null
            ? model.AllObjectClasses.First(c => c.FullName == selection.FullName)
            : null;

        var members = interaction is not null
            ? interaction.AllParameters.Select(p => (p.Name, p.DataType, p.Semantics))
            : objectClass!.AllAttributes.Select(a => (a.Name, a.DataType, a.Semantics));

        // Every record is a fixed-size box, so this sum is the record's size rather than a worst
        // case: each row's 領域(Bytes) is the space it always occupies, ceilings folded in. Variant
        // alternatives are the exception — mutually exclusive, so the box holds the largest of them
        // and adding them all would overstate it. Grouping by the outermost selector picks that
        // largest; alternatives nested inside one another are still summed within their group, which
        // can only err upward.
        long worstCase = 0;
        var alternatives = new Dictionary<string, long>(StringComparer.Ordinal);
        bool fixedSize = true;

        foreach (var (name, dataType, semantics) in members)
        {
            foreach (var field in flattener.Flatten(name, dataType, semantics))
            {
                if (model.TryGetDataType(field.TypeName, out var fieldType)
                    && fieldType.Kind == FomDataTypeKind.Enumerated)
                {
                    if (!enumUsage.TryGetValue(field.TypeName, out var users))
                    {
                        enumUsage[field.TypeName] = users = new SortedSet<string>(StringComparer.Ordinal);
                    }
                    users.Add(selection.ShortName);
                }

                if (field.MaxBytes is not long max)
                {
                    fixedSize = false;
                }
                else if (field.Selector.Length == 0)
                {
                    worstCase += max;
                }
                else
                {
                    var branch = field.Selector.Split(" / ")[0];
                    alternatives[branch] = alternatives.GetValueOrDefault(branch) + max;
                }

                sheet.AddRow(
                    field.Path,
                    field.TypeName,
                    field.SizeBytes is int bytes ? bytes : "可変",
                    field.Amount,
                    field.Repeat,
                    field.MaxBytes,
                    field.LengthRule,
                    // The FOM writes "NA" where a type has no units; that placeholder is noise in a
                    // working layout sheet, though データ型定義 still reproduces it verbatim.
                    field.Units == "NA" ? "" : field.Units,
                    field.Semantics);
            }

        }

        if (alternatives.Count > 0) worstCase += alternatives.Values.Max();

        // Freeze through the identification block and the table's own header.
        sheet.FrozenRows = 5;
        // No autofilter: the rows are in wire order, and anything that reorders or hides them makes
        // the sheet say something the datagram does not.
        sheet.WrapColumn(9);

        return new IndexRow(
            selection.ShortName,
            fixedSize ? worstCase : "要確認",
            interaction?.Semantics ?? objectClass!.Semantics);
    }

    /// <summary>
    /// The bytes that precede every record, which no per-class sheet can show because they are
    /// common to all of them. Same columns as a detail sheet so the two read the same way.
    /// </summary>
    static void WriteHeaderSheet(XlsxWorkbook workbook)
    {
        var sheet = workbook.AddSheet("ヘッダ");

        sheet.AddRow("データグラム", "同一クラスのレコードを詰めた1つのUDPペイロード。"
            + "レコードは全て同じ長さ（可変長配列は上限まで領域を取る）。ヘッダは12バイト。");
        sheet.AddRow("ペイロード上限", "1400（MTU 1500）/ 8900（MTU 9000）");
        sheet.AddRow("バイトオーダー",
            "ビッグエンディアン（ネットワークバイトオーダー）。合意事項として固定で、"
            + "マジックワードもエンディアンフラグも持たない。");
        sheet.AddRow();

        sheet.AddHeader("Name", "Type", "Size(Bytes)", "Amount", "繰り返し", "領域(Bytes)",
                        "長さ決定", "Units", "Description");

        sheet.AddRow("classId", "uint32", 4, "1", "", 4, "固定", "",
            "抽出概要シートの ID 列の値。ポートで分けていても、ポート設定ミスはこれで捕まる。");
        sheet.AddRow("recordCount", "uint32", 4, "1", "", 4, "固定", "",
            "後続のレコード件数。");
        sheet.AddRow("recordSize", "uint32", 4, "1", "", 4, "固定", "",
            "レコード1件のバイト数。全レコードが同じ長さなので、長さはここに1回書けば足りる。"
            + "k番目のレコードは 12 + k * recordSize の位置にあり、前を読み飛ばさずに取り出せる。"
            + "送信側が後のICDで属性を増やしていればこの値が大きくなるので、"
            + "知っている分だけ読んで残りを飛ばせる。");
        sheet.AddRow("<レコード本体>", "", "recordSize", "1", "i = 0..recordCount-1", null, "固定", "",
            "クラスごとの詳細シートを参照。中身はそのシートの行順に並び、間に区切りは入らない。");

        sheet.FrozenRows = 5;
        sheet.WrapColumn(9);
    }

    /// <summary>
    /// Lists the values of every enumerated type the selected classes actually reach. The full
    /// 列挙値一覧 sheet still carries the whole FOM; this one stays readable by covering only what
    /// the extracted layout refers to.
    /// </summary>
    static void WriteSelectedEnumeratorSheet(XlsxWorkbook workbook, FomModel model,
        FomTypeResolver resolver, IReadOnlyDictionary<string, SortedSet<string>> enumUsage)
    {
        if (enumUsage.Count == 0) return;

        var sheet = workbook.AddSheet("抽出列挙値");
        sheet.AddHeader("列挙型", "使用クラス", "基本表現", "Size(Bytes)", "列挙子名", "値",
                        "Notes", "型のSemantics");

        foreach (var (typeName, users) in enumUsage)
        {
            if (!model.TryGetDataType(typeName, out var type)) continue;

            var resolved = resolver.Resolve(typeName);
            var usedBy = string.Join(", ", users);

            foreach (var enumerator in type.Enumerators)
            {
                sheet.AddRow(
                    typeName,
                    usedBy,
                    resolved.BaseRepresentation,
                    resolved.SizeInBytes,
                    enumerator.Name,
                    // Values are plain integers throughout, so emit them as numbers.
                    long.TryParse(enumerator.Value, out var value) ? value : enumerator.Value,
                    ResolveNotes(model, enumerator.Notes),
                    type.Semantics);
            }
        }

        sheet.FreezeHeader = true;
        sheet.WrapColumn(7);
        sheet.WrapColumn(8);
    }

}
