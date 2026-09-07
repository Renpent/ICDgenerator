using ICDgenerator.Fom;

namespace ICDgenerator.Excel;

public static partial class IcdExporter
{
    static void WriteObjectClassSheet(XlsxWorkbook workbook, FomModel model)
    {
        var sheet = workbook.AddSheet("オブジェクトクラス一覧");

        sheet.AddHeader("階層", "クラス名", "完全修飾名", "親クラス", "Sharing",
                        "自クラス属性数", "継承後属性数", "リーフ", "公開可能", "Notes", "Semantics");

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
                cls.IsPublishable ? "○" : "",
                ResolveNotes(model, cls.Notes),
                cls.Semantics);
        }

        sheet.FreezeHeader = true;
        sheet.AutoFilter = true;
        sheet.WrapColumn(10);
        sheet.WrapColumn(11);
    }

    /// <summary>
    /// One row per (publishable class, attribute) pair with inheritance already expanded — the form an
    /// ICD reader needs, since a class like Aircraft declares nothing of its own yet carries 46
    /// attributes. Intermediate classes are omitted: they exist only to be inherited from.
    /// </summary>
    static void WriteAttributeSheet(XlsxWorkbook workbook, FomModel model, FomTypeResolver resolver)
    {
        var sheet = workbook.AddSheet("属性ICD");

        sheet.AddHeader("クラス", "属性名", "宣言元クラス", "データ型", "型分類", "基本表現",
                        "ビット幅", "単位", "UpdateType", "UpdateCondition", "Transportation",
                        "Order", "Sharing", "Notes", "Semantics");

        foreach (var cls in model.AllObjectClasses.Where(c => c.IsPublishable))
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
        sheet.WrapColumn(14);
        sheet.WrapColumn(15);
    }
}
