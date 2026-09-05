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
    /// <summary>
    /// Writes the extraction view: an index listing each selected class, and one detail sheet per
    /// class enumerating everything it puts on the wire. The index links to the detail sheets.
    ///
    /// ID / Port / Rate have no counterpart in the FOM — HLA carries no port and this FOM declares
    /// no update rates — so they are emitted blank for the reader to fill in.
    /// </summary>
    static void WriteSelectionSheets(XlsxWorkbook workbook, FomModel model, FomTypeResolver resolver,
        IReadOnlyList<IcdSelection> selections)
    {
        if (selections.Count == 0) return;

        var flattener = new FomFlattener(model, resolver);
        var index = workbook.AddSheet("抽出概要");

        index.AddHeader("Name", "ID", "Port", "Rate", "Size(Bytes)", "Descripter");

        // Sheets must exist before the index can link to them, and the index must come first in the
        // workbook, so add the index sheet up front and fill in its rows as the details are built.
        var rows = new List<(string Display, string SheetName, object? Size, string Description)>();

        foreach (var selection in selections)
        {
            var detail = workbook.AddSheet(selection.ShortName);
            var (size, description) = WriteDetailSheet(detail, model, resolver, flattener, selection);
            rows.Add((selection.ShortName, detail.Name, size, description));
        }

        for (int i = 0; i < rows.Count; i++)
        {
            var (display, sheetName, size, description) = rows[i];
            index.AddRow(display, null, null, null, size, description);
            index.LinkToSheet(i + 2, 1, sheetName);
        }

        index.FreezeHeader = true;
        index.AutoFilter = true;
        ApplyWidths(index, 34, 12, 12, 12, 14, ProseWidth);
        index.WrapColumn(6);
    }

    /// <returns>The class's total size in bytes (or "可変") and its semantics, for the index row.</returns>
    static (object? Size, string Description) WriteDetailSheet(XlsxSheet sheet, FomModel model,
        FomTypeResolver resolver, FomFlattener flattener, IcdSelection selection)
    {
        sheet.AddHeader("Name", "Type", "Size(Bytes)", "Amount", "Units", "選択子", "Description");

        var members = selection.IsInteraction
            ? model.AllInteractionClasses.First(c => c.FullName == selection.FullName)
                .AllParameters.Select(p => (p.Name, p.DataType, p.Semantics))
            : model.AllObjectClasses.First(c => c.FullName == selection.FullName)
                .AllAttributes.Select(a => (a.Name, a.DataType, a.Semantics));

        int totalBytes = 0;
        bool fixedSize = true;

        foreach (var (name, dataType, semantics) in members)
        {
            foreach (var field in flattener.Flatten(name, dataType, semantics))
            {
                sheet.AddRow(
                    // Depth is shown by indentation so the bare leaf name keeps its context.
                    new string(' ', field.Depth * 2) + field.Name,
                    field.TypeName,
                    field.SizeBytes is int bytes ? bytes : field.IsComposite ? "" : "可変",
                    field.Amount,
                    // The FOM writes "NA" where a type has no units; that placeholder is noise in a
                    // working layout sheet, though データ型定義 still reproduces it verbatim.
                    field.Units == "NA" ? "" : field.Units,
                    field.Selector,
                    field.Semantics);
            }

            // The class total comes from the member's own resolved width, not from the expanded
            // rows: composites deliberately leave their size blank once they are broken open.
            if (resolver.Resolve(dataType).SizeInBits is int bits) totalBytes += bits / 8;
            else fixedSize = false;
        }

        var description = selection.IsInteraction
            ? model.AllInteractionClasses.First(c => c.FullName == selection.FullName).Semantics
            : model.AllObjectClasses.First(c => c.FullName == selection.FullName).Semantics;

        sheet.FreezeHeader = true;
        sheet.AutoFilter = true;
        ApplyWidths(sheet, 44, 40, 13, 10, 26, 26, ProseWidth);
        sheet.WrapColumn(7);

        return (fixedSize ? totalBytes : "可変", description);
    }
}
