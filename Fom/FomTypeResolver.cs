namespace ICDgenerator.Fom;

/// <summary>The outcome of following a data type name down to its underlying representation.</summary>
/// <param name="SizeInBits">null when the encoding is variable length (dynamic arrays, variant records).</param>
public sealed record ResolvedType(
    string Name,
    FomDataTypeKind Kind,
    string BaseRepresentation,
    int? SizeInBits,
    string Units,
    string Encoding,
    bool IsKnown)
{
    public string SizeText => SizeInBits is int bits ? bits.ToString() : IsKnown ? "可変" : "";
}

/// <summary>
/// Follows the data type chain (attribute → variant/fixed record → simple → basic) to work out
/// the underlying representation and, where it is fixed, the width in bits.
/// </summary>
public sealed class FomTypeResolver
{
    /// <summary>
    /// Widths of the HLA basic types. These are declared in the standard MIM rather than in the FOM
    /// itself — the RPR FOM's basicDataRepresentations section only defines the four RPRunsignedInteger
    /// types — so referencing them without this table leaves most attributes unresolvable.
    /// </summary>
    static readonly Dictionary<string, int> MimBasicTypeSizes = new(StringComparer.Ordinal)
    {
        ["HLAoctet"] = 8,
        ["HLAbyte"] = 8,
        ["HLAASCIIchar"] = 8,
        ["HLAoctetPairBE"] = 16,
        ["HLAoctetPairLE"] = 16,
        ["HLAunicodeChar"] = 16,
        ["HLAinteger16BE"] = 16,
        ["HLAinteger16LE"] = 16,
        ["HLAinteger32BE"] = 32,
        ["HLAinteger32LE"] = 32,
        ["HLAinteger64BE"] = 64,
        ["HLAinteger64LE"] = 64,
        ["HLAfloat32BE"] = 32,
        ["HLAfloat32LE"] = 32,
        ["HLAfloat64BE"] = 64,
        ["HLAfloat64LE"] = 64
    };

    readonly FomModel _model;
    readonly Dictionary<string, ResolvedType> _cache = new(StringComparer.Ordinal);

    public FomTypeResolver(FomModel model) => _model = model;

    public ResolvedType Resolve(string typeName) => Resolve(typeName, new HashSet<string>(StringComparer.Ordinal));

    /// <param name="visiting">Guards against a record that reaches itself; the schema does not forbid it.</param>
    ResolvedType Resolve(string typeName, HashSet<string> visiting)
    {
        if (string.IsNullOrEmpty(typeName) || typeName == "NA")
        {
            return new ResolvedType(typeName, FomDataTypeKind.Unknown, "", null, "", "", IsKnown: false);
        }

        if (_cache.TryGetValue(typeName, out var cached)) return cached;

        if (!visiting.Add(typeName))
        {
            return new ResolvedType(typeName, FomDataTypeKind.Unknown, "", null, "", "", IsKnown: false);
        }

        var resolved = ResolveCore(typeName, visiting);
        visiting.Remove(typeName);

        // Only cache results that did not pass through a cycle guard, so a type is not
        // permanently recorded with the truncated size seen on the recursive path.
        if (visiting.Count == 0) _cache[typeName] = resolved;
        return resolved;
    }

    ResolvedType ResolveCore(string typeName, HashSet<string> visiting)
    {
        if (!_model.DataTypes.TryGetValue(typeName, out var type))
        {
            // Not in the FOM: it is either a MIM basic type or genuinely undefined.
            return MimBasicTypeSizes.TryGetValue(typeName, out var mimSize)
                ? new ResolvedType(typeName, FomDataTypeKind.Basic, typeName, mimSize, "", "", IsKnown: true)
                : new ResolvedType(typeName, FomDataTypeKind.Unknown, "", null, "", "", IsKnown: false);
        }

        switch (type.Kind)
        {
            case FomDataTypeKind.Basic:
                return new ResolvedType(typeName, type.Kind, typeName, type.Size, "", type.Encoding, true);

            case FomDataTypeKind.Simple:
            case FomDataTypeKind.Enumerated:
            {
                var under = Resolve(type.Representation, visiting);
                var units = type.Units.Length > 0 ? type.Units : under.Units;
                return new ResolvedType(typeName, type.Kind, under.BaseRepresentation, under.SizeInBits,
                    units, type.Encoding, true);
            }

            case FomDataTypeKind.Array:
            {
                var element = Resolve(type.ElementDataType, visiting);
                // Only a plain count gives a fixed width; "Dynamic" and ranges do not.
                int? size = int.TryParse(type.Cardinality, out var count) && element.SizeInBits is int elementBits
                    ? count * elementBits
                    : null;
                return new ResolvedType(typeName, type.Kind, element.BaseRepresentation, size,
                    element.Units, type.Encoding, true);
            }

            case FomDataTypeKind.FixedRecord:
            {
                int total = 0;
                bool fixedWidth = type.Fields.Count > 0;
                foreach (var field in type.Fields)
                {
                    var f = Resolve(field.DataType, visiting);
                    if (f.SizeInBits is int bits) total += bits;
                    else { fixedWidth = false; break; }
                }
                return new ResolvedType(typeName, type.Kind, "", fixedWidth ? total : null, "",
                    type.Encoding, true);
            }

            case FomDataTypeKind.VariantRecord:
                // The alternatives differ in width, so the encoded size depends on the value.
                return new ResolvedType(typeName, type.Kind, "", null, "", type.Encoding, true);

            default:
                return new ResolvedType(typeName, FomDataTypeKind.Unknown, "", null, "", "", false);
        }
    }
}
