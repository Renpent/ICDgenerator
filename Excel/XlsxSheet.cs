using System.Globalization;
using System.Text;

namespace ICDgenerator.Excel;

/// <summary>One worksheet. Rows are buffered and serialised when the workbook is saved.</summary>
public sealed class XlsxSheet
{
    const int StyleDefault = 0;
    const int StyleHeader = 1;
    const int StyleWrap = 2;

    readonly List<(object?[] Cells, bool IsHeader)> _rows = new();
    readonly Dictionary<int, double> _widths = new();
    readonly HashSet<int> _wrapColumns = new();

    internal XlsxSheet(string name) => Name = name;

    public string Name { get; }

    /// <summary>Keeps row 1 visible while scrolling.</summary>
    public bool FreezeHeader { get; set; }

    /// <summary>Puts filter dropdowns on row 1, spanning the used range.</summary>
    public bool AutoFilter { get; set; }

    public void AddHeader(params string[] cells) => _rows.Add((cells, true));

    /// <summary>Numeric values are written as numbers; anything else as an inline string.</summary>
    public void AddRow(params object?[] cells) => _rows.Add((cells, false));

    public void SetColumnWidth(int column, double width) => _widths[column] = width;

    /// <summary>Marks a column as prose: it wraps and is top-aligned.</summary>
    public void WrapColumn(int column) => _wrapColumns.Add(column);

    internal string BuildXml()
    {
        int columnCount = _rows.Count == 0 ? 1 : _rows.Max(r => r.Cells.Length);
        int rowCount = Math.Max(_rows.Count, 1);
        string lastCell = ColumnName(columnCount) + rowCount;

        var sb = new StringBuilder(XlsxWorkbook.Declaration);
        sb.Append("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">");
        sb.Append($"<dimension ref=\"A1:{lastCell}\"/>");

        sb.Append("<sheetViews><sheetView workbookViewId=\"0\">");
        if (FreezeHeader)
        {
            sb.Append("<pane ySplit=\"1\" topLeftCell=\"A2\" activePane=\"bottomLeft\" state=\"frozen\"/>");
        }
        sb.Append("</sheetView></sheetViews>");
        sb.Append("<sheetFormatPr defaultRowHeight=\"15\"/>");

        if (_widths.Count > 0)
        {
            sb.Append("<cols>");
            foreach (var (column, width) in _widths.OrderBy(w => w.Key))
            {
                sb.Append(CultureInfo.InvariantCulture,
                    $"<col min=\"{column}\" max=\"{column}\" width=\"{width}\" customWidth=\"1\"/>");
            }
            sb.Append("</cols>");
        }

        sb.Append("<sheetData>");
        for (int i = 0; i < _rows.Count; i++)
        {
            var (cells, isHeader) = _rows[i];
            int rowNumber = i + 1;
            sb.Append($"<row r=\"{rowNumber}\">");
            for (int c = 0; c < cells.Length; c++)
            {
                int column = c + 1;
                int style = isHeader ? StyleHeader
                    : _wrapColumns.Contains(column) ? StyleWrap
                    : StyleDefault;
                AppendCell(sb, ColumnName(column) + rowNumber, cells[c], style);
            }
            sb.Append("</row>");
        }
        sb.Append("</sheetData>");

        // Schema order: autoFilter must follow sheetData.
        if (AutoFilter && _rows.Count > 0)
        {
            sb.Append($"<autoFilter ref=\"A1:{lastCell}\"/>");
        }

        return sb.Append("</worksheet>").ToString();
    }

    static void AppendCell(StringBuilder sb, string reference, object? value, int style)
    {
        switch (value)
        {
            case null:
                sb.Append($"<c r=\"{reference}\" s=\"{style}\"/>");
                return;

            case int or long or short or byte or double or float or decimal:
                sb.Append($"<c r=\"{reference}\" s=\"{style}\"><v>")
                  .Append(Convert.ToString(value, CultureInfo.InvariantCulture))
                  .Append("</v></c>");
                return;

            case bool b:
                sb.Append($"<c r=\"{reference}\" s=\"{style}\" t=\"b\"><v>{(b ? 1 : 0)}</v></c>");
                return;

            default:
                var text = value.ToString() ?? "";
                if (text.Length == 0)
                {
                    sb.Append($"<c r=\"{reference}\" s=\"{style}\"/>");
                    return;
                }
                // Inline strings avoid needing a sharedStrings part; xml:space keeps the
                // leading spaces used to indent class names by hierarchy depth.
                sb.Append($"<c r=\"{reference}\" s=\"{style}\" t=\"inlineStr\"><is><t xml:space=\"preserve\">");
                AppendEscaped(sb, text);
                sb.Append("</t></is></c>");
                return;
        }
    }

    internal static void AppendEscaped(StringBuilder sb, string text)
    {
        foreach (char ch in text)
        {
            switch (ch)
            {
                case '&': sb.Append("&amp;"); break;
                case '<': sb.Append("&lt;"); break;
                case '>': sb.Append("&gt;"); break;
                case '"': sb.Append("&quot;"); break;
                case '\t' or '\n' or '\r': sb.Append(ch); break;
                default:
                    // Other control characters are not legal in XML 1.0 and would corrupt the part.
                    if (ch >= 0x20) sb.Append(ch);
                    break;
            }
        }
    }

    /// <summary>1 => A, 27 => AA.</summary>
    internal static string ColumnName(int column)
    {
        Span<char> buffer = stackalloc char[3];
        int i = buffer.Length;
        while (column > 0)
        {
            column--;
            buffer[--i] = (char)('A' + column % 26);
            column /= 26;
        }
        return new string(buffer[i..]);
    }
}
