namespace ICDgenerator.Fom;

/// <summary>
/// One value on the wire. Every row is real transferred data — container types are walked through
/// rather than listed, and <see cref="Path"/> carries the structure the containers used to show.
/// </summary>
/// <param name="Path">Dotted path from the attribute or parameter, with [] marking array elements.</param>
/// <param name="SizeBytes">Size of a single occurrence; null when the encoding is variable length.</param>
/// <param name="Amount">
/// How many times this value occurs: 1, a literal count, or an expression naming the count rows that
/// decide it — never a bare "variable", so a reader always has something concrete to parse by.
/// </param>
/// <param name="Selector">
/// Discriminant values that must hold for this row to appear at all. Empty means unconditional.
/// </param>
/// <param name="LengthRule">How a variable length is determined; empty where it does not apply.</param>
public sealed record FlatField(
    string Path,
    string TypeName,
    string BaseType,
    int? SizeBytes,
    string Amount,
    string Units,
    string Selector,
    string Semantics,
    string LengthRule = "");

/// <summary>
/// Expands an attribute or parameter into the individual values that go on the wire, following
/// records, variants and arrays down to their primitives.
/// </summary>
public sealed class FomFlattener
{
    /// <summary>Width of the element count that precedes a variable array, per HLAvariableArray.</summary>
    const int CountBytes = 4;

    const string CountType = "HLAinteger32BE";

    /// <summary>Deep enough for any real FOM; a backstop against pathological nesting.</summary>
    const int MaxDepth = 12;

    /// <summary>
    /// How a variable array's length is established.
    ///
    /// IEEE 1516-2010 defines only HLAfixedArray and HLAvariableArray; every other encoding is an
    /// extension the FOM itself introduces, and the standard does not say how it delimits its
    /// elements. Known extensions are listed so that a FOM using them reads well, but an unrecognised
    /// one is reported as unrecognised rather than guessed at — asserting a delimiting rule that the
    /// FOM never stated would put a wrong layout into the ICD.
    /// </summary>
    static readonly Dictionary<string, (string ArrayRule, string CountRule)> KnownEncodings =
        new(StringComparer.Ordinal)
        {
            // Defined by IEEE 1516-2010.
            ["HLAfixedArray"] = ("固定", ""),
            ["HLAvariableArray"] = ("個数前置", "HLA側も前置"),

            // Extensions seen in the RPR FOM. Add entries here as other FOMs bring their own.
            ["RPRlengthlessArray"] = ("個数前置", "HLA側になし(GW算出)"),
            ["RPRnullTerminatedArray"] = ("個数前置", "HLA側は終端子"),
            ["RPRpaddingTo32Array"] = ("パディング", "HLA側になし(GW算出)"),
            ["RPRpaddingTo64Array"] = ("パディング", "HLA側になし(GW算出)")
        };

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
        Expand(name, dataType, semantics, selector: "", amount: "1", depth: 0, rows,
            new HashSet<string>(StringComparer.Ordinal));
        return rows;
    }

    void Expand(string path, string dataType, string semantics, string selector, string amount,
        int depth, List<FlatField> rows, HashSet<string> visiting, string lengthRule = "")
    {
        var resolved = _resolver.Resolve(dataType);
        _model.DataTypes.TryGetValue(dataType, out var type);
        var kind = type?.Kind ?? resolved.Kind;

        bool composite = kind is FomDataTypeKind.FixedRecord or FomDataTypeKind.VariantRecord
            or FomDataTypeKind.Array;

        // A type already on the stack, or nesting this deep, cannot be broken open any further. It
        // is emitted whole rather than dropped, so no transferred bytes go unrecorded.
        if (composite && type is not null && depth < MaxDepth && visiting.Add(dataType))
        {
            switch (kind)
            {
                case FomDataTypeKind.FixedRecord:
                    foreach (var field in type.Fields)
                    {
                        Expand($"{path}.{field.Name}", field.DataType, field.Semantics,
                            selector, amount, depth + 1, rows, visiting);
                    }
                    break;

                case FomDataTypeKind.VariantRecord:
                    ExpandVariant(path, type, selector, amount, depth, rows, visiting);
                    break;

                case FomDataTypeKind.Array:
                    ExpandArray(path, type, semantics, selector, amount, depth, rows, visiting);
                    break;
            }

            visiting.Remove(dataType);
            return;
        }

        rows.Add(new FlatField(
            path,
            dataType,
            resolved.BaseRepresentation,
            resolved.SizeInBytes,
            amount,
            resolved.Units,
            selector,
            semantics,
            FirstRule(lengthRule, composite ? "展開打切り" : "", SubByteRule(resolved))));
    }

    void ExpandVariant(string path, FomDataType type, string selector, string amount, int depth,
        List<FlatField> rows, HashSet<string> visiting)
    {
        // The discriminant precedes the selected alternative on the wire and is what tells the
        // receiver which alternative follows, so it is always transferred.
        Expand($"{path}.{type.Discriminant}", type.DiscriminantDataType,
            $"{path} の判別子。", selector, amount, depth + 1, rows, visiting, lengthRule: "判別子");

        foreach (var alternative in type.Alternatives)
        {
            // Alternatives are mutually exclusive: only the one the discriminant names is present.
            // Carrying the selector down to every leaf is what keeps that visible once the container
            // rows are gone.
            Expand($"{path}.{alternative.Name}", alternative.DataType, alternative.Semantics,
                Combine(selector, alternative.Enumerator, " / "), amount, depth + 1, rows, visiting);
        }
    }

    void ExpandArray(string path, FomDataType type, string semantics, string selector, string amount,
        int depth, List<FlatField> rows, HashSet<string> visiting)
    {
        var elementCount = type.Cardinality;

        if (!IsFixedCardinality(type))
        {
            // The count is a field of the UDP layout in its own right, whether or not HLA carries it.
            elementCount = CountFieldName(path);
            rows.Add(new FlatField(elementCount, CountType, CountType, CountBytes, amount, "",
                selector, $"{path} の要素数。", CountRuleOf(type)));
        }

        var elementAmount = Combine(amount, elementCount, "*");
        _model.DataTypes.TryGetValue(type.ElementDataType, out var element);

        if (element is not null && element.Kind is FomDataTypeKind.FixedRecord
            or FomDataTypeKind.VariantRecord or FomDataTypeKind.Array)
        {
            // [] marks the repetition; the element's own members hang off it.
            Expand($"{path}[]", type.ElementDataType, semantics, selector, elementAmount,
                depth + 1, rows, visiting);
            return;
        }

        // An array of primitives is a single row: the element size times the count.
        var resolved = _resolver.Resolve(type.ElementDataType);
        rows.Add(new FlatField(path, type.ElementDataType, resolved.BaseRepresentation,
            resolved.SizeInBytes, elementAmount, resolved.Units, selector, semantics,
            FirstRule(LengthRuleOf(type), SubByteRule(resolved))));
    }

    /// <summary>
    /// Joins two occurrence counts or selectors. Nested arrays multiply and nested variants
    /// accumulate conditions; a plain 1 or an empty side contributes nothing.
    /// </summary>
    static string Combine(string outer, string inner, string separator)
    {
        if (outer.Length == 0 || outer == "1") return inner;
        if (inner.Length == 0 || inner == "1") return outer;

        return outer + separator + inner;
    }

    static bool IsFixedCardinality(FomDataType type) => int.TryParse(type.Cardinality, out _);

    static string CountFieldName(string path) => path + "_Count";

    static string LengthRuleOf(FomDataType type)
    {
        if (KnownEncodings.TryGetValue(type.Encoding, out var known)) return known.ArrayRule;

        // Fixed cardinality is unambiguous whatever the encoding is called.
        return IsFixedCardinality(type) ? "固定" : $"要確認({type.Encoding})";
    }

    /// <summary>
    /// Where the gateway gets this count. The field is always present in the UDP layout; what differs
    /// is whether HLA hands the value over or the gateway has to work it out.
    /// </summary>
    static string CountRuleOf(FomDataType type) =>
        KnownEncodings.TryGetValue(type.Encoding, out var known) && known.CountRule.Length > 0
            ? known.CountRule
            : $"要確認({type.Encoding})";

    static string FirstRule(params string[] candidates) =>
        candidates.FirstOrDefault(c => c.Length > 0) ?? "";

    /// <summary>
    /// A basic type narrower than a byte cannot be laid out in a byte-oriented ICD without bit
    /// packing, which this sheet has no way to express. Size is rounded up and the row says so,
    /// rather than reporting a byte count that does not match the wire.
    /// </summary>
    static string SubByteRule(ResolvedType resolved) =>
        resolved.IsWholeBytes ? "" : $"要確認(ビット幅{resolved.SizeInBits})";
}
