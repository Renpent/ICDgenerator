using ClosedXML.Excel;
using ICDgenerator.Fom;

namespace ICDgenerator.Excel;

/// <summary>Writes a parsed <see cref="FomModel"/> out as an ICD workbook.</summary>
public static class IcdExporter
{
    /// <summary>Semantics columns hold whole paragraphs; cap them so AdjustToContents stays sane.</summary>
    const double MaxColumnWidth = 80;

    public static void Export(FomModel model, string path)
    {
        using var wb = new XLWorkbook();

        WriteOverviewSheet(wb, model);
        WriteObjectClassSheet(wb, model);

        wb.SaveAs(path);
    }

    static void WriteOverviewSheet(XLWorkbook wb, FomModel model)
    {
        var ws = wb.Worksheets.Add("概要");
        var id = model.Identification;

        var rows = new (string Label, string Value)[]
        {
            ("名称", id.Name),
            ("種別", id.Type),
            ("バージョン", id.Version),
            ("更新日", id.ModificationDate),
            ("セキュリティ区分", id.SecurityClassification),
            ("目的", id.Purpose),
            ("適用領域", id.ApplicationDomain),
            ("説明", id.Description),
            ("オブジェクトクラス数", model.AllObjectClasses.Count.ToString()),
            ("リーフクラス数", model.AllObjectClasses.Count(c => c.IsLeaf).ToString()),
            ("生成日時", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"))
        };

        for (int i = 0; i < rows.Length; i++)
        {
            int r = i + 1;
            ws.Cell(r, 1).Value = rows[i].Label;
            ws.Cell(r, 1).Style.Font.Bold = true;
            ws.Cell(r, 2).Value = rows[i].Value;
            ws.Cell(r, 2).Style.Alignment.WrapText = true;
        }

        ws.Column(1).AdjustToContents();
        ws.Column(2).Width = MaxColumnWidth;
    }

    static void WriteObjectClassSheet(XLWorkbook wb, FomModel model)
    {
        var ws = wb.Worksheets.Add("オブジェクトクラス一覧");

        string[] headers =
        {
            "階層", "クラス名", "完全修飾名", "親クラス", "Sharing",
            "自クラス属性数", "継承後属性数", "リーフ", "Notes", "Semantics"
        };

        for (int c = 0; c < headers.Length; c++)
        {
            ws.Cell(1, c + 1).Value = headers[c];
        }

        int row = 2;
        foreach (var cls in model.AllObjectClasses)
        {
            ws.Cell(row, 1).Value = cls.Level;
            // Indent by depth so the inheritance tree stays readable in a flat table.
            ws.Cell(row, 2).Value = new string(' ', (cls.Level - 1) * 2) + cls.Name;
            ws.Cell(row, 3).Value = cls.FullName;
            ws.Cell(row, 4).Value = cls.Parent?.FullName ?? "";
            ws.Cell(row, 5).Value = cls.Sharing;
            ws.Cell(row, 6).Value = cls.OwnAttributes.Count;
            ws.Cell(row, 7).Value = cls.AllAttributes.Count;
            ws.Cell(row, 8).Value = cls.IsLeaf ? "○" : "";
            ws.Cell(row, 9).Value = string.Join(" ", cls.Notes);
            ws.Cell(row, 10).Value = cls.Semantics;
            row++;
        }

        FormatTable(ws, headerColumns: headers.Length, lastRow: row - 1, wideColumns: 10);
    }

    /// <summary>
    /// Applies the shared header/filter/width treatment. Columns listed in
    /// <paramref name="wideColumns"/> hold prose and get a fixed width instead: measuring them
    /// with AdjustToContents dominates export time and the result is clamped anyway.
    /// </summary>
    static void FormatTable(IXLWorksheet ws, int headerColumns, int lastRow, params int[] wideColumns)
    {
        var header = ws.Range(1, 1, 1, headerColumns);
        header.Style.Font.Bold = true;
        header.Style.Fill.BackgroundColor = XLColor.LightGray;
        header.Style.Border.BottomBorder = XLBorderStyleValues.Thin;

        ws.SheetView.FreezeRows(1);

        if (lastRow >= 1)
        {
            ws.Range(1, 1, lastRow, headerColumns).SetAutoFilter();
        }

        for (int c = 1; c <= headerColumns; c++)
        {
            if (wideColumns.Contains(c))
            {
                ws.Column(c).Width = MaxColumnWidth;
                ws.Column(c).Style.Alignment.WrapText = true;
                continue;
            }

            ws.Column(c).AdjustToContents();
            if (ws.Column(c).Width > MaxColumnWidth) ws.Column(c).Width = MaxColumnWidth;
        }
    }
}
