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

        ParseDataTypes(root.Element(Ns + "dataTypes"), model);
        ParseNotes(root.Element(Ns + "notes"), model);

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

    /// <summary>
    /// Loads all six data type sections into one dictionary. Types are resolved by name after the
    /// fact rather than during the walk, because a type may reference one declared further down.
    /// </summary>
    static void ParseDataTypes(XElement? el, FomModel model)
    {
        if (el is null) return;

        foreach (var (section, element, kind) in new[]
        {
            ("basicDataRepresentations", "basicData", FomDataTypeKind.Basic),
            ("simpleDataTypes", "simpleData", FomDataTypeKind.Simple),
            ("enumeratedDataTypes", "enumeratedData", FomDataTypeKind.Enumerated),
            ("arrayDataTypes", "arrayData", FomDataTypeKind.Array),
            ("fixedRecordDataTypes", "fixedRecordData", FomDataTypeKind.FixedRecord),
            ("variantRecordDataTypes", "variantRecordData", FomDataTypeKind.VariantRecord)
        })
        {
            var sectionEl = el.Element(Ns + section);
            if (sectionEl is null) continue;

            foreach (var typeEl in sectionEl.Elements(Ns + element))
            {
                var type = ParseDataType(typeEl, kind);
                if (type.Name.Length > 0) model.DataTypes[type.Name] = type;
            }
        }
    }

    static FomDataType ParseDataType(XElement el, FomDataTypeKind kind)
    {
        var type = new FomDataType
        {
            Name = Text(el, "name"),
            Kind = kind,
            Semantics = Text(el, "semantics"),
            Notes = SplitNotes(el),
            Encoding = Text(el, "encoding")
        };

        switch (kind)
        {
            case FomDataTypeKind.Basic:
                type.Size = int.TryParse(Text(el, "size"), out var size) ? size : null;
                type.Endian = Text(el, "endian");
                type.Interpretation = Text(el, "interpretation");
                break;

            case FomDataTypeKind.Simple:
                type.Representation = Text(el, "representation");
                type.Units = Text(el, "units");
                type.Resolution = Text(el, "resolution");
                type.Accuracy = Text(el, "accuracy");
                break;

            case FomDataTypeKind.Enumerated:
                type.Representation = Text(el, "representation");
                foreach (var e in el.Elements(Ns + "enumerator"))
                {
                    type.Enumerators.Add(new FomEnumerator
                    {
                        Name = Text(e, "name"),
                        Value = Text(e, "value"),
                        Notes = SplitNotes(e)
                    });
                }
                break;

            case FomDataTypeKind.Array:
                type.ElementDataType = Text(el, "dataType");
                type.Cardinality = Text(el, "cardinality");
                break;

            case FomDataTypeKind.FixedRecord:
                foreach (var f in el.Elements(Ns + "field"))
                {
                    type.Fields.Add(new FomField
                    {
                        Name = Text(f, "name"),
                        DataType = Text(f, "dataType"),
                        Semantics = Text(f, "semantics"),
                        Notes = SplitNotes(f)
                    });
                }
                break;

            case FomDataTypeKind.VariantRecord:
                type.Discriminant = Text(el, "discriminant");
                type.DiscriminantDataType = Text(el, "dataType");
                foreach (var a in el.Elements(Ns + "alternative"))
                {
                    type.Alternatives.Add(new FomAlternative
                    {
                        Enumerator = Text(a, "enumerator"),
                        Name = Text(a, "name"),
                        DataType = Text(a, "dataType"),
                        Semantics = Text(a, "semantics")
                    });
                }
                break;
        }

        return type;
    }

    static void ParseNotes(XElement? el, FomModel model)
    {
        if (el is null) return;

        foreach (var noteEl in el.Elements(Ns + "note"))
        {
            var note = new FomNote { Label = Text(noteEl, "label"), Semantics = Text(noteEl, "semantics") };
            if (note.Label.Length > 0) model.Notes[note.Label] = note;
        }
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
