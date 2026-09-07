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
    sealed record IndexRow(string Display, object? Size, string Transportation,
        string Reliability, string Description)
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
        IReadOnlyList<IcdSelection> selections)
    {
        if (selections.Count == 0) return;

        var flattener = new FomFlattener(model, resolver);
        var index = workbook.AddSheet("抽出概要");

        index.AddHeader("Name", "ID", "Port", "Rate", "Size(Bytes)",
                        "Transportation", "信頼配送", "Descripter");

        // Sheets must exist before the index can link to them, and the index must come first in the
        // workbook, so add the index sheet up front and fill in its rows as the details are built.
        var rows = new List<IndexRow>();

        // A detail sheet names enumerated types but cannot say what values they take, which is
        // exactly what someone implementing the layout needs. Collected here so the listing that
        // follows covers the selected classes only, instead of the FOM's several thousand values.
        var enumUsage = new SortedDictionary<string, SortedSet<string>>(StringComparer.Ordinal);

        foreach (var selection in selections)
        {
            var detail = workbook.AddSheet(selection.ShortName);
            rows.Add(WriteDetailSheet(detail, model, resolver, flattener, selection, enumUsage) with
            {
                SheetName = detail.Name
            });
        }

        WriteSelectedEnumeratorSheet(workbook, model, resolver, enumUsage);

        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            index.AddRow(row.Display, null, null, null, row.Size,
                row.Transportation, row.Reliability, row.Description);
            index.LinkToSheet(i + 2, 1, row.SheetName);
        }

        index.FreezeHeader = true;
        index.AutoFilter = true;
        ApplyWidths(index, 34, 12, 12, 12, 14, 20, 12, ProseWidth);
        index.WrapColumn(8);
    }

    static IndexRow WriteDetailSheet(XlsxSheet sheet, FomModel model, FomTypeResolver resolver,
        FomFlattener flattener, IcdSelection selection,
        IDictionary<string, SortedSet<string>> enumUsage)
    {
        sheet.AddHeader("Name", "Type", "Size(Bytes)", "Amount", "長さ決定", "Units", "選択子",
                        "Transportation", "信頼配送", "Order", "Description");

        // Interactions declare transportation once for the class; object classes declare it per
        // attribute. Carrying it with each member covers both without assuming they agree.
        var interaction = selection.IsInteraction
            ? model.AllInteractionClasses.First(c => c.FullName == selection.FullName)
            : null;
        var objectClass = interaction is null
            ? model.AllObjectClasses.First(c => c.FullName == selection.FullName)
            : null;

        var members = interaction is not null
            ? interaction.AllParameters.Select(p =>
                (p.Name, p.DataType, p.Semantics, interaction.Transportation, interaction.Order))
            : objectClass!.AllAttributes.Select(a =>
                (a.Name, a.DataType, a.Semantics, a.Transportation, a.Order));

        int totalBytes = 0;
        bool fixedSize = true;
        var transportations = new List<string>();

        foreach (var (name, dataType, semantics, transportation, order) in members)
        {
            if (transportation.Length > 0) transportations.Add(transportation);

            bool topRow = true;
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

                // Transportation applies to the member as a whole, so it is stated once on its top
                // row rather than repeated down the expansion.
                sheet.AddRow(
                    field.Path,
                    field.TypeName,
                    field.SizeBytes is int bytes ? bytes : "可変",
                    field.Amount,
                    field.LengthRule,
                    // The FOM writes "NA" where a type has no units; that placeholder is noise in a
                    // working layout sheet, though データ型定義 still reproduces it verbatim.
                    field.Units == "NA" ? "" : field.Units,
                    field.Selector,
                    topRow ? transportation : "",
                    topRow ? ReliabilityText(model, transportation) : "",
                    topRow ? order : "",
                    field.Semantics);

                topRow = false;
            }

            // The class total comes from the member's own resolved width, not from the expanded
            // rows: composites deliberately leave their size blank once they are broken open.
            if (resolver.Resolve(dataType).SizeInBits is int bits) totalBytes += bits / 8;
            else fixedSize = false;
        }

        sheet.FreezeHeader = true;
        sheet.AutoFilter = true;
        // Paths run long now that they carry the whole structure, so Name gets the width back.
        ApplyWidths(sheet, 62, 34, 13, 26, 22, 24, 24, 17, 10, 11, ProseWidth);
        sheet.WrapColumn(11);

        var distinct = transportations.Distinct(StringComparer.Ordinal).ToList();

        return new IndexRow(
            selection.ShortName,
            fixedSize ? totalBytes : "可変",
            // One value for the class where the members agree; say so rather than pick one when not.
            distinct.Count switch { 0 => "", 1 => distinct[0], _ => "混在" },
            distinct.Count == 1 ? ReliabilityText(model, distinct[0])
                : distinct.Any(t => model.IsReliable(t) == true) ? "一部要"
                : "",
            interaction?.Semantics ?? objectClass!.Semantics);
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
        sheet.AutoFilter = true;
        ApplyWidths(sheet, 38, 30, 24, 13, 44, 12, 34, ProseWidth);
        sheet.WrapColumn(7);
        sheet.WrapColumn(8);
    }

    /// <summary>
    /// Reads reliability off the model, so a FOM declaring its own transportation types is honoured.
    /// Reports 不明 rather than guessing when neither the FOM nor the HLA standard names say.
    /// </summary>
    static string ReliabilityText(FomModel model, string transportation) =>
        model.IsReliable(transportation) switch
        {
            true => "要",
            false => "不要",
            _ => transportation.Length > 0 ? "不明" : ""
        };
}
