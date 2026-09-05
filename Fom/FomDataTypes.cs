namespace ICDgenerator.Fom;

/// <summary>Which of the six &lt;dataTypes&gt; sections a type was declared in.</summary>
public enum FomDataTypeKind
{
    Unknown,
    Basic,
    Simple,
    Enumerated,
    Array,
    FixedRecord,
    VariantRecord
}

public sealed class FomEnumerator
{
    public string Name { get; set; } = "";
    public string Value { get; set; } = "";
    public IReadOnlyList<string> Notes { get; set; } = Array.Empty<string>();
}

/// <summary>A &lt;field&gt; of a fixed record.</summary>
public sealed class FomField
{
    public string Name { get; set; } = "";
    public string DataType { get; set; } = "";
    public string Semantics { get; set; } = "";
    public IReadOnlyList<string> Notes { get; set; } = Array.Empty<string>();
}

/// <summary>An &lt;alternative&gt; of a variant record, selected by a discriminant value.</summary>
public sealed class FomAlternative
{
    public string Enumerator { get; set; } = "";
    public string Name { get; set; } = "";
    public string DataType { get; set; } = "";
    public string Semantics { get; set; } = "";
}

/// <summary>
/// One declared data type. The six categories are modelled as a single class with a
/// <see cref="Kind"/> discriminator rather than a hierarchy: the ICD renders them into one flat
/// table, so a shape that matches the output keeps the exporter free of type switches.
/// Fields not relevant to a given kind stay at their defaults.
/// </summary>
public sealed class FomDataType
{
    public string Name { get; set; } = "";
    public FomDataTypeKind Kind { get; set; }
    public string Semantics { get; set; } = "";
    public IReadOnlyList<string> Notes { get; set; } = Array.Empty<string>();

    /// <summary>Basic only: width in bits.</summary>
    public int? Size { get; set; }

    /// <summary>Basic only.</summary>
    public string Endian { get; set; } = "";
    public string Interpretation { get; set; } = "";

    /// <summary>Simple and enumerated: the type this one is encoded as.</summary>
    public string Representation { get; set; } = "";

    /// <summary>Simple only.</summary>
    public string Units { get; set; } = "";
    public string Resolution { get; set; } = "";
    public string Accuracy { get; set; } = "";

    /// <summary>Enumerated only.</summary>
    public List<FomEnumerator> Enumerators { get; } = new();

    /// <summary>Array only: the element type.</summary>
    public string ElementDataType { get; set; } = "";

    /// <summary>Array only: a count, "Dynamic", or a range such as [1..2147483647].</summary>
    public string Cardinality { get; set; } = "";

    /// <summary>Array, fixed record and variant record.</summary>
    public string Encoding { get; set; } = "";

    /// <summary>Fixed record only.</summary>
    public List<FomField> Fields { get; } = new();

    /// <summary>Variant record only: the field whose value selects the alternative.</summary>
    public string Discriminant { get; set; } = "";

    /// <summary>Variant record only: the enumerated type of the discriminant.</summary>
    public string DiscriminantDataType { get; set; } = "";

    /// <summary>Variant record only.</summary>
    public List<FomAlternative> Alternatives { get; } = new();
}

/// <summary>An entry of the &lt;notes&gt; section, referenced by label from notes="..." attributes.</summary>
public sealed class FomNote
{
    public string Label { get; set; } = "";
    public string Semantics { get; set; } = "";
}
