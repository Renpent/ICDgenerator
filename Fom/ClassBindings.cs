using System.Text.Json;
using System.Text.Json.Serialization;

namespace ICDgenerator.Fom;

/// <summary>
/// The ID / Port / Rate a class is given on the 抽出概要 sheet, and which of its members it leaves
/// off the wire.
///
/// None of the three exists in a FOM: HLA has no port concept, update rates are rarely declared,
/// and nothing in HLA numbers a class. They are decisions someone makes when the gateway is
/// specified, and they used to be typed into the workbook by hand — which meant **regenerating the
/// workbook threw them away**, the index sheet writing all three blank every time.
///
/// <c>Id</c> and <c>Port</c> are not merely documentation: the id is the datagram header's
/// classId and the port is where the class is sent, so both must agree between the two ends, and
/// both reach the generated C++ (<c>kClassId</c>, <c>kPort</c>). <c>Rate</c> stays on the sheet —
/// the gateway sends every class on every tick of its loop, so the column describes what a
/// receiver may expect, not something the code acts on.
///
/// Port used to stay out too, as "deployment configuration". That assumed the gateway read ports at
/// runtime; it compiles them in, so generating the value removes a hand-copy without adding a
/// recompile.
/// </summary>
public sealed class ClassBinding
{
    /// <summary>Datagram header classId. Null until someone assigns one.</summary>
    public int? Id { get; set; }

    /// <summary>UDP port this class is sent on. One class per port.</summary>
    public int? Port { get; set; }

    /// <summary>Update rate in Hz. 0 means "on change only".</summary>
    public int? Rate { get; set; }

    /// <summary>
    /// Attributes or parameters left off the wire, by name. Empty means the whole class is sent,
    /// which is what every class did before this existed.
    ///
    /// Held as exclusions rather than inclusions so that an attribute the FOM gains later is sent
    /// by default instead of vanishing — and if that pushes the class past its payload, generation
    /// says so. Only whole attributes can go: a record type is shared by every class that uses it,
    /// so dropping one of its fields for one class would need a second copy of the type.
    /// </summary>
    public List<string> Excluded { get; set; } = new();

    public bool IsEmpty => Id is null && Port is null && Rate is null && Excluded.Count == 0;

    public ClassBinding Clone() => new() { Id = Id, Port = Port, Rate = Rate, Excluded = new(Excluded) };
}

/// <summary>
/// Bindings for every class, keyed by fully-qualified FOM name and persisted beside the FOM so they
/// travel with it, the same way <see cref="ArrayLimits"/> does.
/// </summary>
public sealed class ClassBindings
{
    [JsonPropertyName("byClass")]
    public Dictionary<string, ClassBinding> ByClass { get; set; } = new(StringComparer.Ordinal);

    /// <summary>
    /// The MTU the whole ICD assumes, 1500 or 9000. One value, not one per class: it is a property
    /// of the network path, and every class on it is carried in the same size of box. It reaches the
    /// generated C++ as <c>kPayload</c> on each class, and a class that cannot fit one record into
    /// that box is refused at generation rather than dropped at runtime.
    ///
    /// Absent from a file written before this existed, so it defaults to the standard MTU.
    /// </summary>
    [JsonPropertyName("mtu")]
    public int Mtu { get; set; } = 1500;

    public int? PortOf(string fullName) => For(fullName)?.Port;

    public ClassBinding? For(string fullName) =>
        ByClass.TryGetValue(fullName, out var binding) ? binding : null;

    public int? IdOf(string fullName) => For(fullName)?.Id;

    /// <summary>Stores a binding, dropping it when nothing is set so the file stays readable.</summary>
    public void Set(string fullName, ClassBinding binding)
    {
        if (binding.IsEmpty) ByClass.Remove(fullName);
        else ByClass[fullName] = binding;
    }

    public int AssignedCount => ByClass.Values.Count(b => b.Id is not null);

    /// <summary>Classes sending fewer than all of their attributes or parameters.</summary>
    public int TrimmedCount => ByClass.Values.Count(b => b.Excluded.Count > 0);

    public bool IsExcluded(string fullName, string member) =>
        For(fullName)?.Excluded.Contains(member, StringComparer.Ordinal) ?? false;

    /// <summary>Replaces a class's exclusions, keeping its ID / Port / Rate.</summary>
    public void SetExcluded(string fullName, IEnumerable<string> members)
    {
        var binding = For(fullName)?.Clone() ?? new ClassBinding();
        binding.Excluded = members.Distinct(StringComparer.Ordinal).ToList();
        Set(fullName, binding);
    }

    /// <summary>
    /// What an object class puts on the wire: its attributes in FOM order, less the excluded ones.
    ///
    /// **The one place the exclusions are applied.** The extraction sheets and the generated C++
    /// both read their member lists from here, so the sheet's size and <c>kEncodedSize</c> cannot
    /// drift apart — they are two renderings of one layout, and taking the list from two places
    /// would be exactly how they came to disagree.
    /// </summary>
    public List<(string Name, string DataType, string Semantics)> SentMembers(FomObjectClass cls) =>
        cls.AllAttributes.Where(a => !IsExcluded(cls.FullName, a.Name))
            .Select(a => (a.Name, a.DataType, a.Semantics)).ToList();

    /// <summary>What an interaction puts on the wire: its parameters less the excluded ones.</summary>
    public List<(string Name, string DataType, string Semantics)> SentMembers(FomInteractionClass cls) =>
        cls.AllParameters.Where(p => !IsExcluded(cls.FullName, p.Name))
            .Select(p => (p.Name, p.DataType, p.Semantics)).ToList();

    /// <summary>
    /// Exclusions actually in force on a class, in FOM order. A name the FOM no longer declares is
    /// left out, since there is nothing for it to exclude.
    /// </summary>
    public List<string> ExcludedMembers(IEnumerable<string> declared, string fullName) =>
        declared.Where(name => IsExcluded(fullName, name)).ToList();

    /// <summary>
    /// Exclusions naming a class or member this FOM does not have — a rename, or a file carried
    /// over from another FOM. Reported on load rather than dropped, since saving would otherwise
    /// discard them without anyone noticing.
    /// </summary>
    public List<string> StaleExclusions(FomModel model)
    {
        var declared = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var cls in model.AllObjectClasses)
            declared[cls.FullName] = cls.AllAttributes.Select(a => a.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var cls in model.AllInteractionClasses)
            declared[cls.FullName] = cls.AllParameters.Select(p => p.Name).ToHashSet(StringComparer.Ordinal);

        var stale = new List<string>();
        foreach (var (fullName, binding) in ByClass)
        {
            foreach (var member in binding.Excluded)
            {
                if (!declared.TryGetValue(fullName, out var names) || !names.Contains(member))
                    stale.Add($"{fullName}.{member}");
            }
        }
        return stale;
    }

    /// <summary>
    /// Ids used by more than one class.
    ///
    /// A duplicate is the one mistake here that cannot be caught downstream: the header carries no
    /// other discriminator, so two classes sharing an id are indistinguishable on the wire and a
    /// receiver decodes whichever it was compiled for. Ports collide less quietly — the second bind
    /// fails — but they are reported too, one class per port being the whole design.
    /// </summary>
    public IReadOnlyList<int> DuplicateIds() => Duplicates(b => b.Id);

    public IReadOnlyList<int> DuplicatePorts() => Duplicates(b => b.Port);

    List<int> Duplicates(Func<ClassBinding, int?> field) =>
        ByClass.Values.Select(field).OfType<int>()
            .GroupBy(v => v).Where(g => g.Count() > 1).Select(g => g.Key)
            .OrderBy(v => v).ToList();

    /// <summary>Bindings sit beside the FOM they were agreed for, so they travel with it.</summary>
    public static string PathFor(string fomPath) => fomPath + ".classbindings.json";

    static readonly JsonSerializerOptions Format = new() { WriteIndented = true };

    /// <summary>Returns empty when there is no file yet; a missing file is the normal first run.</summary>
    public static ClassBindings Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return new ClassBindings();
            return JsonSerializer.Deserialize<ClassBindings>(File.ReadAllText(path)) ?? new ClassBindings();
        }
        catch (Exception)
        {
            // A corrupt or hand-edited file falls back to empty rather than stopping the tool. The
            // loss is visible: the sheet's ID column goes blank and generation omits kClassId.
            return new ClassBindings();
        }
    }

    public void Save(string path) =>
        File.WriteAllText(path, JsonSerializer.Serialize(this, Format));

    public ClassBindings Clone()
    {
        var copy = new ClassBindings { Mtu = Mtu };
        foreach (var (name, binding) in ByClass) copy.ByClass[name] = binding.Clone();
        return copy;
    }
}
