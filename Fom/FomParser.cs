using System.Xml.Linq;

namespace ICDgenerator.Fom;

/// <summary>
/// Reads an IEEE 1516-2010 OMT DIF file into a <see cref="FomModel"/>.
///
/// The namespace is taken from the document rather than assumed, so a FOM that omits the default
/// xmlns, or carries an older one, still parses. Only the element structure has to match.
/// (xsi:schemaLocation is a hint attribute and is never read.)
/// </summary>
public sealed class FomParser
{
    /// <summary>The namespace the IEEE 1516-2010 DIF schema defines.</summary>
    public static readonly XNamespace Ieee1516_2010 = "http://standards.ieee.org/IEEE1516-2010";

    /// <summary>Whatever namespace this document actually uses — possibly none.</summary>
    readonly XNamespace _ns;

    readonly FomModel _model;

    FomParser(XNamespace ns, FomModel model)
    {
        _ns = ns;
        _model = model;
    }

    public static FomModel Parse(string path)
    {
        var doc = XDocument.Load(path);
        var root = doc.Root
            ?? throw new InvalidDataException("XML has no root element.");

        if (root.Name.LocalName != "objectModel")
        {
            throw new InvalidDataException(
                $"Expected an OMT DIF <objectModel> root, found <{root.Name.LocalName}>.");
        }

        var ns = root.Name.Namespace;
        var model = new FomModel();

        if (ns != Ieee1516_2010)
        {
            model.Warnings.Add(ns == XNamespace.None
                ? "名前空間の宣言がありません。IEEE 1516-2010 の構造として読み込みます。"
                : $"名前空間が IEEE 1516-2010 ではありません ('{ns.NamespaceName}')。" +
                  "構造が異なる場合、一部の要素が読み取れていない可能性があります。");
        }

        var parser = new FomParser(ns, model);
        parser.ParseModel(root);
        parser.ReportUnhandled();
        return model;
    }

    void ParseModel(XElement root)
    {
        var model = _model;
        model.Identification = ParseIdentification(root.Element(_ns + "modelIdentification"));

        ParseDataTypes(root.Element(_ns + "dataTypes"), model);
        ParseNotes(root.Element(_ns + "notes"), model);
        ParseTransportations(root.Element(_ns + "transportations"), model);

        var objects = root.Element(_ns + "objects");
        if (objects is not null)
        {
            foreach (var el in objects.Elements(_ns + "objectClass"))
            {
                model.RootObjectClasses.Add(ParseObjectClass(el, parent: null, model));
            }
        }

        var interactions = root.Element(_ns + "interactions");
        if (interactions is not null)
        {
            foreach (var el in interactions.Elements(_ns + "interactionClass"))
            {
                model.RootInteractionClasses.Add(ParseInteractionClass(el, parent: null, model));
            }
        }

        if (model.AllObjectClasses.Count == 0 && model.AllInteractionClasses.Count == 0)
        {
            model.Warnings.Add(
                "オブジェクトクラスもインタラクションも読み取れませんでした。" +
                "名前空間または要素構造がこのツールの想定と異なる可能性があります。");
        }
    }

    /// <summary>Mirrors <see cref="ParseObjectClass"/>: parameters inherit down the nesting.</summary>
    FomInteractionClass ParseInteractionClass(XElement el, FomInteractionClass? parent, FomModel model)
    {
        var name = Text(el, "name");
        var cls = new FomInteractionClass
        {
            Name = name,
            FullName = parent is null ? name : $"{parent.FullName}.{name}",
            Level = parent is null ? 1 : parent.Level + 1,
            Sharing = Text(el, "sharing"),
            Transportation = Text(el, "transportation"),
            Order = Text(el, "order"),
            Semantics = Text(el, "semantics"),
            Notes = SplitNotes(el),
            Parent = parent
        };

        NoteUnhandled(el, "name", "sharing", "transportation", "order", "semantics",
            "parameter", "interactionClass");

        foreach (var paramEl in el.Elements(_ns + "parameter"))
        {
            NoteUnhandled(paramEl, "name", "dataType", "semantics");
            cls.OwnParameters.Add(new FomParameter
            {
                Name = Text(paramEl, "name"),
                DataType = Text(paramEl, "dataType"),
                Semantics = Text(paramEl, "semantics"),
                Notes = SplitNotes(paramEl),
                DeclaringClass = cls.FullName
            });
        }

        if (parent is not null) cls.AllParameters.AddRange(parent.AllParameters);
        cls.AllParameters.AddRange(cls.OwnParameters);

        model.AllInteractionClasses.Add(cls);

        foreach (var childEl in el.Elements(_ns + "interactionClass"))
        {
            cls.Children.Add(ParseInteractionClass(childEl, cls, model));
        }

        return cls;
    }

    /// <summary>
    /// Loads all six data type sections into one dictionary. Types are resolved by name after the
    /// fact rather than during the walk, because a type may reference one declared further down.
    /// </summary>
    void ParseDataTypes(XElement? el, FomModel model)
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
            var sectionEl = el.Element(_ns + section);
            if (sectionEl is null) continue;

            foreach (var typeEl in sectionEl.Elements(_ns + element))
            {
                var type = ParseDataType(typeEl, kind);
                if (type.Name.Length > 0) model.DataTypes[type.Name] = type;
            }
        }
    }

    FomDataType ParseDataType(XElement el, FomDataTypeKind kind)
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
                foreach (var e in el.Elements(_ns + "enumerator"))
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
                foreach (var f in el.Elements(_ns + "field"))
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
                foreach (var a in el.Elements(_ns + "alternative"))
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

    /// <summary>
    /// Reads the transportation types a FOM declares. The section is frequently absent or empty, in
    /// which case <see cref="FomModel.IsReliable"/> relies on the HLA standard names instead.
    /// </summary>
    void ParseTransportations(XElement? el, FomModel model)
    {
        if (el is null) return;

        foreach (var transportEl in el.Elements(_ns + "transportation"))
        {
            var name = Text(transportEl, "name");
            if (name.Length == 0) continue;

            model.Transportations[name] = new FomTransportation
            {
                Name = name,
                Reliable = ParseYesNo(Text(transportEl, "reliable")),
                Semantics = Text(transportEl, "semantics")
            };
        }
    }

    /// <summary>The DIF spells booleans as Yes/No; anything else is treated as undeclared.</summary>
    bool? ParseYesNo(string value) => value.Trim().ToLowerInvariant() switch
    {
        "yes" or "true" => true,
        "no" or "false" => false,
        _ => null
    };

    void ParseNotes(XElement? el, FomModel model)
    {
        if (el is null) return;

        foreach (var noteEl in el.Elements(_ns + "note"))
        {
            var note = new FomNote { Label = Text(noteEl, "label"), Semantics = Text(noteEl, "semantics") };
            if (note.Label.Length > 0) model.Notes[note.Label] = note;
        }
    }

    FomIdentification ParseIdentification(XElement? el)
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
    FomObjectClass ParseObjectClass(XElement el, FomObjectClass? parent, FomModel model)
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

        NoteUnhandled(el, "name", "sharing", "semantics", "attribute", "objectClass");

        foreach (var attrEl in el.Elements(_ns + "attribute"))
        {
            cls.OwnAttributes.Add(ParseAttribute(attrEl, cls.FullName));
        }

        if (parent is not null) cls.AllAttributes.AddRange(parent.AllAttributes);
        cls.AllAttributes.AddRange(cls.OwnAttributes);

        model.AllObjectClasses.Add(cls);

        foreach (var childEl in el.Elements(_ns + "objectClass"))
        {
            cls.Children.Add(ParseObjectClass(childEl, cls, model));
        }

        return cls;
    }

    FomAttribute ParseAttribute(XElement el, string declaringClass)
    {
        NoteUnhandled(el, "name", "dataType", "updateType", "updateCondition", "ownership",
            "sharing", "transportation", "order", "semantics");

        return new FomAttribute
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
    }

    /// <summary>
    /// Child element names this parser has never read, as "parent/child". IEEE 1516-2010 allows
    /// elements the RPR FOM happens not to use (DDM dimensions on an attribute, for instance), and a
    /// FOM that does use them would otherwise have them dropped without a word.
    /// </summary>
    readonly HashSet<string> _unhandled = new(StringComparer.Ordinal);

    void NoteUnhandled(XElement el, params string[] handled)
    {
        foreach (var child in el.Elements())
        {
            if (Array.IndexOf(handled, child.Name.LocalName) >= 0) continue;

            _unhandled.Add($"{el.Name.LocalName}/{child.Name.LocalName}");
        }
    }

    void ReportUnhandled()
    {
        if (_unhandled.Count == 0) return;

        _model.Warnings.Add(
            "このツールが読み取っていない要素があります: " +
            string.Join(", ", _unhandled.OrderBy(x => x, StringComparer.Ordinal)) +
            "。ICDに反映されていないため、必要なら対応を追加してください。");
    }

    string Text(XElement parent, string localName) =>
        parent.Element(_ns + localName)?.Value.Trim() ?? "";

    /// <summary>notes="RPRnoteBase2 RPRnoteBase18" holds several labels separated by spaces.</summary>
    IReadOnlyList<string> SplitNotes(XElement el)
    {
        var raw = (string?)el.Attribute("notes");
        if (string.IsNullOrWhiteSpace(raw)) return Array.Empty<string>();

        return raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
    }
}
