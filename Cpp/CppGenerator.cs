using System.Text;
using ICDgenerator.Excel;
using ICDgenerator.Fom;

namespace ICDgenerator.Cpp;

/// <summary>What a generation run produced, for the UI log.</summary>
public sealed class CppGenerationResult
{
    public List<string> Files { get; } = new();
    public List<string> Warnings { get; } = new();
}

/// <summary>
/// Raised when the FOM uses a construct the generator will not encode. Reporting the type names and
/// stopping is deliberate: a wrong layout compiled into a gateway is worse than no layout at all.
/// </summary>
public sealed class CppGenerationException : Exception
{
    public CppGenerationException(string message) : base(message) { }
}

/// <summary>
/// Emits C++11 codecs for the selected classes.
///
/// The input is the <see cref="FomModel"/> type tree, not the flattened ICD rows. The sheets show a
/// flat wire order because that is what a person reads down; a generator needs the structure, and a
/// dynamic array nested in another cannot be reconstructed from flattened paths — the occurrence
/// counts there are sums over per-parent counts, not products.
///
/// Generated types keep their FOM names (sanitised), so any row of the ICD can be grepped for in
/// the output.
/// </summary>
public sealed class CppGenerator
{
    const string RuntimeResource = "ICDgenerator.Cpp.Runtime.icd_codec.h";
    const string RuntimeFile = "icd_codec.h";
    const string TypesFile = "icd_types";
    const string ClassesFile = "icd_classes.h";

    /// <summary>Namespace for generated code, kept apart from the runtime's own.</summary>
    const string Namespace = "icdfom";

    /// <summary>
    /// Prepended to every shared FOM data type — enums, records, array typedefs — but not to the
    /// class structs, which are ICD records with no counterpart in an HLA toolkit.
    ///
    /// Empty by default: the generated types are the ones to use unless a project already has the
    /// same FOM types from its HLA toolkit. Setting a prefix makes them substitutable — strip or
    /// rewrite it and the codecs point at those definitions instead — and keeping it off the class
    /// structs means one search-and-replace touches exactly the types that have a counterpart.
    /// Nothing else in the output may start with the chosen token.
    /// </summary>
    public string TypePrefix { get; set; } = "";

    /// <summary>
    /// Outer namespace of an HLA toolkit's own generated FOM types. Setting it switches the emitter
    /// into reference mode: <c>icd_types.h</c> stops defining the FOM data types and includes those
    /// headers instead, so a project keeps one definition of each type rather than two.
    ///
    /// Reference mode assumes the shape that toolkit produces, which the caller must confirm:
    /// a header per type, every type reachable as <c>&lt;Outer&gt;::&lt;Type&gt;::&lt;Type&gt;</c>,
    /// record members named exactly after the FOM's fields, and an array that is a
    /// <c>std::vector</c> of its element.
    /// </summary>
    public string ExternalNamespace { get; set; } = "";

    /// <summary>Include line for one external type; {0} is the FOM type name.</summary>
    public string ExternalIncludePattern { get; set; } = "{0}.h";

    /// <summary>
    /// Ceilings for dynamic arrays. Every record is a fixed-size box, so these are what make its
    /// size a constant at all: an array occupies its ceiling whether or not it is full.
    /// </summary>
    public ArrayLimits Limits { get; set; } = new();

    /// <summary>
    /// The ID each class was given. Only the id is read: it is the datagram header's classId, so
    /// both ends must agree on it, and having it here spares the integrator a hand-copy that
    /// nothing would catch if it went wrong.
    ///
    /// Port and Rate stay out on purpose — they are deployment configuration, and baking them in
    /// would mean regenerating and recompiling to move a port.
    /// </summary>
    public ClassBindings Bindings { get; set; } = new();

    bool External => ExternalNamespace.Length > 0;

    readonly FomModel _model;
    readonly FomTypeResolver _resolver;
    readonly CppGenerationResult _result = new();

    /// <summary>FOM type name to C++ identifier, for every type the selection reaches.</summary>
    readonly Dictionary<string, string> _typeNames = new(StringComparer.Ordinal);

    /// <summary>Emission order: a type always follows the ones it is composed of.</summary>
    readonly List<FomDataType> _ordered = new();

    readonly Dictionary<string, int> _fixedSize = new(StringComparer.Ordinal);

    public CppGenerator(FomModel model, FomTypeResolver resolver)
    {
        _model = model;
        _resolver = resolver;
    }

    public CppGenerationResult Generate(IReadOnlyList<IcdSelection> selections, string directory)
    {
        if (selections.Count == 0)
        {
            throw new CppGenerationException("生成するクラスが選択されていません。");
        }

        Directory.CreateDirectory(directory);

        var classes = selections.Select(ResolveClass).ToList();
        CollectTypes(classes);
        NameTypes(classes);

        WriteRuntime(directory);
        WriteTypes(directory);
        foreach (var cls in classes) WriteClass(directory, cls);
        WriteClassIndex(directory, classes);

        return _result;
    }

    // ------------------------------------------------------------------
    // Model side
    // ------------------------------------------------------------------

    /// <summary>A selected class reduced to what generation needs: a name and a member list.</summary>
    sealed record GenClass(string FullName, string ShortName,
        IReadOnlyList<(string Name, string DataType, string Semantics)> Members);

    GenClass ResolveClass(IcdSelection selection)
    {
        if (selection.IsInteraction)
        {
            var interaction = _model.AllInteractionClasses.FirstOrDefault(c => c.FullName == selection.FullName)
                ?? throw new CppGenerationException($"インタラクション {selection.FullName} が見つかりません。");

            return new GenClass(interaction.FullName, interaction.Name,
                interaction.AllParameters.Select(p => (p.Name, p.DataType, p.Semantics)).ToList());
        }

        var objectClass = _model.AllObjectClasses.FirstOrDefault(c => c.FullName == selection.FullName)
            ?? throw new CppGenerationException($"オブジェクトクラス {selection.FullName} が見つかりません。");

        return new GenClass(objectClass.FullName, objectClass.Name,
            objectClass.AllAttributes.Select(a => (a.Name, a.DataType, a.Semantics)).ToList());
    }

    /// <summary>
    /// Transitive closure of the types the selection reaches, in dependency order. Variant records
    /// are collected as they are met and reported together, so one run tells the whole story.
    /// </summary>
    void CollectTypes(IReadOnlyList<GenClass> classes)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var onStack = new HashSet<string>(StringComparer.Ordinal);
        var variants = new SortedSet<string>(StringComparer.Ordinal);
        var unknown = new SortedSet<string>(StringComparer.Ordinal);
        var cycles = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var cls in classes)
        {
            foreach (var member in cls.Members) Visit(member.DataType);
        }

        if (variants.Count > 0)
        {
            throw new CppGenerationException(
                "可変レコードを含むクラスは生成できません。判別子で分岐するレイアウトには未対応です:\n  "
                + string.Join("\n  ", variants));
        }

        if (unknown.Count > 0)
        {
            throw new CppGenerationException(
                "C++の型に対応付けられないデータ型があります:\n  " + string.Join("\n  ", unknown));
        }

        if (cycles.Count > 0)
        {
            throw new CppGenerationException(
                "データ型が自分自身を含んでいるため、値として保持するC++の構造体にできません:\n  "
                + string.Join("\n  ", cycles));
        }

        void Visit(string typeName)
        {
            if (typeName.Length == 0 || visited.Contains(typeName)) return;

            if (!_model.TryGetDataType(typeName, out var type))
            {
                unknown.Add($"{typeName} (FOMにもMIMにも定義がありません)");
                visited.Add(typeName);
                return;
            }

            if (!onStack.Add(typeName))
            {
                cycles.Add(typeName);
                return;
            }

            switch (type.Kind)
            {
                case FomDataTypeKind.VariantRecord:
                    variants.Add(typeName);
                    break;

                case FomDataTypeKind.FixedRecord:
                    foreach (var field in type.Fields) Visit(field.DataType);
                    break;

                case FomDataTypeKind.Array:
                    Visit(type.ElementDataType);
                    break;

                case FomDataTypeKind.Simple:
                case FomDataTypeKind.Enumerated:
                    Visit(type.Representation);
                    break;

                case FomDataTypeKind.Basic:
                    if (PrimitiveOf(type) is null)
                    {
                        unknown.Add($"{typeName} (size={type.Size}, interpretation=\"{type.Interpretation}\")");
                    }
                    break;
            }

            onStack.Remove(typeName);
            visited.Add(typeName);

            // Basics become primitives inline and need no declaration of their own.
            if (type.Kind != FomDataTypeKind.Basic) _ordered.Add(type);
        }
    }

    /// <summary>
    /// Names every generated type and class in one pass, because they share one C++ namespace. A
    /// class is keyed by its fully qualified name — two classes in different branches may share a
    /// short name, and an object class and an interaction certainly can.
    /// </summary>
    void NameTypes(IReadOnlyList<GenClass> classes)
    {
        // The prefix goes through the same sanitising and collision check as everything else, so a
        // prefixed type still cannot collide with a class that happens to be named like one.
        var entries = _ordered
            .Select(t => (Key: t.Name, Raw: TypePrefix + t.Name, TieBreak: KindTag(t.Kind)))
            .Concat(classes.Select(c => (Key: c.FullName, Raw: c.ShortName, TieBreak: c.FullName)));

        foreach (var (key, name) in CppNames.Resolve(entries, "型名", _result.Warnings))
        {
            _typeNames[key] = name;
        }
    }

    static string KindTag(FomDataTypeKind kind) => kind switch
    {
        FomDataTypeKind.Array => "Array",
        FomDataTypeKind.FixedRecord => "Record",
        FomDataTypeKind.Enumerated => "Enum",
        _ => "Type"
    };

    /// <summary>
    /// The C++ primitive a basic type maps to, read from what the FOM says rather than from its
    /// name: the interpretation states the range, which is what settles signedness. Returns null
    /// when nothing in the declaration decides it, so the caller can report instead of guessing.
    /// </summary>
    static string? PrimitiveOf(FomDataType basic)
    {
        var interpretation = basic.Interpretation;

        if (interpretation.Contains("floating point", StringComparison.OrdinalIgnoreCase))
        {
            return basic.Size switch { 32 => "float", 64 => "double", _ => null };
        }

        // "Integer in the range [-2^31, 2^31 - 1]" is signed; "[0, 2^32-1]" is not. Anything else —
        // HLAoctet's "8-bit value" — is an opaque block of bits, which is unsigned.
        bool signed = interpretation.Contains("[-", StringComparison.Ordinal);

        return basic.Size switch
        {
            8 => signed ? "int8_t" : "uint8_t",
            16 => signed ? "int16_t" : "uint16_t",
            32 => signed ? "int32_t" : "uint32_t",
            64 => signed ? "int64_t" : "uint64_t",
            _ => null
        };
    }

    /// <summary>
    /// The one boolean the HLA standard defines. Its MIM declaration is an enumeration over
    /// HLAinteger32BE, but every HLA API hands it over as a plain bool, so that is what the
    /// generated code uses — four bytes on the wire, <c>bool</c> in the struct.
    ///
    /// Matched by the standard's own name and nothing else. A FOM is free to declare a boolean of
    /// its own, and guessing which of its two-valued enumerations meant one would be exactly the
    /// kind of assumption this tool does not make.
    /// </summary>
    static bool IsStandardBoolean(FomDataType type) =>
        type.Kind == FomDataTypeKind.Enumerated && type.Name == "HLAboolean";

    /// <summary>The C++ type used to declare a member of this FOM type.</summary>
    string MemberType(string typeName)
    {
        if (!_model.TryGetDataType(typeName, out var type))
        {
            throw new CppGenerationException($"未定義のデータ型 {typeName} が参照されています。");
        }

        if (type.Kind == FomDataTypeKind.Basic)
        {
            return PrimitiveOf(type) ?? throw new CppGenerationException($"{typeName} を型に対応付けられません。");
        }

        // bool is a C++ primitive, so it needs no declaration of its own and no namespace — the
        // runtime already carries its codec, and it stays bool in reference mode too.
        if (IsStandardBoolean(type)) return "bool";

        if (!_typeNames.TryGetValue(typeName, out var name))
        {
            throw new CppGenerationException($"{typeName} の識別子が決まっていません。");
        }

        // In reference mode every type comes from the toolkit under Outer::Name::Name, which is
        // the shape a C++03-era HLA generator produces. Self-contained output is not constrained by
        // that and uses the plain name, an `enum class` needing no wrapper to scope its values.
        if (External) return $"{ExternalNamespace}::{name}::{name}";

        return name;
    }

    /// <summary>
    /// Bytes a value occupies. Exact, not a minimum: a dynamic array is written at its ceiling, so
    /// every type has one constant size and a record is a fixed-size box.
    /// </summary>
    int FixedSizeOf(string typeName)
    {
        if (_fixedSize.TryGetValue(typeName, out var cached)) return cached;

        if (!_model.TryGetDataType(typeName, out var type)) return 1;

        // One byte, not the four its MIM representation implies: see FomFlattener.StandardBoolean.
        if (IsStandardBoolean(type)) return _fixedSize[typeName] = 1;

        // Set before recursing so a self-referential type cannot loop; CollectTypes has already
        // rejected those, this only keeps the walk finite if one slips through.
        _fixedSize[typeName] = 1;

        int size = type.Kind switch
        {
            FomDataTypeKind.Basic => ((type.Size ?? 8) + 7) / 8,
            FomDataTypeKind.Simple or FomDataTypeKind.Enumerated => FixedSizeOf(type.Representation),
            FomDataTypeKind.FixedRecord => type.Fields.Sum(f => FixedSizeOf(f.DataType)),
            FomDataTypeKind.Array => int.TryParse(type.Cardinality, out var count)
                ? count * FixedSizeOf(type.ElementDataType)
                // icd::kCountSize plus room for the ceiling, filled or not.
                : 2 + Limits.For(type.Name) * FixedSizeOf(type.ElementDataType),
            _ => 1
        };

        return _fixedSize[typeName] = Math.Max(size, 1);
    }

    // ------------------------------------------------------------------
    // Emission
    // ------------------------------------------------------------------

    /// <summary>
    /// UTF-8 **with** a BOM. MSVC on a non-UTF-8 system codepage — a Japanese Windows reads CP932 —
    /// otherwise decodes a UTF-8 source file as the local codepage and raises C4819, which is an
    /// error under /WX. The BOM is what tells it the encoding; GCC and Clang skip it silently.
    /// Every non-ASCII character in the output (the Japanese banner, an em dash in a comment) would
    /// trip this, so it is not a matter of avoiding a few characters.
    /// </summary>
    static readonly UTF8Encoding SourceEncoding = new(encoderShouldEmitUTF8Identifier: true);

    /// <summary>
    /// CRLF, on every line of every file. The two write paths below are the only places a generated
    /// file reaches disk, so normalising here is what makes that hold, rather than each emitter taking
    /// care. Before this, icd_codec.h went out byte for byte as stored — entirely LF — and a class
    /// header comment built with an embedded "\n" came out as one lone-LF line in an otherwise CRLF
    /// file. GCC and Clang treat CRLF as a line end, so the runtime's backslash-continued macro still
    /// splices when built on Linux.
    /// </summary>
    static string ToCrlf(string text) =>
        text.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", "\r\n");

    void WriteRuntime(string directory)
    {
        using var stream = typeof(CppGenerator).Assembly.GetManifestResourceStream(RuntimeResource)
            ?? throw new CppGenerationException($"埋め込みリソース {RuntimeResource} が見つかりません。");

        // Read as text rather than copied as bytes, so its line endings are normalised like every
        // other file's whatever the stored copy uses. A BOM on the resource is consumed here and
        // written back exactly once.
        using var reader = new StreamReader(stream, new UTF8Encoding(false),
            detectEncodingFromByteOrderMarks: true);
        var text = reader.ReadToEnd();

        File.WriteAllText(Path.Combine(directory, RuntimeFile), ToCrlf(text), SourceEncoding);
        _result.Files.Add(RuntimeFile);
    }

    void WriteTypes(string directory)
    {
        var header = new StringBuilder();
        Banner(header, "FOMのデータ型");
        header.AppendLine("#ifndef ICDFOM_TYPES_H");
        header.AppendLine("#define ICDFOM_TYPES_H");
        header.AppendLine();
        header.AppendLine($"#include \"{RuntimeFile}\"");

        var body = new StringBuilder();
        Banner(body, "FOMのデータ型");
        body.AppendLine($"#include \"{TypesFile}.h\"");
        body.AppendLine();

        if (External) WriteExternalTypes(header, body);
        else WriteOwnTypes(header, body);

        header.AppendLine();
        header.AppendLine("#endif  // ICDFOM_TYPES_H");

        Save(directory, TypesFile + ".h", header);
        Save(directory, TypesFile + ".cpp", body);
    }

    /// <summary>The self-contained form: this tool defines the FOM types and their codecs.</summary>
    void WriteOwnTypes(StringBuilder header, StringBuilder body)
    {
        header.AppendLine();
        header.AppendLine($"namespace {Namespace} {{");
        header.AppendLine();
        header.AppendLine("// The runtime's primitive and container codecs. Generated types are found through");
        header.AppendLine("// argument-dependent lookup; these are not, so they are named explicitly.");
        header.AppendLine("using icd::decode;");
        header.AppendLine("using icd::encode;");
        header.AppendLine("using icd::fixedSize;");
        header.AppendLine("using icd::wireSize;");
        header.AppendLine();

        body.AppendLine($"namespace {Namespace} {{");
        body.AppendLine();

        foreach (var type in _ordered)
        {
            if (IsStandardBoolean(type)) continue;

            switch (type.Kind)
            {
                case FomDataTypeKind.Simple: WriteSimple(header, type); break;
                case FomDataTypeKind.Enumerated: WriteEnum(header, type); break;
                case FomDataTypeKind.Array: WriteArray(header, type); break;
                case FomDataTypeKind.FixedRecord: WriteRecord(header, body, type); break;
            }
        }

        header.AppendLine($"}}  // namespace {Namespace}");
        body.AppendLine($"}}  // namespace {Namespace}");
    }

    /// <summary>
    /// The reference form: the toolkit's headers define the types, and only the codecs are emitted.
    ///
    /// Each codec goes inside its own type's namespace, which is not a style choice — a type's only
    /// associated namespace for argument-dependent lookup is the one it is declared in, so an
    /// overload left anywhere else is invisible to the container templates and a std::vector of that
    /// type would fail to compile. Reopening the toolkit's namespace to add them is ordinary C++.
    ///
    /// FixedSize is specialised explicitly rather than read off a kEncodedSize member, since the
    /// toolkit's types carry no such member. Being a class template specialisation it is found at
    /// instantiation wherever it is declared, so it goes in icd where it belongs.
    /// </summary>
    void WriteExternalTypes(StringBuilder header, StringBuilder body)
    {
        header.AppendLine();
        header.AppendLine($"// 型定義は {ExternalNamespace} 側のヘッダから取る。ここでは定義せず、");
        header.AppendLine("// ワイヤ形式のコーデックだけを各型の名前空間に足す。");

        foreach (var type in _ordered)
        {
            // bool comes from the language, not from the toolkit's headers.
            if (IsStandardBoolean(type)) continue;

            var include = string.Format(ExternalIncludePattern, _typeNames[type.Name]);
            header.AppendLine($"#include \"{include}\"");
        }
        header.AppendLine();

        var minSizes = new List<string>();

        foreach (var type in _ordered)
        {
            if (IsStandardBoolean(type)) continue;

            var name = _typeNames[type.Name];
            var qualified = $"{ExternalNamespace}::{name}::{name}";

            switch (type.Kind)
            {
                case FomDataTypeKind.Enumerated:
                    WriteExternalEnumCodec(header, type, name);
                    break;

                case FomDataTypeKind.FixedRecord:
                    WriteExternalRecordCodec(header, body, type, name);
                    break;

                // A simple type is a typedef to a primitive and an array a typedef to std::vector,
                // so both are already covered by the runtime's own codecs and traits.
                default:
                    continue;
            }

            minSizes.Add($"template <> struct FixedSize<{qualified}> "
                + $"{{ static constexpr std::size_t value = {FixedSizeOf(type.Name)}; }};");
        }

        header.AppendLine("namespace icd {");
        foreach (var line in minSizes) header.AppendLine(line);
        header.AppendLine("}  // namespace icd");
        header.AppendLine();
        header.AppendLine($"namespace {Namespace} {{");
        header.AppendLine("using icd::decode;");
        header.AppendLine("using icd::encode;");
        header.AppendLine("using icd::fixedSize;");
        header.AppendLine("using icd::wireSize;");
        header.AppendLine($"}}  // namespace {Namespace}");

        body.AppendLine("// コーデックの定義。宣言は icd_types.h 側にある。");
        body.AppendLine();
    }

    void WriteExternalEnumCodec(StringBuilder header, FomDataType type, string name)
    {
        var underlying = ExternalUnderlying(type.Representation);
        int width = FixedSizeOf(type.Name);

        header.AppendLine($"/// FOM: {type.Name}");
        header.AppendLine($"namespace {ExternalNamespace} {{ namespace {name} {{");
        header.AppendLine($"[[nodiscard]] inline icd::Result decode(icd::Reader& r, {name}& v) {{");
        header.AppendLine($"    {underlying} raw = 0;");
        header.AppendLine("    if (const icd::Result rc = icd::decode(r, raw); rc != icd::Result::Ok) return rc;");
        header.AppendLine($"    v = static_cast<{name}>(raw);");
        header.AppendLine("    return icd::Result::Ok;");
        header.AppendLine("}");
        header.AppendLine($"inline void encode(icd::Writer& w, {name} v) "
            + $"{{ icd::encode(w, static_cast<{underlying}>(v)); }}");
        header.AppendLine($"}} }}  // namespace {ExternalNamespace}::{name}");
        header.AppendLine();
    }

    void WriteExternalRecordCodec(StringBuilder header, StringBuilder body, FomDataType type, string name)
    {
        var members = CppNames.Resolve(
            type.Fields.Select(f => (Key: f.Name, Raw: f.Name, TieBreak: f.DataType)),
            name, _result.Warnings);

        header.AppendLine($"/// FOM: {type.Name}");
        header.AppendLine($"namespace {ExternalNamespace} {{ namespace {name} {{");
        header.AppendLine($"[[nodiscard]] icd::Result decode(icd::Reader& r, {name}& v);");
        header.AppendLine($"void encode(icd::Writer& w, const {name}& v);");
        header.AppendLine($"}} }}  // namespace {ExternalNamespace}::{name}");
        header.AppendLine();

        body.AppendLine($"namespace {ExternalNamespace} {{ namespace {name} {{");
        WriteCodecBody(body, name, type.Fields
            .Select(f => new CodecMember(members[f.Name], CallQualifier(f.DataType),
                BoundOf(f.DataType), InnerBoundOf(f.DataType)))
            .ToList(), FixedSizeOf(type.Name));
        body.AppendLine($"}} }}  // namespace {ExternalNamespace}::{name}");
        body.AppendLine();
    }

    /// <summary>
    /// The primitive an enum is carried as. Its representation is a simple type in the toolkit's
    /// namespace too, but the raw value has to be a plain integer to cast from, so this resolves
    /// past the typedef to the underlying C++ primitive.
    /// </summary>
    string ExternalUnderlying(string representation)
    {
        var basicName = _resolver.Resolve(representation).BaseRepresentation;

        return _model.TryGetDataType(basicName, out var basic) && PrimitiveOf(basic) is string primitive
            ? primitive
            : throw new CppGenerationException($"{representation} の基本表現を解決できません。");
    }

    void WriteSimple(StringBuilder header, FomDataType type)
    {
        var underlying = MemberType(type.Representation);
        header.AppendLine($"/// FOM: {type.Name}{Units(type)}");
        header.AppendLine($"typedef {underlying} {_typeNames[type.Name]};");
        header.AppendLine();
    }

    static string Units(FomDataType type) =>
        type.Units.Length > 0 && type.Units != "NA" ? $"  [{type.Units}]" : "";

    /// <summary>
    /// An `enum class` with the FOM's own name: the type is spelled Name and the enumerators
    /// Name::Value. Unscoped is not an option — the RPR FOM has 3,958 enumerators and names such as
    /// Other and Unknown repeat across dozens of them, which would collide in one namespace.
    ///
    /// This used to be an unscoped enum inside a namespace of its own name (type spelled Name::Name),
    /// to match what a C++03-era HLA generator emits so the two header sets could stand in for each
    /// other. Reference mode still spells types that way, since it is the toolkit's headers being
    /// included; self-contained output is not bound by it. **The enumerator spelling is the same
    /// either way**, which is why dropping the wrapper changed no calling code.
    ///
    /// The codec overloads sit next to it in <see cref="Namespace"/>, which is also the enum's only
    /// associated namespace for argument-dependent lookup — that is what lets the container
    /// templates find them for a std::vector of this enum.
    /// </summary>
    void WriteEnum(StringBuilder header, FomDataType type)
    {
        var name = _typeNames[type.Name];
        var underlying = MemberType(type.Representation);
        int width = FixedSizeOf(type.Name);

        var members = CppNames.Resolve(
            type.Enumerators.Select(e => (Key: e.Name, Raw: e.Name, TieBreak: e.Value)),
            name, _result.Warnings);

        header.AppendLine($"/// FOM: {type.Name}");
        header.AppendLine($"/// 値は {name}::<列挙子名>。");
        header.AppendLine($"enum class {name} : {underlying} {{");

        // Two enumerators may share a value in a FOM; C++ allows that, so they are emitted as-is.
        foreach (var enumerator in type.Enumerators)
        {
            var comment = members[enumerator.Name] == enumerator.Name ? "" : $"  ///< FOM: \"{enumerator.Name}\"";
            header.AppendLine($"    {members[enumerator.Name]} = {enumerator.Value},{comment}");
        }

        header.AppendLine("};");
        header.AppendLine();

        // An unrecognised value is passed through rather than rejected: a FOM may add enumerators,
        // and refusing one would drop a record that is otherwise intact.
        header.AppendLine($"[[nodiscard]] inline icd::Result decode(icd::Reader& r, {name}& v) {{");
        header.AppendLine($"    {underlying} raw = 0;");
        header.AppendLine("    if (const icd::Result rc = icd::decode(r, raw); rc != icd::Result::Ok) return rc;");
        header.AppendLine($"    v = static_cast<{name}>(raw);");
        header.AppendLine("    return icd::Result::Ok;");
        header.AppendLine("}");
        header.AppendLine();
        header.AppendLine($"inline void encode(icd::Writer& w, {name} v) {{");
        header.AppendLine($"    icd::encode(w, static_cast<{underlying}>(v));");
        header.AppendLine("}");
        header.AppendLine();
        header.AppendLine($"}}  // namespace {Namespace}");
        header.AppendLine("namespace icd {");
        header.AppendLine($"template <> struct FixedSize<{Namespace}::{name}> {{ static constexpr std::size_t value = {width}; }};");
        header.AppendLine("}");
        header.AppendLine($"namespace {Namespace} {{");
        header.AppendLine();
    }

    void WriteArray(StringBuilder header, FomDataType type)
    {
        var name = _typeNames[type.Name];
        var element = MemberType(type.ElementDataType);

        header.AppendLine($"/// FOM: {type.Name}  cardinality={type.Cardinality} encoding={type.Encoding}");

        if (int.TryParse(type.Cardinality, out var count))
        {
            header.AppendLine($"typedef std::array<{element}, {count}> {name};");
        }
        else
        {
            // Every dynamic array carries a count in the UDP layout, whatever the FOM's encoding
            // does on the HLA side; deriving that count is the gateway's problem, not this one's.
            header.AppendLine($"typedef std::vector<{element}> {name};");
        }

        header.AppendLine();
    }

    void WriteRecord(StringBuilder header, StringBuilder body, FomDataType type)
    {
        var name = _typeNames[type.Name];
        var members = CppNames.Resolve(
            type.Fields.Select(f => (Key: f.Name, Raw: f.Name, TieBreak: f.DataType)),
            name, _result.Warnings);

        var fields = type.Fields
            .Select(f => (Member: members[f.Name], Type: MemberType(f.DataType), Field: f))
            .ToList();

        header.AppendLine($"/// FOM: {type.Name}");
        header.AppendLine($"struct {name} {{");
        foreach (var (member, memberType, field) in fields)
        {
            header.AppendLine($"    {memberType} {member};  ///< FOM: {field.Name} : {field.DataType}");
        }
        header.AppendLine($"    static constexpr std::size_t kEncodedSize = {FixedSizeOf(type.Name)};");
        header.AppendLine("};");
        header.AppendLine();
        header.AppendLine($"[[nodiscard]] icd::Result decode(icd::Reader& r, {name}& v);");
        header.AppendLine($"void encode(icd::Writer& w, const {name}& v);");
        header.AppendLine();

        WriteCodecBody(body, name,
            fields.Select(f => new CodecMember(f.Member, "", BoundOf(f.Field.DataType),
                InnerBoundOf(f.Field.DataType))).ToList(),
            FixedSizeOf(type.Name));
    }

    /// <summary>One member of a record or class: what to call it, and how to reach its codec.</summary>
    /// <param name="Qualifier">
    /// Empty leaves the call unqualified, which is what the self-contained form wants: ordinary
    /// lookup in icdfom sees the runtime's codecs through using-declarations, and ADL reaches the
    /// generated ones. Inside a toolkit type's own namespace that breaks down — ordinary lookup
    /// finds that type's own overload and stops before ever reaching the primitives — so reference
    /// mode names each codec outright.
    /// </param>
    /// <param name="Bound">
    /// Ceiling when the member is a dynamic array, which is written at that size however full it is.
    /// Null for everything else, whose codec is reached the ordinary way.
    /// </param>
    /// <param name="InnerBound">
    /// Second ceiling when the elements are themselves dynamic arrays. An element that is a record
    /// containing an array needs none — that record's own codec knows its fields' ceilings.
    /// </param>
    readonly record struct CodecMember(string Name, string Qualifier = "",
        int? Bound = null, int? InnerBound = null);

    /// <summary>True for an array type with no fixed cardinality.</summary>
    bool IsDynamicArray(string typeName, out FomDataType? type) =>
        _model.TryGetDataType(typeName, out type)
        && type.Kind == FomDataTypeKind.Array
        && !int.TryParse(type.Cardinality, out _);

    /// <summary>The ceiling for a member that is a dynamic array; null when it is not one.</summary>
    int? BoundOf(string memberTypeName) =>
        IsDynamicArray(memberTypeName, out var type) ? Limits.For(type!.Name) : null;

    /// <summary>
    /// The elements' own ceiling when they are dynamic arrays too. Three levels of direct nesting
    /// would need a third, and are refused instead of quietly encoding something else.
    /// </summary>
    int? InnerBoundOf(string memberTypeName)
    {
        if (!IsDynamicArray(memberTypeName, out var outer)) return null;
        if (!IsDynamicArray(outer!.ElementDataType, out var inner)) return null;

        if (IsDynamicArray(inner!.ElementDataType, out _))
        {
            throw new CppGenerationException(
                $"可変長配列が3段直接入れ子になっています: {memberTypeName}。未対応です。");
        }

        return Limits.For(inner.Name);
    }

    /// <summary>
    /// The three functions are identical in shape for a record and for a class, so they are emitted
    /// from one place: read every member in declaration order, which is wire order.
    /// </summary>
    void WriteCodecBody(StringBuilder body, string name, IReadOnlyList<CodecMember> members, int size)
    {
        body.AppendLine($"icd::Result decode(icd::Reader& r, {name}& v) {{");
        if (members.Count == 0) body.AppendLine("    (void)r; (void)v;");

        foreach (var member in members)
        {
            var call = member.Bound is int bound
                ? member.InnerBound is int inner
                    ? $"icd::decodeBounded(r, v.{member.Name}, {bound}, {inner})"
                    : $"icd::decodeBounded(r, v.{member.Name}, {bound})"
                : $"{member.Qualifier}decode(r, v.{member.Name})";

            body.AppendLine($"    if (const icd::Result rc = {call}; rc != icd::Result::Ok) return rc;");
        }

        body.AppendLine("    return icd::Result::Ok;");
        body.AppendLine("}");
        body.AppendLine();

        body.AppendLine($"void encode(icd::Writer& w, const {name}& v) {{");
        if (members.Count == 0) body.AppendLine("    (void)w; (void)v;");
        foreach (var member in members)
        {
            body.AppendLine(member.Bound is int bound
                ? member.InnerBound is int inner
                    ? $"    icd::encodeBounded(w, v.{member.Name}, {bound}, {inner});"
                    : $"    icd::encodeBounded(w, v.{member.Name}, {bound});"
                : $"    {member.Qualifier}encode(w, v.{member.Name});");
        }
        body.AppendLine("}");
        body.AppendLine();

    }

    /// <summary>
    /// How a call to one member's codec must be spelled from inside a toolkit type's namespace.
    /// A record or enum has its codec in its own namespace; everything else — primitives, simple
    /// typedefs, arrays that are std::vector — is served by the runtime.
    /// </summary>
    string CallQualifier(string memberTypeName)
    {
        if (!External) return "";

        return _model.TryGetDataType(memberTypeName, out var type)
            && !IsStandardBoolean(type)
            && type.Kind is FomDataTypeKind.Enumerated or FomDataTypeKind.FixedRecord
                ? $"{ExternalNamespace}::{_typeNames[memberTypeName]}::"
                : "icd::";
    }

    /// <summary>
    /// One header that pulls in every class, so the file wiring the gateway together names it once
    /// instead of listing the classes — and cannot fall out of step with the ICD when a class is
    /// added or dropped, being generated from the same selection.
    ///
    /// The usual objection to an umbrella header is that it makes everything depend on everything,
    /// so touching one class rebuilds the lot. It does not bite here: this directory is regenerated
    /// wholesale, never edited, so a change arrives as every file at once and a finer dependency
    /// graph would save nothing. The weight is small either way — a class header is a thin shell
    /// over the shared icd_types.h, about 40 preprocessed lines against the 14,000 that
    /// &lt;vector&gt; alone costs.
    ///
    /// It is for the wiring, not for general use: code that touches one class should include that
    /// class.
    /// </summary>
    void WriteClassIndex(string directory, IReadOnlyList<GenClass> classes)
    {
        var guard = "ICDFOM_CLASSES_H";
        var header = new StringBuilder();

        Banner(header, "選択した全クラス");
        header.AppendLine($"#ifndef {guard}");
        header.AppendLine($"#define {guard}");
        header.AppendLine();
        header.AppendLine("// 配線用のまとめ include。個々のクラスだけを扱うコードは、そのクラスの");
        header.AppendLine("// ヘッダを直接 include すること。");
        header.AppendLine();

        foreach (var cls in classes)
        {
            var name = _typeNames[cls.FullName];
            var id = Bindings.IdOf(cls.FullName) is int classId ? $"ID {classId}" : "ID 未設定";
            header.AppendLine($"#include \"{name}.h\"  // {id} : {cls.FullName}");
        }

        header.AppendLine();
        header.AppendLine($"#endif  // {guard}");

        Save(directory, ClassesFile, header);
    }

    void WriteClass(string directory, GenClass cls)
    {
        var name = _typeNames[cls.FullName];
        var guard = "ICDFOM_" + name.ToUpperInvariant() + "_H";

        var members = CppNames.Resolve(
            cls.Members.Select(m => (Key: m.Name, Raw: m.Name, TieBreak: m.DataType)),
            name, _result.Warnings);

        var fields = cls.Members
            .Select(m => (Member: members[m.Name], Type: MemberType(m.DataType), Source: m))
            .ToList();

        int size = cls.Members.Sum(m => FixedSizeOf(m.DataType));

        var header = new StringBuilder();
        Banner(header, cls.FullName);
        header.AppendLine($"#ifndef {guard}");
        header.AppendLine($"#define {guard}");
        header.AppendLine();
        header.AppendLine($"#include \"{TypesFile}.h\"");
        header.AppendLine();
        header.AppendLine($"namespace {Namespace} {{");
        header.AppendLine();
        header.AppendLine($"/// FOM: {cls.FullName}");
        header.AppendLine("///");
        header.AppendLine("/// Members are in the ICD's row order, which is the order they occupy on the wire.");
        header.AppendLine($"struct {name} {{");
        foreach (var (member, memberType, source) in fields)
        {
            header.AppendLine($"    {memberType} {member};  ///< FOM: {source.Name} : {source.DataType}");
        }
        header.AppendLine($"    static constexpr std::size_t kEncodedSize = {size};");

        // Only when one has been assigned. Generation must not require ids: an ICD is often drafted
        // before anyone has agreed the numbering, and refusing to emit until then would put the
        // tool in the way of its own first use.
        if (Bindings.IdOf(cls.FullName) is int classId)
        {
            header.AppendLine($"    static constexpr std::uint32_t kClassId = {classId};  // 抽出概要シートの ID 列");
        }

        header.AppendLine("};");
        header.AppendLine();
        header.AppendLine($"[[nodiscard]] icd::Result decode(icd::Reader& r, {name}& v);");
        header.AppendLine($"void encode(icd::Writer& w, const {name}& v);");
        header.AppendLine();
        header.AppendLine("/// Reads the records batched into one datagram.");

        // The reader takes the id at runtime either way. Saying so differently depending on whether
        // one was assigned keeps the header from mentioning a member it does not have.
        header.AppendLine(Bindings.IdOf(cls.FullName) is null
            ? "/// The class id is a runtime argument: none was assigned when this was generated."
            : "/// The class id stays a runtime argument so a deployment can override the ICD's"
              + " number\n/// without regenerating; pass kClassId to take it.");
        header.AppendLine($"typedef icd::DatagramReader<{name}> {name}Reader;");
        header.AppendLine();
        header.AppendLine("/// Packs records into one datagram until the next one will not fit.");
        header.AppendLine($"typedef icd::DatagramWriter<{name}> {name}Writer;");
        header.AppendLine();
        header.AppendLine($"}}  // namespace {Namespace}");
        header.AppendLine();
        header.AppendLine($"#endif  // {guard}");

        var body = new StringBuilder();
        Banner(body, cls.FullName);
        body.AppendLine($"#include \"{name}.h\"");
        body.AppendLine();
        body.AppendLine($"namespace {Namespace} {{");
        body.AppendLine();
        WriteCodecBody(body, name,
            fields.Select(f => new CodecMember(f.Member, "", BoundOf(f.Source.DataType),
                InnerBoundOf(f.Source.DataType))).ToList(),
            size);
        body.AppendLine($"}}  // namespace {Namespace}");

        Save(directory, name + ".h", header);
        Save(directory, name + ".cpp", body);
    }

    static void Banner(StringBuilder sb, string subject)
    {
        sb.AppendLine("// 自動生成 — 編集しないこと。");
        sb.AppendLine($"// {subject}");
        sb.AppendLine("//");
        sb.AppendLine("// ICDgenerator が FOM から生成。C++17 / ビッグエンディアン / レコードは固定長。");
        sb.AppendLine();
    }

    void Save(string directory, string fileName, StringBuilder content)
    {
        File.WriteAllText(Path.Combine(directory, fileName), ToCrlf(content.ToString()), SourceEncoding);
        _result.Files.Add(fileName);
    }
}
