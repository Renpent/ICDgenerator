using ICDgenerator.Fom;

namespace ICDgenerator.Excel;

/// <summary>
/// Writes a parsed <see cref="FomModel"/> out as an ICD workbook. The per-section sheets live in
/// the Icd*Sheets partials, mirroring how the FOM itself is divided.
/// </summary>
public static partial class IcdExporter
{
    /// <summary>Width given to columns holding prose.</summary>
    internal const double ProseWidth = 80;

    /// <param name="selections">
    /// Classes to break out into the extraction sheets. The full parse result is always written
    /// regardless, so an empty selection simply omits those sheets.
    /// </param>
    public static void Export(FomModel model, string path, IReadOnlyList<IcdSelection>? selections = null)
    {
        var workbook = new XlsxWorkbook();
        var resolver = new FomTypeResolver(model);

        WriteSelectionSheets(workbook, model, resolver, selections ?? Array.Empty<IcdSelection>());

        WriteOverviewSheet(workbook, model);
        WriteObjectClassSheet(workbook, model);
        WriteAttributeSheet(workbook, model, resolver);
        WriteInteractionClassSheet(workbook, model);
        WriteParameterSheet(workbook, model, resolver);
        WriteDataTypeSheet(workbook, model, resolver);
        WriteRecordLayoutSheet(workbook, model, resolver);
        WriteEnumeratorSheet(workbook, model, resolver);

        workbook.Save(path);
    }

    static void WriteOverviewSheet(XlsxWorkbook workbook, FomModel model)
    {
        var sheet = workbook.AddSheet("概要");
        var id = model.Identification;

        sheet.AddHeader("項目", "内容");
        sheet.AddRow("名称", id.Name);
        sheet.AddRow("種別", id.Type);
        sheet.AddRow("バージョン", id.Version);
        sheet.AddRow("更新日", id.ModificationDate);
        sheet.AddRow("セキュリティ区分", id.SecurityClassification);
        sheet.AddRow("目的", id.Purpose);
        sheet.AddRow("適用領域", id.ApplicationDomain);
        sheet.AddRow("説明", id.Description);
        sheet.AddRow("オブジェクトクラス数", model.AllObjectClasses.Count);
        sheet.AddRow("公開可能クラス数", model.AllObjectClasses.Count(c => c.IsPublishable));
        sheet.AddRow("インタラクションクラス数", model.AllInteractionClasses.Count);
        sheet.AddRow("公開可能インタラクション数", model.AllInteractionClasses.Count(c => c.IsPublishable));
        sheet.AddRow("データ型数", model.DataTypes.Count);
        sheet.AddRow("列挙子数", model.DataTypes.Values.Sum(t => t.Enumerators.Count));
        sheet.AddRow("注記数", model.Notes.Count);
        sheet.AddRow("生成日時", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));

        sheet.SetColumnWidth(1, 26);
        sheet.SetColumnWidth(2, ProseWidth);
        sheet.WrapColumn(2);
    }

    /// <summary>
    /// Expands notes="RPRnoteBase2 RPRnoteBase18" into the note text. Default values and optionality
    /// are recorded only here, so they would otherwise be missing from the ICD entirely.
    /// </summary>
    internal static string ResolveNotes(FomModel model, IReadOnlyList<string> labels)
    {
        if (labels.Count == 0) return "";

        return string.Join("\n", labels.Select(label =>
            model.Notes.TryGetValue(label, out var note) ? note.Semantics : label));
    }

    internal static string KindText(FomDataTypeKind kind) => kind switch
    {
        FomDataTypeKind.Basic => "基本",
        FomDataTypeKind.Simple => "単純",
        FomDataTypeKind.Enumerated => "列挙",
        FomDataTypeKind.Array => "配列",
        FomDataTypeKind.FixedRecord => "固定レコード",
        FomDataTypeKind.VariantRecord => "可変レコード",
        _ => "未解決"
    };

    /// <summary>Bit width as a cell value: a number when fixed, otherwise the "可変" marker.</summary>
    internal static object? SizeCell(ResolvedType type) =>
        type.SizeInBits is int bits ? bits : type.SizeText;

    internal static void ApplyWidths(XlsxSheet sheet, params double[] widths)
    {
        for (int i = 0; i < widths.Length; i++) sheet.SetColumnWidth(i + 1, widths[i]);
    }
}
