using System.Xml.Linq;

namespace ICDgenerator.Fom;

/// <summary>
/// Reads an IEEE 1516-2010 OMT DIF file (e.g. the RPR FOM) into a <see cref="FomModel"/>.
/// </summary>
public static class FomParser
{
    /// <summary>
    /// Default namespace of the DIF schema. Every element in the document carries it,
    /// so element lookups must be namespace-qualified or they silently return null.
    /// </summary>
    public static readonly XNamespace Ns = "http://standards.ieee.org/IEEE1516-2010";

    public static FomModel Parse(string path)
    {
        var doc = XDocument.Load(path);
        var root = doc.Root
            ?? throw new InvalidDataException("XML has no root element.");

        if (root.Name != Ns + "objectModel")
        {
            throw new InvalidDataException(
                $"Expected an IEEE 1516-2010 <objectModel> root, found <{root.Name.LocalName}> " +
                $"in namespace '{root.Name.NamespaceName}'.");
        }

        var model = new FomModel
        {
            Identification = ParseIdentification(root.Element(Ns + "modelIdentification"))
        };

        var objects = root.Element(Ns + "objects");
        if (objects is not null)
        {
            foreach (var el in objects.Elements(Ns + "objectClass"))
            {
                var cls = ParseObjectClass(el, parent: null, model);
                model.RootObjectClasses.Add(cls);
            }
        }

        return model;
    }

    static FomIdentification ParseIdentification(XElement? el)
    {
        if (el is null) return new FomIdentification();

        return new FomIdentification
        {
            Name = Text(el, "name"),
            Type = Text(el, "type"),
            Version = Text(el, "version"),
            ModificationDate = Text(el, "modificationDate"),
            SecurityClassification = Text(el, "securityClassification"),
            Purpose = Text(el, "purpose"),
            ApplicationDomain = Text(el, "applicationDomain"),
            Description = Text(el, "description")
        };
    }

    /// <summary>
    /// Walks one class and its nested subclasses, carrying the ancestors' attributes down
    /// so that every class ends up with the full inherited set. Subclasses commonly declare
    /// no attributes of their own and exist purely to inherit.
    /// </summary>
    static FomObjectClass ParseObjectClass(XElement el, FomObjectClass? parent, FomModel model)
    {
        var name = Text(el, "name");
        var cls = new FomObjectClass
        {
            Name = name,
            FullName = parent is null ? name : $"{parent.FullName}.{name}",
            Level = parent is null ? 1 : parent.Level + 1,
            Sharing = Text(el, "sharing"),
            Semantics = Text(el, "semantics"),
            Notes = SplitNotes(el),
            Parent = parent
        };

        foreach (var attrEl in el.Elements(Ns + "attribute"))
        {
            cls.OwnAttributes.Add(ParseAttribute(attrEl, cls.FullName));
        }

        if (parent is not null) cls.AllAttributes.AddRange(parent.AllAttributes);
        cls.AllAttributes.AddRange(cls.OwnAttributes);

        model.AllObjectClasses.Add(cls);

        foreach (var childEl in el.Elements(Ns + "objectClass"))
        {
            cls.Children.Add(ParseObjectClass(childEl, cls, model));
        }

        return cls;
    }

    static FomAttribute ParseAttribute(XElement el, string declaringClass) => new()
    {
        Name = Text(el, "name"),
        DataType = Text(el, "dataType"),
        UpdateType = Text(el, "updateType"),
        UpdateCondition = Text(el, "updateCondition"),
        Ownership = Text(el, "ownership"),
        Sharing = Text(el, "sharing"),
        Transportation = Text(el, "transportation"),
        Order = Text(el, "order"),
        Semantics = Text(el, "semantics"),
        Notes = SplitNotes(el),
        DeclaringClass = declaringClass
    };

    static string Text(XElement parent, string localName) =>
        parent.Element(Ns + localName)?.Value.Trim() ?? "";

    /// <summary>notes="RPRnoteBase2 RPRnoteBase18" holds several labels separated by spaces.</summary>
    static IReadOnlyList<string> SplitNotes(XElement el)
    {
        var raw = (string?)el.Attribute("notes");
        if (string.IsNullOrWhiteSpace(raw)) return Array.Empty<string>();

        return raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
    }
}
