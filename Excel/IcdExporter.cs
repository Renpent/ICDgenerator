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

        WriteOverviewSheet(workbook, model);
        WriteObjectClassSheet(workbook, model);

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
}
