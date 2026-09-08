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

    /// <summary>Namespace for generated code, kept apart from the runtime's own.</summary>
    const string Namespace = "icdfom";

    /// <summary>
    /// Prepended to every shared FOM data type — enums, records, array typedefs — but not to the
    /// class structs, which are ICD records with no counterpart in an HLA toolkit.
    ///
    /// The point is substitutability. An HLA code generator emits the same FOM types under the same
    /// names, so a project that already has them can strip or rewrite this prefix and point the
    /// generated codecs at its own definitions. Keeping it off the class structs means one
    /// search-and-replace touches exactly the types that have a counterpart.
    /// </summary>
    public string TypePrefix { get; set; } = "ICD_";

    readonly FomModel _model;
    readonly FomTypeResolver _resolver;
    readonly CppGenerationResult _result = new();

    /// <summary>FOM type name to C++ identifier, for every type the selection reaches.</summary>
    readonly Dictionary<string, string> _typeNames = new(StringComparer.Ordinal);

    /// <summary>Emission order: a type always follows the ones it is composed of.</summary>
    readonly List<FomDataType> _ordered = new();

    readonly Dictionary<string, int> _minSize = new(StringComparer.Ordinal);

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

        return _typeNames.TryGetValue(typeName, out var name)
            ? name
            : throw new CppGenerationException($"{typeName} の識別子が決まっていません。");
    }

    /// <summary>
    /// Bytes a value occupies with every variable-length part empty — the divisor that bounds an
    /// element count against the bytes remaining, so it must never be zero.
    /// </summary>
    int MinSizeOf(string typeName)
    {
        if (_minSize.TryGetValue(typeName, out var cached)) return cached;

        if (!_model.TryGetDataType(typeName, out var type)) return 1;

        // Set before recursing so a self-referential type cannot loop; CollectTypes has already
        // rejected those, this only keeps the walk finite if one slips through.
        _minSize[typeName] = 1;

        int size = type.Kind switch
        {
            FomDataTypeKind.Basic => ((type.Size ?? 8) + 7) / 8,
            FomDataTypeKind.Simple or FomDataTypeKind.Enumerated => MinSizeOf(type.Representation),
            FomDataTypeKind.FixedRecord => type.Fields.Sum(f => MinSizeOf(f.DataType)),
            FomDataTypeKind.Array => int.TryParse(type.Cardinality, out var count)
                ? count * MinSizeOf(type.ElementDataType)
                : 2,  // icd::kCountSize — an empty dynamic array is still its count
            _ => 1
        };

        return _minSize[typeName] = Math.Max(size, 1);
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

    void WriteRuntime(string directory)
    {
        using var stream = typeof(CppGenerator).Assembly.GetManifestResourceStream(RuntimeResource)
            ?? throw new CppGenerationException($"埋め込みリソース {RuntimeResource} が見つかりません。");

        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        var bytes = buffer.ToArray();

        var path = Path.Combine(directory, RuntimeFile);
        var bom = SourceEncoding.GetPreamble();
        bool hasBom = bytes.Length >= bom.Length && bytes.Take(bom.Length).SequenceEqual(bom);

        using (var file = File.Create(path))
        {
            if (!hasBom) file.Write(bom, 0, bom.Length);
            file.Write(bytes, 0, bytes.Length);
        }

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
        header.AppendLine();
        header.AppendLine($"namespace {Namespace} {{");
        header.AppendLine();
        header.AppendLine("// The runtime's primitive and container codecs. Generated types are found through");
        header.AppendLine("// argument-dependent lookup; these are not, so they are named explicitly.");
        header.AppendLine("using icd::decode;");
        header.AppendLine("using icd::encode;");
        header.AppendLine("using icd::encodedSize;");
        header.AppendLine();

        var body = new StringBuilder();
        Banner(body, "FOMのデータ型");
        body.AppendLine($"#include \"{TypesFile}.h\"");
        body.AppendLine();
        body.AppendLine($"namespace {Namespace} {{");
        body.AppendLine();

        foreach (var type in _ordered)
        {
            switch (type.Kind)
            {
                case FomDataTypeKind.Simple: WriteSimple(header, type); break;
                case FomDataTypeKind.Enumerated: WriteEnum(header, type); break;
                case FomDataTypeKind.Array: WriteArray(header, type); break;
                case FomDataTypeKind.FixedRecord: WriteRecord(header, body, type); break;
            }
        }

        header.AppendLine($"}}  // namespace {Namespace}");
        header.AppendLine();
        header.AppendLine("#endif  // ICDFOM_TYPES_H");

        body.AppendLine($"}}  // namespace {Namespace}");

        Save(directory, TypesFile + ".h", header);
        Save(directory, TypesFile + ".cpp", body);
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

    void WriteEnum(StringBuilder header, FomDataType type)
    {
        var name = _typeNames[type.Name];
        var underlying = MemberType(type.Representation);
        int width = MinSizeOf(type.Name);

        var members = CppNames.Resolve(
            type.Enumerators.Select(e => (Key: e.Name, Raw: e.Name, TieBreak: e.Value)),
            name, _result.Warnings);

        header.AppendLine($"/// FOM: {type.Name}");
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
        header.AppendLine($"inline icd::Result decode(icd::Reader& r, {name}& v) {{");
        header.AppendLine($"    {underlying} raw = 0;");
        header.AppendLine("    icd::Result rc = decode(r, raw);");
        header.AppendLine("    if (rc != icd::Result::Ok) return rc;");
        header.AppendLine($"    v = static_cast<{name}>(raw);");
        header.AppendLine("    return icd::Result::Ok;");
        header.AppendLine("}");
        header.AppendLine();
        header.AppendLine($"inline void encode(icd::Writer& w, {name} v) {{");
        header.AppendLine($"    encode(w, static_cast<{underlying}>(v));");
        header.AppendLine("}");
        header.AppendLine();
        header.AppendLine($"inline std::size_t encodedSize({name}) {{ return {width}; }}");
        header.AppendLine();
        header.AppendLine($"}}  // namespace {Namespace}");
        header.AppendLine("namespace icd {");
        header.AppendLine($"template <> struct MinSize<{Namespace}::{name}> {{ enum : size_t {{ value = {width} }}; }};");
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
        header.AppendLine($"    enum : std::size_t {{ kMinEncodedSize = {MinSizeOf(type.Name)} }};");
        header.AppendLine("};");
        header.AppendLine();
        header.AppendLine($"icd::Result decode(icd::Reader& r, {name}& v);");
        header.AppendLine($"void encode(icd::Writer& w, const {name}& v);");
        header.AppendLine($"std::size_t encodedSize(const {name}& v);");
        header.AppendLine();

        WriteCodecBody(body, name, fields.Select(f => f.Member).ToList());
    }

    /// <summary>
    /// The three functions are identical in shape for a record and for a class, so they are emitted
    /// from one place: read every member in declaration order, which is wire order.
    /// </summary>
    void WriteCodecBody(StringBuilder body, string name, IReadOnlyList<string> members)
    {
        body.AppendLine($"icd::Result decode(icd::Reader& r, {name}& v) {{");
        if (members.Count == 0)
        {
            body.AppendLine("    (void)r; (void)v;");
        }
        else
        {
            body.AppendLine("    icd::Result rc;");
            foreach (var member in members)
            {
                body.AppendLine($"    rc = decode(r, v.{member});");
                body.AppendLine("    if (rc != icd::Result::Ok) return rc;");
            }
        }
        body.AppendLine("    return icd::Result::Ok;");
        body.AppendLine("}");
        body.AppendLine();

        body.AppendLine($"void encode(icd::Writer& w, const {name}& v) {{");
        if (members.Count == 0) body.AppendLine("    (void)w; (void)v;");
        foreach (var member in members) body.AppendLine($"    encode(w, v.{member});");
        body.AppendLine("}");
        body.AppendLine();

        body.AppendLine($"std::size_t encodedSize(const {name}& v) {{");
        if (members.Count == 0)
        {
            body.AppendLine("    (void)v;");
            body.AppendLine("    return 0;");
        }
        else
        {
            body.AppendLine("    std::size_t total = 0;");
            foreach (var member in members) body.AppendLine($"    total += encodedSize(v.{member});");
            body.AppendLine("    return total;");
        }
        body.AppendLine("}");
        body.AppendLine();
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

        int minSize = cls.Members.Sum(m => MinSizeOf(m.DataType));

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
        header.AppendLine($"    enum : std::size_t {{ kMinEncodedSize = {minSize} }};");
        header.AppendLine("};");
        header.AppendLine();
        header.AppendLine($"icd::Result decode(icd::Reader& r, {name}& v);");
        header.AppendLine($"void encode(icd::Writer& w, const {name}& v);");
        header.AppendLine($"std::size_t encodedSize(const {name}& v);");
        header.AppendLine();
        header.AppendLine("/// Reads the records batched into one datagram. The class id is a runtime argument:");
        header.AppendLine("/// the ICD's ID column is filled in by hand, so it is not known at generation time.");
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
        WriteCodecBody(body, name, fields.Select(f => f.Member).ToList());
        body.AppendLine($"}}  // namespace {Namespace}");

        Save(directory, name + ".h", header);
        Save(directory, name + ".cpp", body);
    }

    static void Banner(StringBuilder sb, string subject)
    {
        sb.AppendLine("// 自動生成 — 編集しないこと。");
        sb.AppendLine($"// {subject}");
        sb.AppendLine("//");
        sb.AppendLine("// ICDgenerator が FOM から生成。バイトオーダーはリトルエンディアン。");
        sb.AppendLine();
    }

    void Save(string directory, string fileName, StringBuilder content)
    {
        File.WriteAllText(Path.Combine(directory, fileName), content.ToString(), SourceEncoding);
        _result.Files.Add(fileName);
    }
}
