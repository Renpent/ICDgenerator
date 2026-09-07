using ICDgenerator.Fom;

namespace ICDgenerator.Excel;

public static partial class IcdExporter
{
    static void WriteInteractionClassSheet(XlsxWorkbook workbook, FomModel model)
    {
        var sheet = workbook.AddSheet("インタラクション一覧");

        sheet.AddHeader("階層", "クラス名", "完全修飾名", "親クラス", "Sharing", "Transportation",
                        "Order", "自クラスパラメータ数", "継承後パラメータ数", "リーフ", "公開可能", "Notes", "Semantics");

        foreach (var cls in model.AllInteractionClasses)
        {
            sheet.AddRow(
                cls.Level,
                new string(' ', (cls.Level - 1) * 2) + cls.Name,
                cls.FullName,
                cls.Parent?.FullName ?? "",
                cls.Sharing,
                cls.Transportation,
                cls.Order,
                cls.OwnParameters.Count,
                cls.AllParameters.Count,
                cls.IsLeaf ? "○" : "",
                cls.IsPublishable ? "○" : "",
                ResolveNotes(model, cls.Notes),
                cls.Semantics);
        }

        sheet.FreezeHeader = true;
        sheet.AutoFilter = true;
        sheet.WrapColumn(12);
        sheet.WrapColumn(13);
    }

    /// <summary>
    /// One row per (publishable interaction, parameter) pair, inheritance expanded.
    ///
    /// Publishability, not leafness, decides what is listed: 19 interactions such as Collision and
    /// ActionRequest are sent in their own right yet also have subclasses. Federations commonly send
    /// only the deepest classes, so the リーフ column lets a reader filter down to that view rather
    /// than having the narrower choice baked in — leaves are a strict subset of the publishable set.
    /// </summary>
    static void WriteParameterSheet(XlsxWorkbook workbook, FomModel model, FomTypeResolver resolver)
    {
        var sheet = workbook.AddSheet("パラメータICD");

        sheet.AddHeader("インタラクション", "リーフ", "パラメータ名", "宣言元クラス", "データ型", "型分類",
                        "基本表現", "ビット幅", "単位", "Transportation", "Order", "Notes", "Semantics");

        foreach (var cls in model.AllInteractionClasses.Where(c => c.IsPublishable))
        {
            foreach (var parameter in cls.AllParameters)
            {
                var type = resolver.Resolve(parameter.DataType);
                sheet.AddRow(
                    cls.FullName,
                    cls.IsLeaf ? "○" : "",
                    parameter.Name,
                    parameter.DeclaringClass,
                    parameter.DataType,
                    KindText(type.Kind),
                    type.BaseRepresentation,
                    SizeCell(type),
                    type.Units,
                    // Transportation and order are properties of the interaction, not the parameter.
                    cls.Transportation,
                    cls.Order,
                    ResolveNotes(model, parameter.Notes),
                    parameter.Semantics);
            }
        }

        sheet.FreezeHeader = true;
        sheet.AutoFilter = true;
        sheet.WrapColumn(12);
        sheet.WrapColumn(13);
    }
}
