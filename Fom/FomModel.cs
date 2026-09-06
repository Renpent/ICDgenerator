namespace ICDgenerator.Fom;

/// <summary>Contents of the object model's &lt;modelIdentification&gt; block.</summary>
public sealed class FomIdentification
{
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
    public string Version { get; set; } = "";
    public string ModificationDate { get; set; } = "";
    public string SecurityClassification { get; set; } = "";
    public string Purpose { get; set; } = "";
    public string ApplicationDomain { get; set; } = "";
    public string Description { get; set; } = "";
}

/// <summary>An &lt;attribute&gt; of an object class.</summary>
public sealed class FomAttribute
{
    public string Name { get; set; } = "";
    public string DataType { get; set; } = "";
    public string UpdateType { get; set; } = "";
    public string UpdateCondition { get; set; } = "";
    public string Ownership { get; set; } = "";
    public string Sharing { get; set; } = "";
    public string Transportation { get; set; } = "";
    public string Order { get; set; } = "";
    public string Semantics { get; set; } = "";

    /// <summary>Note labels from the notes="a b c" attribute, already split.</summary>
    public IReadOnlyList<string> Notes { get; set; } = Array.Empty<string>();

    /// <summary>FQN of the class that declares this attribute (may be an ancestor).</summary>
    public string DeclaringClass { get; set; } = "";
}

/// <summary>A &lt;parameter&gt; of an interaction class.</summary>
public sealed class FomParameter
{
    public string Name { get; set; } = "";
    public string DataType { get; set; } = "";
    public string Semantics { get; set; } = "";
    public IReadOnlyList<string> Notes { get; set; } = Array.Empty<string>();

    /// <summary>FQN of the class that declares this parameter (may be an ancestor).</summary>
    public string DeclaringClass { get; set; } = "";
}

/// <summary>
/// An &lt;interactionClass&gt;. Nested the same way object classes are, and parameters are
/// inherited by subclasses in the same manner.
/// </summary>
public sealed class FomInteractionClass
{
    public string Name { get; set; } = "";
    public string FullName { get; set; } = "";
    public int Level { get; set; }

    public string Sharing { get; set; } = "";
    public string Transportation { get; set; } = "";
    public string Order { get; set; } = "";
    public string Semantics { get; set; } = "";
    public IReadOnlyList<string> Notes { get; set; } = Array.Empty<string>();

    public FomInteractionClass? Parent { get; set; }
    public List<FomInteractionClass> Children { get; } = new();

    public List<FomParameter> OwnParameters { get; } = new();
    public List<FomParameter> AllParameters { get; } = new();

    public bool IsLeaf => Children.Count == 0;

    /// <summary>
    /// Whether a federate may send this interaction. Unlike object classes, an interaction can be
    /// publishable and still have subclasses — Collision is sent in its own right as well as being
    /// the parent of CollisionElastic — so this, not <see cref="IsLeaf"/>, decides what the ICD lists.
    /// </summary>
    public bool IsPublishable => Sharing.Contains("Publish", StringComparison.Ordinal);
}

/// <summary>
/// An &lt;objectClass&gt;. In the 1516-2010 DIF, subclasses are nested inside their
/// parent element rather than referencing it by name, so this mirrors that tree.
/// </summary>
public sealed class FomObjectClass
{
    public string Name { get; set; } = "";

    /// <summary>Dot-joined path from the root, e.g. HLAobjectRoot.BaseEntity.PhysicalEntity.</summary>
    public string FullName { get; set; } = "";

    /// <summary>1 for HLAobjectRoot.</summary>
    public int Level { get; set; }

    public string Sharing { get; set; } = "";
    public string Semantics { get; set; } = "";
    public IReadOnlyList<string> Notes { get; set; } = Array.Empty<string>();

    public FomObjectClass? Parent { get; set; }
    public List<FomObjectClass> Children { get; } = new();

    /// <summary>Attributes declared on this class only.</summary>
    public List<FomAttribute> OwnAttributes { get; } = new();

    /// <summary>Attributes inherited from ancestors, followed by this class's own.</summary>
    public List<FomAttribute> AllAttributes { get; } = new();

    public bool IsLeaf => Children.Count == 0;

    /// <summary>
    /// Whether a federate may publish instances of this class. In the RPR FOM this coincides exactly
    /// with <see cref="IsLeaf"/> — all 49 leaves are PublishSubscribe and no intermediate class is —
    /// but the ICD keys off publishability because that is the property that actually matters.
    /// </summary>
    public bool IsPublishable => Sharing.Contains("Publish", StringComparison.Ordinal);
}

/// <summary>The parsed FOM. Only the pieces needed for the object class ICD so far.</summary>
public sealed class FomModel
{
    public FomIdentification Identification { get; set; } = new();

    /// <summary>Root classes (in practice a single HLAobjectRoot).</summary>
    public List<FomObjectClass> RootObjectClasses { get; } = new();

    /// <summary>Every object class in document order, flattened.</summary>
    public List<FomObjectClass> AllObjectClasses { get; } = new();

    /// <summary>Root interaction classes (in practice a single HLAinteractionRoot).</summary>
    public List<FomInteractionClass> RootInteractionClasses { get; } = new();

    /// <summary>Every interaction class in document order, flattened.</summary>
    public List<FomInteractionClass> AllInteractionClasses { get; } = new();

    /// <summary>Every declared data type, keyed by name. Types reference each other by name only.</summary>
    public Dictionary<string, FomDataType> DataTypes { get; } = new(StringComparer.Ordinal);

    /// <summary>Notes keyed by label, as referenced from notes="label1 label2" attributes.</summary>
    public Dictionary<string, FomNote> Notes { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Things the parser could not take for granted — an unexpected namespace, a size that is not a
    /// whole number of bytes. Surfaced in the UI log and on the 概要 sheet rather than thrown, so an
    /// unusual but readable FOM still produces output.
    /// </summary>
    public List<string> Warnings { get; } = new();

    /// <summary>
    /// Transportation types the FOM declares. Often empty — the RPR FOM leaves the section
    /// self-closing — in which case <see cref="IsReliable"/> falls back to the HLA standard types.
    /// </summary>
    public Dictionary<string, FomTransportation> Transportations { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Whether a transportation delivers reliably. Prefers the FOM's own declaration so that a model
    /// defining its own transportation types works; falls back to the two types the HLA standard
    /// defines, and returns null when neither says — a caller must not guess in that case.
    /// </summary>
    public bool? IsReliable(string transportation)
    {
        if (string.IsNullOrEmpty(transportation)) return null;

        if (Transportations.TryGetValue(transportation, out var declared) && declared.Reliable is bool value)
        {
            return value;
        }

        return transportation switch
        {
            "HLAreliable" => true,
            "HLAbestEffort" => false,
            _ => null
        };
    }
}
