namespace RulesCorpus.Json;

/// <summary>
/// The canonical-JSON value model: exactly what the format allows (objects, arrays, strings,
/// non-negative integers, booleans) and nothing else, so an unrepresentable value cannot be
/// built, let alone hashed. There is no null.
/// </summary>
internal abstract class CjValue
{
    public static CjString Of(string value) => new(value);

    public static CjNumber Of(long value) => new(value);

    public static CjBool Of(bool value) => value ? CjBool.True : CjBool.False;
}

/// <summary>An object. Members are kept in canonical order: ordinal, i.e. by UTF-16 code unit.</summary>
internal sealed class CjObject : CjValue
{
    public SortedDictionary<string, CjValue> Members { get; } = new(StringComparer.Ordinal);

    /// <summary>Adds a member; a duplicate key is a programming error.</summary>
    public CjObject Add(string key, CjValue value)
    {
        Members.Add(key, value);
        return this;
    }

    /// <summary>Adds a member only when it has a value: an absent optional member is omitted, never null.</summary>
    public CjObject AddOptional(string key, CjValue? value)
    {
        if (value is not null)
        {
            Members.Add(key, value);
        }

        return this;
    }
}

internal sealed class CjArray : CjValue
{
    public CjArray()
    {
        Items = [];
    }

    public CjArray(IEnumerable<CjValue> items)
    {
        Items = [.. items];
    }

    public List<CjValue> Items { get; }
}

internal sealed class CjString(string value) : CjValue
{
    public string Value { get; } = value;
}

internal sealed class CjNumber(long value) : CjValue
{
    public long Value { get; } = value;
}

internal sealed class CjBool : CjValue
{
    private CjBool(bool value)
    {
        Value = value;
    }

    public static CjBool True { get; } = new(true);

    public static CjBool False { get; } = new(false);

    public bool Value { get; }
}
