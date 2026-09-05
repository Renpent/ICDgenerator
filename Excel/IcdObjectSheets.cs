using ICDgenerator.Fom;

namespace ICDgenerator.Excel;

public static partial class IcdExporter
{
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
                ResolveNotes(model, cls.Notes),
                cls.Semantics);
        }

        sheet.FreezeHeader = true;
        sheet.AutoFilter = true;
        ApplyWidths(sheet, 6, 30, 46, 44, 17, 14, 14, 7, 40, ProseWidth);
        sheet.WrapColumn(9);
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
                    SizeCell(type),
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
        ApplyWidths(sheet, 46, 30, 40, 34, 14, 22, 9, 26, 13, 17, 15, 11, 17, 50, ProseWidth);
        sheet.WrapColumn(14);
        sheet.WrapColumn(15);
    }
}
