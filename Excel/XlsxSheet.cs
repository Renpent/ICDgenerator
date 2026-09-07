using System.Globalization;
using System.Text;

namespace ICDgenerator.Excel;

/// <summary>One worksheet. Rows are buffered and serialised when the workbook is saved.</summary>
public sealed class XlsxSheet
{
    const int StyleDefault = 0;
    const int StyleHeader = 1;
    const int StyleWrap = 2;
    const int StyleLink = 3;

    readonly List<(object?[] Cells, bool IsHeader)> _rows = new();
    readonly Dictionary<int, double> _widths = new();
    readonly HashSet<int> _wrapColumns = new();

    /// <summary>Cell reference to the target sheet, for internal links. Keyed by "row,column".</summary>
    readonly Dictionary<(int Row, int Column), string> _links = new();

    internal XlsxSheet(string name) => Name = name;

    public string Name { get; }

    /// <summary>Keeps row 1 visible while scrolling.</summary>
    public bool FreezeHeader { get; set; }

    /// <summary>Puts filter dropdowns on row 1, spanning the used range.</summary>
    public bool AutoFilter { get; set; }

    /// <summary>
    /// Widest a fitted column may become. Prose runs to hundreds of characters and would otherwise
    /// produce a column no one can read across; such columns are given <see cref="WrapColumn"/>
    /// instead, and wrap at this width.
    /// </summary>
    public double MaxFittedWidth { get; set; } = 80;

    /// <summary>Narrowest a fitted column may become, so an empty column is still usable.</summary>
    public double MinFittedWidth { get; set; } = 6;

    public void AddHeader(params string[] cells) => _rows.Add((cells, true));

    /// <summary>Numeric values are written as numbers; anything else as an inline string.</summary>
    public void AddRow(params object?[] cells) => _rows.Add((cells, false));

    public void SetColumnWidth(int column, double width) => _widths[column] = width;

    /// <summary>Marks a column as prose: it wraps and is top-aligned.</summary>
    public void WrapColumn(int column) => _wrapColumns.Add(column);

    /// <summary>
    /// Turns a cell into a link to the top of another sheet in this workbook. Internal links carry
    /// their target in a location attribute, so unlike external ones they need no relationship part.
    /// </summary>
    /// <param name="row">1-based, counting the header row.</param>
    public void LinkToSheet(int row, int column, string targetSheetName) =>
        _links[(row, column)] = $"'{targetSheetName.Replace("'", "''")}'!A1";

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

        if (_rows.Count > 0)
        {
            sb.Append("<cols>");
            for (int column = 1; column <= columnCount; column++)
            {
                double width = _widths.TryGetValue(column, out var fixedWidth)
                    ? fixedWidth
                    : FitWidth(column);

                sb.Append(CultureInfo.InvariantCulture,
                    $"<col min=\"{column}\" max=\"{column}\" width=\"{width:0.##}\" customWidth=\"1\"/>");
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
                    : _links.ContainsKey((rowNumber, column)) ? StyleLink
                    : _wrapColumns.Contains(column) ? StyleWrap
                    : StyleDefault;
                AppendCell(sb, ColumnName(column) + rowNumber, cells[c], style);
            }
            sb.Append("</row>");
        }
        sb.Append("</sheetData>");

        // Schema order: autoFilter follows sheetData, and hyperlinks follow autoFilter.
        if (AutoFilter && _rows.Count > 0)
        {
            sb.Append($"<autoFilter ref=\"A1:{lastCell}\"/>");
        }

        if (_links.Count > 0)
        {
            sb.Append("<hyperlinks>");
            foreach (var ((row, column), location) in _links.OrderBy(l => l.Key.Row).ThenBy(l => l.Key.Column))
            {
                sb.Append($"<hyperlink ref=\"{ColumnName(column)}{row}\" location=\"");
                AppendEscaped(sb, location);
                sb.Append("\"/>");
            }
            sb.Append("</hyperlinks>");
        }

        return sb.Append("</worksheet>").ToString();
    }

    /// <summary>
    /// Width that fits the column's own content. There is no way to measure text without a font
    /// library, so this counts characters the way Excel's width unit is defined — the width of a
    /// digit in the default font — and treats a full-width character as two of them. Counting is
    /// cheap enough to do over every cell; it is measuring that was slow when this used a
    /// spreadsheet library.
    /// </summary>
    double FitWidth(int column)
    {
        double widest = 0;

        foreach (var (cells, isHeader) in _rows)
        {
            if (column > cells.Length) continue;

            double width = DisplayWidth(CellText(cells[column - 1]));

            // The filter dropdown sits on top of the header text, so the header needs room for it.
            if (isHeader && AutoFilter) width += 3;

            widest = Math.Max(widest, width);
        }

        return Math.Clamp(widest + 1, MinFittedWidth, MaxFittedWidth);
    }

    static string CellText(object? value) => value switch
    {
        null => "",
        bool b => b ? "TRUE" : "FALSE",
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? ""
    };

    /// <summary>
    /// Width in digit-widths, measured over the longest line. Semantics carry embedded newlines, and
    /// counting the whole string would size a wrapped column to the sum of its lines.
    /// </summary>
    static double DisplayWidth(string text)
    {
        double widest = 0, line = 0;

        foreach (char ch in text)
        {
            switch (ch)
            {
                case '\n': widest = Math.Max(widest, line); line = 0; break;
                case '\r': break;
                default: line += IsFullWidth(ch) ? 2 : 1; break;
            }
        }

        return Math.Max(widest, line);
    }

    /// <summary>
    /// Whether a character occupies two digit-widths. Covers the CJK and fullwidth blocks, which is
    /// what the Japanese headers and notes in this workbook need; anything else counts as one.
    /// </summary>
    static bool IsFullWidth(char ch) =>
        ch is >= 'ᄀ' and <= 'ᅟ'      // Hangul Jamo
           or >= '⺀' and <= '〾'      // CJK radicals, Kangxi, CJK punctuation
           or >= 'ぁ' and <= '㏿'      // Kana, Hangul Compatibility Jamo, CJK compat
           or >= '㐀' and <= '䶿'      // CJK Extension A
           or >= '一' and <= '鿿'      // CJK Unified Ideographs
           or >= 'ꀀ' and <= '꓏'      // Yi
           or >= '가' and <= '힣'      // Hangul syllables
           or >= '豈' and <= '﫿'      // CJK Compatibility Ideographs
           or >= '︰' and <= '﹯'      // CJK compatibility forms
           or >= '＀' and <= '｠'      // Fullwidth forms
           or >= '￠' and <= '￦';

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
