using System.Collections.ObjectModel;

namespace RulesCorpus.Internal;

/// <summary>
/// Copies into collections a caller cannot mutate. A <see cref="List{T}"/> or an array exposed as
/// <see cref="IReadOnlyList{T}"/> can be downcast and changed, which would let anyone holding a
/// validated manifest alter it after validation; the wrappers here hold a private copy.
/// </summary>
internal static class ReadOnly
{
    public static ReadOnlyCollection<T> List<T>(IEnumerable<T> items) => Array.AsReadOnly(items.ToArray());

    /// <summary>A copy in ordinal key order, which is the order the manifest documents.</summary>
    public static ReadOnlyDictionary<string, string> Map(IEnumerable<KeyValuePair<string, string>> items)
    {
        var copy = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (KeyValuePair<string, string> item in items)
        {
            copy.Add(item.Key, item.Value);
        }

        return new ReadOnlyDictionary<string, string>(copy);
    }
}
