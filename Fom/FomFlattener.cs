namespace ICDgenerator.Fom;

/// <summary>
/// One value on the wire. Every row is real transferred data — container types are walked through
/// rather than listed, and <see cref="Path"/> carries the structure the containers used to show.
/// </summary>
/// <param name="Path">
/// Dotted path from the attribute or parameter. An array element carries an index variable —
/// <c>SegmentRecords[i].SegmentNumber</c> — and rows sharing an index belong to one repeating block.
/// </param>
/// <param name="SizeBytes">Size of a single occurrence; null when the encoding is variable length.</param>
/// <param name="Amount">
/// How many times this value occurs consecutively **within one iteration** of its enclosing block:
/// 1, a literal for a fixed-length run of primitives, or the name of the count row that decides it.
/// It is deliberately local — a value repeated by an enclosing block does not multiply into it,
/// because that is what made the sheet read as "all the A's, then all the B's".
/// </param>
/// <param name="Repeat">
/// The blocks this row sits inside, as index ranges: <c>i = 0..SegmentRecords_Count-1 (上限16)</c>,
/// accumulating outermost first when blocks nest. Empty when the row occurs exactly once.
/// </param>
/// <param name="MaxBytes">
/// Worst case for this row: size × amount × every enclosing bound. Summing the column over a class
/// gives the largest datagram it can produce. Null where a size is not fixed.
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
    string LengthRule = "",
    string Repeat = "",
    long? MaxBytes = null);

/// <summary>
/// Expands an attribute or parameter into the individual values that go on the wire, following
/// records, variants and arrays down to their primitives.
/// </summary>
public sealed class FomFlattener
{
    /// <summary>
    /// The element count that precedes a variable array in the UDP layout.
    ///
    /// Deliberately not HLA's own prefix. IEEE 1516-2010 gives HLAvariableArray a signed 32-bit
    /// big-endian count, but this row describes the datagram, not HLA: big-endian would contradict
    /// the layout's byte order, a count has no use for a sign, and for the RPRlengthlessArray types
    /// HLA carries no count at all — the gateway computes it, which is what 長さ決定 says. Naming it
    /// after an HLA type would claim a provenance the row does not have.
    ///
    /// 16 bits because a datagram caps the count long before it overflows: the widest case is an
    /// array of single-byte elements, 1,400 of which fit. 8 bits does not survive that — SignalData
    /// alone is audio payload running to hundreds of bytes.
    /// </summary>
    const int CountBytes = 2;

    const string CountType = "uint16";

    /// <summary>Deep enough for any real FOM; a backstop against pathological nesting.</summary>
    const int MaxDepth = 12;

    /// <summary>
    /// The one boolean the HLA standard defines, and the width it takes here.
    ///
    /// The MIM encodes it as an enumeration over HLAinteger32BE, so <see cref="FomTypeResolver"/>
    /// reports four bytes — correctly, because that describes the FOM. **This sheet describes the
    /// datagram**, where a boolean has no use for four bytes; the same reasoning made the element
    /// count a 16-bit little-endian field rather than HLA's 32-bit big-endian one. The full-parse
    /// sheets keep reporting what the FOM declares, which is the difference between the two views.
    ///
    /// Matched by the standard's own name and nothing else. A FOM may declare a boolean of its own
    /// — the RPR FOM has a one-byte RPRboolean — and picking out which of its two-valued
    /// enumerations meant one would be a guess.
    /// </summary>
    const string StandardBoolean = "HLAboolean";

    const int BooleanBytes = 1;

    /// <summary>The wire width when it differs from what the FOM declares; null when it does not.</summary>
    static int? WireSizeOverride(string dataType) =>
        dataType == StandardBoolean ? BooleanBytes : null;

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

    /// <summary>One enclosing repetition: its index variable, how the range reads, and its ceiling.</summary>
    readonly record struct Block(string Index, string Range, long Bound);

    readonly FomModel _model;
    readonly FomTypeResolver _resolver;
    readonly ArrayLimits _limits;

    public FomFlattener(FomModel model, FomTypeResolver resolver, ArrayLimits? limits = null)
    {
        _model = model;
        _resolver = resolver;
        _limits = limits ?? new ArrayLimits();
    }

    public List<FlatField> Flatten(string name, string dataType, string semantics)
    {
        var rows = new List<FlatField>();
        Expand(name, dataType, semantics, selector: "", Array.Empty<Block>(), depth: 0, rows,
            new HashSet<string>(StringComparer.Ordinal));
        return rows;
    }

    void Expand(string path, string dataType, string semantics, string selector,
        IReadOnlyList<Block> blocks, int depth, List<FlatField> rows, HashSet<string> visiting,
        string lengthRule = "")
    {
        var resolved = _resolver.Resolve(dataType);
        _model.TryGetDataType(dataType, out var type);
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
                            selector, blocks, depth + 1, rows, visiting);
                    }
                    break;

                case FomDataTypeKind.VariantRecord:
                    ExpandVariant(path, type, selector, blocks, depth, rows, visiting);
                    break;

                case FomDataTypeKind.Array:
                    ExpandArray(path, type, semantics, selector, blocks, depth, rows, visiting);
                    break;
            }

            visiting.Remove(dataType);
            return;
        }

        rows.Add(Row(path, dataType, resolved, amount: "1", maxAmount: 1, blocks, selector, semantics,
            FirstRule(lengthRule, composite ? "展開打切り" : "", SubByteRule(resolved)),
            WireSizeOverride(dataType)));
    }

    void ExpandVariant(string path, FomDataType type, string selector, IReadOnlyList<Block> blocks,
        int depth, List<FlatField> rows, HashSet<string> visiting)
    {
        // The discriminant precedes the selected alternative on the wire and is what tells the
        // receiver which alternative follows, so it is always transferred.
        Expand($"{path}.{type.Discriminant}", type.DiscriminantDataType,
            $"{path} の判別子。", selector, blocks, depth + 1, rows, visiting, lengthRule: "判別子");

        foreach (var alternative in type.Alternatives)
        {
            // Alternatives are mutually exclusive: only the one the discriminant names is present.
            // Carrying the selector down to every leaf is what keeps that visible once the container
            // rows are gone.
            Expand($"{path}.{alternative.Name}", alternative.DataType, alternative.Semantics,
                Combine(selector, alternative.Enumerator, " / "), blocks, depth + 1, rows, visiting);
        }
    }

    void ExpandArray(string path, FomDataType type, string semantics, string selector,
        IReadOnlyList<Block> blocks, int depth, List<FlatField> rows, HashSet<string> visiting)
    {
        bool dynamic = !IsFixedCardinality(type);
        string countName = "";
        long bound;
        string rangeEnd;

        if (dynamic)
        {
            // The count is a field of the UDP layout in its own right, whether or not HLA carries
            // it, and it stays even though the bound below gives the worst case: the bound sizes
            // the buffer, the count says how much of it is actually filled.
            countName = CountFieldName(path);
            bound = _limits.For(type.Name);
            rangeEnd = countName + "-1";

            rows.Add(Row(countName, CountType, _resolver.Resolve(CountType), amount: "1", maxAmount: 1,
                blocks, selector, $"{path} の要素数。", CountRuleOf(type), CountBytes));
        }
        else
        {
            bound = int.Parse(type.Cardinality);
            rangeEnd = (bound - 1).ToString();
        }

        _model.TryGetDataType(type.ElementDataType, out var element);

        if (element is not null && element.Kind is FomDataTypeKind.FixedRecord
            or FomDataTypeKind.VariantRecord or FomDataTypeKind.Array)
        {
            // The element expands to several rows, so this is a repeating block: give it an index
            // variable and record its range. Without that the rows read as "every A, then every B",
            // when the wire holds A B A B.
            var index = IndexName(blocks.Count);
            var range = $"{index} = 0..{rangeEnd}" + (dynamic ? $" (上限{bound})" : "");

            Expand($"{path}[{index}]", type.ElementDataType, semantics, selector,
                blocks.Append(new Block(index, range, bound)).ToList(), depth + 1, rows, visiting);
            return;
        }

        // An array of primitives is a single row, so there is no ordering to be ambiguous about and
        // no index is needed: the run simply repeats in place. The ceiling rides along with the
        // count because this row has no 繰り返し entry of its own to carry it, and dividing the
        // 領域 column by the element size to recover it is not something a reader should have to do.
        var resolved = _resolver.Resolve(type.ElementDataType);
        rows.Add(Row(path, type.ElementDataType, resolved,
            amount: dynamic ? $"{countName}（上限:{bound}）" : type.Cardinality, maxAmount: bound,
            blocks, selector, semantics, FirstRule(LengthRuleOf(type), SubByteRule(resolved)),
            WireSizeOverride(type.ElementDataType)));
    }

    /// <summary>
    /// Builds a row, working out the repetition text and the worst-case byte count from the blocks
    /// it sits inside. Keeping this in one place is what stops the two from drifting apart.
    /// </summary>
    static FlatField Row(string path, string typeName, ResolvedType resolved, string amount,
        long maxAmount, IReadOnlyList<Block> blocks, string selector, string semantics,
        string lengthRule, int? sizeOverride = null)
    {
        int? size = sizeOverride ?? resolved.SizeInBytes;

        long? maxBytes = null;
        if (size is int bytes)
        {
            long total = bytes * maxAmount;
            foreach (var block in blocks) total *= block.Bound;
            maxBytes = total;
        }

        return new FlatField(path, typeName, resolved.BaseRepresentation, size, amount,
            resolved.Units, selector, semantics, lengthRule,
            string.Join(", ", blocks.Select(b => b.Range)), maxBytes);
    }

    /// <summary>
    /// i, j, k … for successive nesting levels. Past the usual letters it keeps going rather than
    /// repeating one, since a repeated index would silently merge two different blocks.
    /// </summary>
    static string IndexName(int depth) =>
        depth < 6 ? "ijklmn"[depth].ToString() : "i" + (depth - 4);

    /// <summary>Joins two selectors; nested variants accumulate conditions.</summary>
    static string Combine(string outer, string inner, string separator)
    {
        if (outer.Length == 0) return inner;
        if (inner.Length == 0) return outer;

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
