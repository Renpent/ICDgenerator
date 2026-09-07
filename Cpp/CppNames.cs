using System.Text;

namespace ICDgenerator.Cpp;

/// <summary>
/// Turns FOM names into C++ identifiers.
///
/// A FOM is free to name things in ways C++ cannot spell — the RPR FOM uses a hyphen in 742 of its
/// 4,884 names, and 89 of those would sanitise into an identifier containing a double underscore,
/// which the standard reserves to the implementation. Collisions are resolved by appending a value
/// rather than a running number, so that adding an entry to a FOM never renames an existing one:
/// a generated header whose identifiers move under the caller is worse than an ugly identifier.
/// </summary>
public static class CppNames
{
    static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "alignas", "alignof", "and", "and_eq", "asm", "auto", "bitand", "bitor", "bool", "break",
        "case", "catch", "char", "char16_t", "char32_t", "class", "compl", "const", "constexpr",
        "const_cast", "continue", "decltype", "default", "delete", "do", "double", "dynamic_cast",
        "else", "enum", "explicit", "export", "extern", "false", "float", "for", "friend", "goto",
        "if", "inline", "int", "long", "mutable", "namespace", "new", "noexcept", "not", "not_eq",
        "nullptr", "operator", "or", "or_eq", "private", "protected", "public", "register",
        "reinterpret_cast", "return", "short", "signed", "sizeof", "static", "static_assert",
        "static_cast", "struct", "switch", "template", "this", "thread_local", "throw", "true",
        "try", "typedef", "typeid", "typename", "union", "unsigned", "using", "virtual", "void",
        "volatile", "wchar_t", "while", "xor", "xor_eq", "NULL"
    };

    /// <summary>
    /// The character-level rules, applied in order: substitute, collapse, then make sure the result
    /// is a legal and unreserved identifier. Nothing here can produce a collision that
    /// <see cref="Resolve"/> does not then see.
    /// </summary>
    public static string Sanitise(string name)
    {
        var sb = new StringBuilder(name.Length);

        foreach (char ch in name)
        {
            bool legal = ch is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '_';

            // Runs collapse to one: an identifier containing "__" anywhere is reserved, and the FOM
            // has 89 names that would otherwise produce one.
            if ((!legal || ch == '_') && sb.Length > 0 && sb[^1] == '_') continue;

            sb.Append(legal ? ch : '_');
        }

        var text = sb.ToString();
        if (text.Length == 0) return "k_";

        // A leading underscore followed by an upper-case letter is reserved, and one at global scope
        // is reserved outright; a leading digit is not an identifier at all.
        if (text[0] == '_' || char.IsDigit(text[0])) text = "k" + text;

        return Keywords.Contains(text) ? text + "_" : text;
    }

    /// <summary>
    /// Maps every name in one scope — the members of a record, the enumerators of an enum — to a
    /// unique C++ identifier. Where two names sanitise alike, all of them take a suffix from
    /// <paramref name="tieBreak"/>, which must itself be unique within the scope. Enumerators use
    /// their value, which the FOM already guarantees is distinct.
    /// </summary>
    /// <param name="warnings">Collision reports; a renamed identifier is never silent.</param>
    /// <param name="names">
    /// Key identifies the thing being named and is what the result is keyed by; Raw is the FOM name
    /// to spell in C++. They differ where two distinct things share a name — a class is keyed by its
    /// fully qualified name but spelled with its short one.
    /// </param>
    public static Dictionary<string, string> Resolve(
        IEnumerable<(string Key, string Raw, string TieBreak)> names, string scope, IList<string> warnings)
    {
        var groups = new Dictionary<string, List<(string Key, string Raw, string TieBreak)>>(StringComparer.Ordinal);

        foreach (var entry in names)
        {
            var identifier = Sanitise(entry.Raw);
            if (!groups.TryGetValue(identifier, out var group)) groups[identifier] = group = new();
            group.Add(entry);
        }

        var resolved = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var (identifier, group) in groups)
        {
            if (group.Count == 1)
            {
                resolved[group[0].Key] = identifier;
                continue;
            }

            foreach (var (key, raw, tieBreak) in group)
            {
                var unique = Sanitise(identifier + "_" + tieBreak);
                resolved[key] = unique;
                warnings.Add($"{scope}: 名前が衝突したため \"{raw}\" を {unique} にしました。");
            }
        }

        return resolved;
    }
}
