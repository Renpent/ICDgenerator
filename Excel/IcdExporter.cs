using ICDgenerator.Fom;

namespace ICDgenerator.Excel;

/// <summary>Writes a parsed <see cref="FomModel"/> out as an ICD workbook.</summary>
public static class IcdExporter
{
    /// <summary>Width given to columns holding prose.</summary>
    const double ProseWidth = 80;

    public static void Export(FomModel model, string path)
    {
        var workbook = new XlsxWorkbook();
        var resolver = new FomTypeResolver(model);

        WriteOverviewSheet(workbook, model);
        WriteObjectClassSheet(workbook, model);
        WriteAttributeSheet(workbook, model, resolver);

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
        sheet.AddRow("リーフクラス数", model.AllObjectClasses.Count(c => c.IsLeaf));
        sheet.AddRow("データ型数", model.DataTypes.Count);
        sheet.AddRow("注記数", model.Notes.Count);
        sheet.AddRow("生成日時", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));

        sheet.SetColumnWidth(1, 22);
        sheet.SetColumnWidth(2, ProseWidth);
        sheet.WrapColumn(2);
    }

    static void WriteObjectClassSheet(XlsxWorkbook workbook, FomModel model)
    {
        var sheet = workbook.AddSheet("オブジェクトクラス一覧");

        sheet.AddHeader("階層", "クラス名", "完全修飾名", "親クラス", "Sharing",
                        "自クラス属性数", "継承後属性数", "リーフ", "Notes", "Semantics");

        foreach (var cls in model.AllObjectClasses)
        {
            sheet.AddRow(
                cls.Level,
                // Indent by depth so the inheritance tree stays readable in a flat table.
                new string(' ', (cls.Level - 1) * 2) + cls.Name,
                cls.FullName,
                cls.Parent?.FullName ?? "",
                cls.Sharing,
                cls.OwnAttributes.Count,
                cls.AllAttributes.Count,
                cls.IsLeaf ? "○" : "",
                string.Join(" ", cls.Notes),
                cls.Semantics);
        }

        sheet.FreezeHeader = true;
        sheet.AutoFilter = true;

        double[] widths = { 6, 30, 46, 44, 17, 14, 14, 7, 26, ProseWidth };
        for (int i = 0; i < widths.Length; i++) sheet.SetColumnWidth(i + 1, widths[i]);
        sheet.WrapColumn(10);
    }

    /// <summary>
    /// One row per (leaf class, attribute) pair with inheritance already expanded — the form an ICD
    /// reader needs, since a class like Aircraft declares nothing of its own yet carries 46 attributes.
    /// </summary>
    static void WriteAttributeSheet(XlsxWorkbook workbook, FomModel model, FomTypeResolver resolver)
    {
        var sheet = workbook.AddSheet("属性ICD");

        sheet.AddHeader("クラス", "属性名", "宣言元クラス", "データ型", "型分類", "基本表現",
                        "ビット幅", "単位", "UpdateType", "UpdateCondition", "Transportation",
                        "Order", "Sharing", "Notes", "Semantics");

        foreach (var cls in model.AllObjectClasses.Where(c => c.IsLeaf))
        {
            foreach (var attribute in cls.AllAttributes)
            {
                var type = resolver.Resolve(attribute.DataType);
                sheet.AddRow(
                    cls.FullName,
                    attribute.Name,
                    attribute.DeclaringClass,
                    attribute.DataType,
                    KindText(type.Kind),
                    type.BaseRepresentation,
                    type.SizeInBits is int bits ? bits : type.SizeText,
                    type.Units,
                    attribute.UpdateType,
                    attribute.UpdateCondition,
                    attribute.Transportation,
                    attribute.Order,
                    attribute.Sharing,
                    ResolveNotes(model, attribute.Notes),
                    attribute.Semantics);
            }
        }

        sheet.FreezeHeader = true;
        sheet.AutoFilter = true;

        double[] widths = { 46, 30, 40, 34, 14, 22, 9, 26, 13, 17, 15, 11, 17, 50, ProseWidth };
        for (int i = 0; i < widths.Length; i++) sheet.SetColumnWidth(i + 1, widths[i]);
        sheet.WrapColumn(14);
        sheet.WrapColumn(15);
    }

    /// <summary>
    /// Expands notes="RPRnoteBase2 RPRnoteBase18" into the note text. Default values and optionality
    /// are recorded only here, so they would otherwise be missing from the ICD entirely.
    /// </summary>
    static string ResolveNotes(FomModel model, IReadOnlyList<string> labels)
    {
        if (labels.Count == 0) return "";

        return string.Join("\n", labels.Select(label =>
            model.Notes.TryGetValue(label, out var note) ? note.Semantics : label));
    }

    static string KindText(FomDataTypeKind kind) => kind switch
    {
        FomDataTypeKind.Basic => "基本",
        FomDataTypeKind.Simple => "単純",
        FomDataTypeKind.Enumerated => "列挙",
        FomDataTypeKind.Array => "配列",
        FomDataTypeKind.FixedRecord => "固定レコード",
        FomDataTypeKind.VariantRecord => "可変レコード",
        _ => "未解決"
    };
}
