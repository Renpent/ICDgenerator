using ICDgenerator.Fom;

namespace ICDgenerator.Excel;

public static partial class IcdExporter
{
    /// <summary>Presentation order: primitives first, then the composites built on them.</summary>
    static readonly FomDataTypeKind[] KindOrder =
    {
        FomDataTypeKind.Basic,
        FomDataTypeKind.Simple,
        FomDataTypeKind.Enumerated,
        FomDataTypeKind.Array,
        FomDataTypeKind.FixedRecord,
        FomDataTypeKind.VariantRecord
    };

    static IEnumerable<FomDataType> OrderedTypes(FomModel model) =>
        model.DataTypes.Values
            .OrderBy(t => Array.IndexOf(KindOrder, t.Kind))
            .ThenBy(t => t.Name, StringComparer.Ordinal);

    /// <summary>One row per declared type, with the resolution outcome alongside the declaration.</summary>
    static void WriteDataTypeSheet(XlsxWorkbook workbook, FomModel model, FomTypeResolver resolver)
    {
        var sheet = workbook.AddSheet("データ型定義");

        sheet.AddHeader("型名", "分類", "基本表現", "ビット幅", "単位", "エンコーディング",
                        "要素型/表現", "基数", "判別子", "構成要素数", "エンディアン",
                        "分解能", "精度", "Notes", "Semantics");

        foreach (var type in OrderedTypes(model))
        {
            var resolved = resolver.Resolve(type.Name);
            sheet.AddRow(
                type.Name,
                KindText(type.Kind),
                resolved.BaseRepresentation,
                SizeCell(resolved),
                type.Units,
                type.Encoding,
                // Whichever "what is this built from" field applies to the category.
                type.Kind == FomDataTypeKind.Array ? type.ElementDataType : type.Representation,
                type.Cardinality,
                type.Discriminant.Length > 0 ? $"{type.Discriminant} ({type.DiscriminantDataType})" : "",
                ComponentCount(type),
                type.Endian,
                type.Resolution,
                type.Accuracy,
                ResolveNotes(model, type.Notes),
                type.Semantics);
        }

        sheet.FreezeHeader = true;
        sheet.AutoFilter = true;
        sheet.WrapColumn(14);
        sheet.WrapColumn(15);
    }

    static object? ComponentCount(FomDataType type) => type.Kind switch
    {
        FomDataTypeKind.Enumerated => type.Enumerators.Count,
        FomDataTypeKind.FixedRecord => type.Fields.Count,
        FomDataTypeKind.VariantRecord => type.Alternatives.Count,
        _ => null
    };

    /// <summary>
    /// Expands the composite types one level: each fixed record field and each variant record
    /// alternative becomes a row, with its own resolved width. This is the byte-level layout an ICD
    /// reader needs in order to lay out a message.
    /// </summary>
    static void WriteRecordLayoutSheet(XlsxWorkbook workbook, FomModel model, FomTypeResolver resolver)
    {
        var sheet = workbook.AddSheet("レコード構成");

        sheet.AddHeader("レコード型", "分類", "順序", "選択子", "要素名", "データ型", "型分類",
                        "基本表現", "ビット幅", "単位", "ビットオフセット", "Notes", "Semantics");

        foreach (var type in OrderedTypes(model))
        {
            if (type.Kind == FomDataTypeKind.FixedRecord)
            {
                // Offsets accumulate only while every preceding field has a fixed width.
                int? offset = 0;
                for (int i = 0; i < type.Fields.Count; i++)
                {
                    var field = type.Fields[i];
                    var resolved = resolver.Resolve(field.DataType);
                    sheet.AddRow(
                        type.Name, KindText(type.Kind), i + 1, "",
                        field.Name, field.DataType, KindText(resolved.Kind),
                        resolved.BaseRepresentation, SizeCell(resolved), resolved.Units,
                        offset is int o ? o : "可変",
                        ResolveNotes(model, field.Notes), field.Semantics);

                    offset = offset is int cur && resolved.SizeInBits is int bits ? cur + bits : null;
                }
            }
            else if (type.Kind == FomDataTypeKind.VariantRecord)
            {
                for (int i = 0; i < type.Alternatives.Count; i++)
                {
                    var alternative = type.Alternatives[i];
                    var resolved = resolver.Resolve(alternative.DataType);
                    sheet.AddRow(
                        type.Name, KindText(type.Kind), i + 1, alternative.Enumerator,
                        alternative.Name, alternative.DataType, KindText(resolved.Kind),
                        resolved.BaseRepresentation, SizeCell(resolved), resolved.Units,
                        // The discriminant precedes the alternative, so there is no fixed offset.
                        "可変",
                        "", alternative.Semantics);
                }
            }
        }

        sheet.FreezeHeader = true;
        sheet.AutoFilter = true;
        sheet.WrapColumn(12);
        sheet.WrapColumn(13);
    }

    static void WriteEnumeratorSheet(XlsxWorkbook workbook, FomModel model, FomTypeResolver resolver)
    {
        var sheet = workbook.AddSheet("列挙値一覧");

        sheet.AddHeader("列挙型", "基本表現", "ビット幅", "列挙子名", "値", "Notes", "型のSemantics");

        foreach (var type in OrderedTypes(model).Where(t => t.Kind == FomDataTypeKind.Enumerated))
        {
            var resolved = resolver.Resolve(type.Name);
            foreach (var enumerator in type.Enumerators)
            {
                sheet.AddRow(
                    type.Name,
                    resolved.BaseRepresentation,
                    SizeCell(resolved),
                    enumerator.Name,
                    // Values are plain integers throughout this FOM, so emit them as numbers.
                    long.TryParse(enumerator.Value, out var value) ? value : enumerator.Value,
                    ResolveNotes(model, enumerator.Notes),
                    type.Semantics);
            }
        }

        sheet.FreezeHeader = true;
        sheet.AutoFilter = true;
        sheet.WrapColumn(6);
        sheet.WrapColumn(7);
    }
}
