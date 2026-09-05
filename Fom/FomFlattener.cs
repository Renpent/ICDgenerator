namespace ICDgenerator.Fom;

/// <summary>
/// One line of a flattened message layout. Composite nodes are emitted alongside the primitives
/// they contain, so that <see cref="Name"/> can stay a bare leaf name without losing the structure.
/// </summary>
/// <param name="Depth">0 for the attribute or parameter itself, deeper for nested members.</param>
/// <param name="SizeBytes">Size of a single element; null when the encoding is variable length.</param>
/// <param name="Amount">Element count — above 1 only for arrays, "可変" for dynamic ones.</param>
public sealed record FlatField(
    int Depth,
    string Name,
    string TypeName,
    string BaseType,
    int? SizeBytes,
    string Amount,
    string Units,
    string Selector,
    string Semantics)
{
    public bool IsComposite { get; init; }
}

/// <summary>
/// Expands an attribute or parameter into the individual values that go on the wire, following
/// records and arrays down to their primitives.
/// </summary>
public sealed class FomFlattener
{
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
        List<FlatField> rows, HashSet<string> visiting)
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
            || (kind == FomDataTypeKind.Array && ElementIsComposite(type));

        // Stop before recursing into a type that is already on the stack, or too deep to be useful.
        bool descend = composite && depth < MaxDepth && type is not null && visiting.Add(dataType);

        rows.Add(new FlatField(
            depth,
            name,
            dataType,
            resolved.BaseRepresentation,
            // A composite that we are about to break open would otherwise double-count its own size.
            descend ? null : sizeBytes,
            AmountOf(type, kind),
            resolved.Units,
            selector,
            semantics)
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
                foreach (var alternative in type!.Alternatives)
                {
                    // The selector column records which discriminant value picks this branch.
                    Expand(alternative.Name, alternative.DataType, alternative.Semantics,
                        alternative.Enumerator, depth + 1, rows, visiting);
                }
                break;

            case FomDataTypeKind.Array:
                // One level for the element type; the count lives on the array row's Amount.
                Expand(ElementName(type!), type!.ElementDataType, "", "", depth + 1, rows, visiting);
                break;
        }

        visiting.Remove(dataType);
    }

    bool ElementIsComposite(FomDataType? type)
    {
        if (type?.Kind != FomDataTypeKind.Array) return false;
        if (!_model.DataTypes.TryGetValue(type.ElementDataType, out var element)) return false;

        return element.Kind is FomDataTypeKind.FixedRecord or FomDataTypeKind.VariantRecord
            or FomDataTypeKind.Array;
    }

    /// <summary>Arrays report their element count; everything else is a single value.</summary>
    static string AmountOf(FomDataType? type, FomDataTypeKind kind)
    {
        if (kind != FomDataTypeKind.Array || type is null) return "1";

        return int.TryParse(type.Cardinality, out var count) ? count.ToString() : "可変";
    }

    static string ElementName(FomDataType arrayType) => arrayType.ElementDataType + " (要素)";

    /// <summary>Every basic type in this FOM is a whole number of bytes.</summary>
    static int? ToBytes(int? bits) => bits is int value ? value / 8 : null;
}
