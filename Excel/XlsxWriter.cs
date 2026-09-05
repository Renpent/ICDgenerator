using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace ICDgenerator.Excel;

/// <summary>
/// Minimal .xlsx writer built only on the framework. An .xlsx file is a ZIP of XML parts, so
/// System.IO.Compression plus hand-written XML is enough — the tool ships with no third-party
/// DLLs, which is what lets it be deployed into locked-down environments.
///
/// Deliberately covers only what the ICD needs: several sheets, inline strings and numbers,
/// a styled header row, a frozen header, an autofilter and fixed column widths.
/// </summary>
public sealed class XlsxWorkbook
{
    readonly List<XlsxSheet> _sheets = new();

    public XlsxSheet AddSheet(string name)
    {
        var sheet = new XlsxSheet(MakeUniqueSheetName(name));
        _sheets.Add(sheet);
        return sheet;
    }

    public void Save(string path)
    {
        using var file = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var zip = new ZipArchive(file, ZipArchiveMode.Create);

        Write(zip, "[Content_Types].xml", BuildContentTypes());
        Write(zip, "_rels/.rels", PackageRels);
        Write(zip, "xl/workbook.xml", BuildWorkbook());
        Write(zip, "xl/_rels/workbook.xml.rels", BuildWorkbookRels());
        Write(zip, "xl/styles.xml", StylesXml);

        for (int i = 0; i < _sheets.Count; i++)
        {
            Write(zip, $"xl/worksheets/sheet{i + 1}.xml", _sheets[i].BuildXml());
        }
    }

    static void Write(ZipArchive zip, string entryName, string content)
    {
        var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
        using var stream = entry.Open();
        // No BOM: that is what Excel itself writes, and some readers choke on one here.
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.Write(content);
    }

    string BuildContentTypes()
    {
        var sb = new StringBuilder(Declaration);
        sb.Append("<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">");
        sb.Append("<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>");
        sb.Append("<Default Extension=\"xml\" ContentType=\"application/xml\"/>");
        sb.Append("<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>");
        sb.Append("<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>");
        for (int i = 0; i < _sheets.Count; i++)
        {
            sb.Append($"<Override PartName=\"/xl/worksheets/sheet{i + 1}.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>");
        }
        return sb.Append("</Types>").ToString();
    }

    string BuildWorkbook()
    {
        var sb = new StringBuilder(Declaration);
        sb.Append("<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" ");
        sb.Append("xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets>");
        for (int i = 0; i < _sheets.Count; i++)
        {
            sb.Append($"<sheet name=\"");
            XlsxSheet.AppendEscaped(sb, _sheets[i].Name);
            sb.Append($"\" sheetId=\"{i + 1}\" r:id=\"rId{i + 1}\"/>");
        }
        return sb.Append("</sheets></workbook>").ToString();
    }

    string BuildWorkbookRels()
    {
        const string ns = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        var sb = new StringBuilder(Declaration);
        sb.Append("<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">");
        for (int i = 0; i < _sheets.Count; i++)
        {
            sb.Append($"<Relationship Id=\"rId{i + 1}\" Type=\"{ns}/worksheet\" Target=\"worksheets/sheet{i + 1}.xml\"/>");
        }
        sb.Append($"<Relationship Id=\"rId{_sheets.Count + 1}\" Type=\"{ns}/styles\" Target=\"styles.xml\"/>");
        return sb.Append("</Relationships>").ToString();
    }

    /// <summary>Excel rejects sheet names over 31 chars, containing []:*?/\ or duplicated.</summary>
    string MakeUniqueSheetName(string name)
    {
        var cleaned = new string(name.Select(c => "[]:*?/\\".Contains(c) ? '_' : c).ToArray());
        if (cleaned.Length > 31) cleaned = cleaned[..31];
        if (cleaned.Length == 0) cleaned = "Sheet";

        var candidate = cleaned;
        for (int n = 2; _sheets.Any(s => s.Name.Equals(candidate, StringComparison.OrdinalIgnoreCase)); n++)
        {
            var suffix = $"_{n}";
            candidate = cleaned.Length + suffix.Length > 31
                ? cleaned[..(31 - suffix.Length)] + suffix
                : cleaned + suffix;
        }
        return candidate;
    }

    internal const string Declaration = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>";

    const string PackageRels = Declaration +
        "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
        "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/>" +
        "</Relationships>";

    // cellXfs indexes here are what XlsxSheet.Style* refer to. Excel requires the first two
    // fills to be 'none' and 'gray125' regardless of whether they are used.
    const string StylesXml = Declaration +
        "<styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">" +
        "<fonts count=\"2\">" +
        "<font><sz val=\"11\"/><color theme=\"1\"/><name val=\"Calibri\"/><family val=\"2\"/><scheme val=\"minor\"/></font>" +
        "<font><b/><sz val=\"11\"/><color theme=\"1\"/><name val=\"Calibri\"/><family val=\"2\"/><scheme val=\"minor\"/></font>" +
        "</fonts>" +
        "<fills count=\"3\">" +
        "<fill><patternFill patternType=\"none\"/></fill>" +
        "<fill><patternFill patternType=\"gray125\"/></fill>" +
        "<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FFD9D9D9\"/><bgColor indexed=\"64\"/></patternFill></fill>" +
        "</fills>" +
        "<borders count=\"2\">" +
        "<border><left/><right/><top/><bottom/><diagonal/></border>" +
        "<border><left/><right/><top/><bottom style=\"thin\"><color indexed=\"64\"/></bottom><diagonal/></border>" +
        "</borders>" +
        "<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>" +
        "<cellXfs count=\"3\">" +
        "<xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/>" +
        "<xf numFmtId=\"0\" fontId=\"1\" fillId=\"2\" borderId=\"1\" xfId=\"0\" applyFont=\"1\" applyFill=\"1\" applyBorder=\"1\"/>" +
        "<xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyAlignment=\"1\"><alignment vertical=\"top\" wrapText=\"1\"/></xf>" +
        "</cellXfs>" +
        "<cellStyles count=\"1\"><cellStyle name=\"Normal\" xfId=\"0\" builtinId=\"0\"/></cellStyles>" +
        "</styleSheet>";
}
