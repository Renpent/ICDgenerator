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
}

/// <summary>The parsed FOM. Only the pieces needed for the object class ICD so far.</summary>
public sealed class FomModel
{
    public FomIdentification Identification { get; set; } = new();

    /// <summary>Root classes (in practice a single HLAobjectRoot).</summary>
    public List<FomObjectClass> RootObjectClasses { get; } = new();

    /// <summary>Every object class in document order, flattened.</summary>
    public List<FomObjectClass> AllObjectClasses { get; } = new();

    /// <summary>Every declared data type, keyed by name. Types reference each other by name only.</summary>
    public Dictionary<string, FomDataType> DataTypes { get; } = new(StringComparer.Ordinal);

    /// <summary>Notes keyed by label, as referenced from notes="label1 label2" attributes.</summary>
    public Dictionary<string, FomNote> Notes { get; } = new(StringComparer.Ordinal);
}
