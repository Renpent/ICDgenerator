using System.Text.Json;
using System.Text.Json.Serialization;

namespace ICDgenerator.Fom;

/// <summary>
/// Upper bounds on how many elements a dynamic array may carry.
///
/// A FOM does not say — the RPR FOM declares `[1..2147483647]` for eleven types and plain "Dynamic"
/// for the rest, which is no bound at all. But a datagram has to fit an MTU, so the layout is only
/// specifiable once someone decides the ceiling. That decision is a federation agreement, not
/// something derivable, so it is entered by hand and kept here.
///
/// Keyed by array *data type*, because the same array is reused across classes and a limit agreed
/// for it should hold everywhere it appears.
/// </summary>
public sealed class ArrayLimits
{
    /// <summary>Applied to any dynamic array with no limit of its own.</summary>
    public int Default { get; set; } = 16;

    [JsonPropertyName("byType")]
    public Dictionary<string, int> ByType { get; set; } = new(StringComparer.Ordinal);

    /// <summary>The bound for one array type. Never zero, so it can be multiplied and divided by.</summary>
    public int For(string arrayTypeName) =>
        Math.Max(1, ByType.TryGetValue(arrayTypeName, out var limit) ? limit : Default);

    /// <summary>Whether this type was given a limit rather than falling back to the default.</summary>
    public bool IsExplicit(string arrayTypeName) => ByType.ContainsKey(arrayTypeName);

    public void Set(string arrayTypeName, int? limit)
    {
        if (limit is int value && value > 0) ByType[arrayTypeName] = value;
        else ByType.Remove(arrayTypeName);
    }

    /// <summary>Limits sit beside the FOM they were agreed for, so they travel with it.</summary>
    public static string PathFor(string fomPath) => fomPath + ".arraylimits.json";

    static readonly JsonSerializerOptions Format = new() { WriteIndented = true };

    /// <summary>Returns defaults when there is no file yet; a missing file is the normal first run.</summary>
    public static ArrayLimits Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return new ArrayLimits();
            return JsonSerializer.Deserialize<ArrayLimits>(File.ReadAllText(path)) ?? new ArrayLimits();
        }
        catch (Exception)
        {
            // A corrupt or hand-edited file falls back to defaults rather than stopping the tool;
            // the sheet says which limits are in force, so the loss is visible.
            return new ArrayLimits();
        }
    }

    public void Save(string path) =>
        File.WriteAllText(path, JsonSerializer.Serialize(this, Format));
}
