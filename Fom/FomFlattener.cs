namespace ICDgenerator.Fom;

/// <summary>
/// One line of a flattened message layout. Composite nodes are emitted alongside the primitives
/// they contain, so that <see cref="Name"/> can stay a bare leaf name without losing the structure.
/// </summary>
/// <param name="Depth">0 for the attribute or parameter itself, deeper for nested members.</param>
/// <param name="SizeBytes">Size of a single element; null when the encoding is variable length.</param>
/// <param name="Amount">
/// Element count. A number for fixed arrays, the name of the count row for dynamic ones, and 1
/// for everything else — never a bare "変長", so that a reader always has something to parse by.
/// </param>
/// <param name="LengthRule">How a variable length is determined; empty when the size is fixed.</param>
public sealed record FlatField(
    int Depth,
    string Name,
    string TypeName,
    string BaseType,
    int? SizeBytes,
    string Amount,
    string Units,
    string Selector,
    string Semantics,
    string LengthRule = "")
{
    public bool IsComposite { get; init; }
}

/// <summary>
/// Expands an attribute or parameter into the individual values that go on the wire, following
/// records and arrays down to their primitives.
/// </summary>
public sealed class FomFlattener
{
    /// <summary>Width of the element count that precedes a variable array, per HLAvariableArray.</summary>
    const int CountBytes = 4;

    const string CountType = "HLAinteger32BE";

    /// <summary>
    /// Arrays contribute a count rather than one row per element; expanding MarkingArray31 into 31
    /// rows of Octet would bury the layout instead of describing it.
    /// </summary>
    const int MaxDepth = 12;

    readonly FomModel _model;
    readonly FomTypeResolver _resolver;

    public FomFlattener(FomModel model, FomTypeResolver resolver)
    {
        _model = model;
        _resolver = resolver;
    }

    public List<FlatField> Flatten(string name, string dataType, string semantics)
    {
        var rows = new List<FlatField>();
        Expand(name, dataType, semantics, selector: "", depth: 0, rows,
            new HashSet<string>(StringComparer.Ordinal));
        return rows;
    }

    void Expand(string name, string dataType, string semantics, string selector, int depth,
        List<FlatField> rows, HashSet<string> visiting, string LengthRule = "")
    {
        var resolved = _resolver.Resolve(dataType);
        _model.DataTypes.TryGetValue(dataType, out var type);
        var kind = type?.Kind ?? resolved.Kind;

        // For an array the size column describes a single element, so that Size x Amount gives the
        // total; taking the resolved width here would count the whole array and then multiply again.
        int? sizeBytes = kind == FomDataTypeKind.Array && type is not null
            ? ToBytes(_resolver.Resolve(type.ElementDataType).SizeInBits)
            : ToBytes(resolved.SizeInBits);

        bool composite = kind is FomDataTypeKind.FixedRecord or FomDataTypeKind.VariantRecord
            or FomDataTypeKind.Array;

        // Stop before recursing into a type that is already on the stack, or too deep to be useful.
        bool descend = composite && depth < MaxDepth && type is not null && visiting.Add(dataType);

        rows.Add(new FlatField(
            depth,
            name,
            dataType,
            resolved.BaseRepresentation,
            // A record that we are about to break open leaves its size to the fields beneath it.
            // An array keeps its element size, because Size x Amount is what gives its total and
            // the rows beneath describe a single element rather than adding to it.
            descend && kind != FomDataTypeKind.Array ? null : sizeBytes,
            AmountOf(type, kind, name),
            resolved.Units,
            selector,
            semantics,
            LengthRule.Length > 0 ? LengthRule : LengthRuleOf(type, kind))
        {
            IsComposite = composite
        });

        if (!descend) return;

        switch (kind)
        {
            case FomDataTypeKind.FixedRecord:
                foreach (var field in type!.Fields)
                {
                    Expand(field.Name, field.DataType, field.Semantics, "", depth + 1, rows, visiting);
                }
                break;

            case FomDataTypeKind.VariantRecord:
                // The discriminant is a real field: it precedes the selected alternative on the wire
                // and is what tells the receiver which alternative follows.
                Expand(type!.Discriminant, type.DiscriminantDataType, $"{name} の判別子。",
                    "", depth + 1, rows, visiting, LengthRule: "判別子");

                foreach (var alternative in type.Alternatives)
                {
                    // The selector column records which discriminant value picks this branch.
                    Expand(alternative.Name, alternative.DataType, alternative.Semantics,
                        alternative.Enumerator, depth + 1, rows, visiting);
                }
                break;

            case FomDataTypeKind.Array:
                // Lay the array out as it appears on the wire: the element count first where one is
                // present, then the element itself. A dynamic array with no count in the HLA
                // encoding still gets a row, because the UDP side needs one to parse unambiguously —
                // the LengthRule column marks it as added by the gateway rather than carried by HLA.
                if (!IsFixedCardinality(type!))
                {
                    rows.Add(new FlatField(depth + 1, CountFieldName(name), CountType, CountType,
                        CountBytes, "1", "", "",
                        $"{name} の要素数。", CountRuleOf(type!)));
                }

                Expand(ElementName(type!), type!.ElementDataType, "", "", depth + 1, rows, visiting);
                break;
        }

        visiting.Remove(dataType);
    }

    static bool IsFixedCardinality(FomDataType type) => int.TryParse(type.Cardinality, out _);

    static string CountFieldName(string arrayName) => arrayName + "_Count";

    /// <summary>
    /// An array's element count is a number when the cardinality is fixed, and otherwise the name of
    /// the count row emitted just above it — so a reader always has a concrete thing to read.
    /// </summary>
    static string AmountOf(FomDataType? type, FomDataTypeKind kind, string name)
    {
        if (kind != FomDataTypeKind.Array || type is null) return "1";

        return int.TryParse(type.Cardinality, out var count)
            ? count.ToString()
            : CountFieldName(name);
    }

    /// <summary>How the length of a variable array is established, per its HLA encoding.</summary>
    static string LengthRuleOf(FomDataType? type, FomDataTypeKind kind)
    {
        if (kind != FomDataTypeKind.Array || type is null) return "";

        return type.Encoding switch
        {
            "HLAfixedArray" => "固定",
            "RPRpaddingTo32Array" or "RPRpaddingTo64Array" => "パディング",
            // Every non-fixed array is preceded by a count row in the UDP layout; where that count's
            // value comes from is recorded on the count row itself.
            _ => "個数前置",
        };
    }

    /// <summary>
    /// Where the gateway gets this count. The field is always present in the UDP layout; what differs
    /// is whether HLA hands the value over or the gateway has to work it out.
    /// </summary>
    static string CountRuleOf(FomDataType type) => type.Encoding switch
    {
        "HLAvariableArray" => "HLA側も前置",
        "RPRnullTerminatedArray" => "HLA側は終端子",
        _ => "HLA側になし(GW算出)"
    };

    static string ElementName(FomDataType arrayType) => arrayType.ElementDataType + " (要素)";

    /// <summary>Every basic type in this FOM is a whole number of bytes.</summary>
    static int? ToBytes(int? bits) => bits is int value ? value / 8 : null;
}
