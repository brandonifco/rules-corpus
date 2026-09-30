namespace RulesCorpus.Json;

/// <summary>
/// Reads the members of one JSON object against a schema, appending a specific error (member
/// path and reason) for every missing, mistyped, ungrammatical or unknown member instead of
/// stopping at the first. <see cref="Finish"/> reports whatever was not asked for as unknown.
/// </summary>
internal sealed class JsonMembers
{
    private readonly CjObject obj;
    private readonly List<CorpusError> errors;
    private readonly HashSet<string> consumed = new(StringComparer.Ordinal);

    public JsonMembers(CjValue? value, string path, List<CorpusError> errors)
    {
        Path = path;
        this.errors = errors;
        if (value is CjObject o)
        {
            obj = o;
            IsObject = true;
        }
        else
        {
            obj = new CjObject();
            if (value is not null)
            {
                errors.Add(new CorpusError(path, "must be an object"));
            }
        }
    }

    public string Path { get; }

    public List<CorpusError> Errors => errors;

    public bool IsObject { get; }

    public string PathOf(string key) => Path + "." + key;

    public bool Has(string key) => obj.Members.ContainsKey(key);

    public void Error(string key, string reason) => errors.Add(new CorpusError(PathOf(key), reason));

    /// <summary>The raw value, or null (with an error when required) if absent.</summary>
    public CjValue? Value(string key, bool required)
    {
        consumed.Add(key);
        if (obj.Members.TryGetValue(key, out CjValue? value))
        {
            return value;
        }

        if (required && IsObject)
        {
            Error(key, "is required but missing");
        }

        return null;
    }

    public string? String(string key, bool required, Func<string, bool>? grammar = null, string? grammarDescription = null, bool allowEmpty = false)
    {
        CjValue? value = Value(key, required);
        if (value is null)
        {
            return null;
        }

        if (value is not CjString s)
        {
            Error(key, "must be a string");
            return null;
        }

        if (!allowEmpty && s.Value.Length == 0)
        {
            Error(key, "must not be empty");
            return null;
        }

        if (grammar is not null && !grammar(s.Value))
        {
            Error(key, $"'{s.Value}' is not a valid {grammarDescription}");
            return null;
        }

        return s.Value;
    }

    public string? Id(string key, bool required, string what) =>
        String(key, required, CorpusGrammar.IsId, $"{what} ([a-z0-9]+([-.][a-z0-9]+)*, at most 128 characters)");

    /// <summary>Requires the member to hold exactly this string.</summary>
    public void Constant(string key, string expected)
    {
        string? value = String(key, required: true);
        if (value is not null && !string.Equals(value, expected, StringComparison.Ordinal))
        {
            Error(key, $"must be '{expected}'; got '{value}'");
        }
    }

    public long? Number(string key, bool required, long min = 0, long max = CanonicalJsonWriter.MaxNumber)
    {
        CjValue? value = Value(key, required);
        if (value is null)
        {
            return null;
        }

        if (value is not CjNumber n)
        {
            Error(key, "must be a number");
            return null;
        }

        if (n.Value < min || n.Value > max)
        {
            Error(key, $"must be from {min} to {max}; got {n.Value}");
            return null;
        }

        return n.Value;
    }

    public bool? Bool(string key, bool required)
    {
        CjValue? value = Value(key, required);
        if (value is null)
        {
            return null;
        }

        if (value is not CjBool b)
        {
            Error(key, "must be true or false");
            return null;
        }

        return b.Value;
    }

    public DateOnly? Date(string key, bool required)
    {
        string? value = String(key, required);
        if (value is null)
        {
            return null;
        }

        if (!CorpusGrammar.TryParseDate(value, out DateOnly date))
        {
            Error(key, $"'{value}' is not a valid date (YYYY-MM-DD, a real calendar date)");
            return null;
        }

        return date;
    }

    public ContentDigest? Digest(string key, bool required)
    {
        string? value = String(key, required);
        if (value is null)
        {
            return null;
        }

        if (!ContentDigest.TryParse(value, out ContentDigest? digest))
        {
            Error(key, $"'{value}' is not a valid digest ('sha256:' and 64 lowercase hexadecimal digits)");
            return null;
        }

        return digest;
    }

    public List<CjValue>? Array(string key, bool required, bool allowEmpty)
    {
        CjValue? value = Value(key, required);
        if (value is null)
        {
            return null;
        }

        if (value is not CjArray a)
        {
            Error(key, "must be an array");
            return null;
        }

        if (!allowEmpty && a.Items.Count == 0)
        {
            Error(key, "must not be empty");
            return null;
        }

        return a.Items;
    }

    public JsonMembers? Object(string key, bool required)
    {
        CjValue? value = Value(key, required);
        return value is null ? null : new JsonMembers(value, PathOf(key), errors);
    }

    /// <summary>Reads an array of strings, each checked against the grammar when one is given.</summary>
    public List<string>? Strings(string key, bool required, bool allowEmptyArray, Func<string, bool>? grammar = null, string? grammarDescription = null)
    {
        List<CjValue>? items = Array(key, required, allowEmptyArray);
        if (items is null)
        {
            return null;
        }

        var result = new List<string>(items.Count);
        bool ok = true;
        for (int i = 0; i < items.Count; i++)
        {
            string path = $"{PathOf(key)}[{i}]";
            if (items[i] is not CjString s)
            {
                errors.Add(new CorpusError(path, "must be a string"));
                ok = false;
            }
            else if (s.Value.Length == 0)
            {
                errors.Add(new CorpusError(path, "must not be empty"));
                ok = false;
            }
            else if (grammar is not null && !grammar(s.Value))
            {
                errors.Add(new CorpusError(path, $"'{s.Value}' is not a valid {grammarDescription}"));
                ok = false;
            }
            else
            {
                result.Add(s.Value);
            }
        }

        return ok ? result : null;
    }

    /// <summary>Reads an object of key (key grammar) to string; the values may be empty.</summary>
    public SortedDictionary<string, string>? StringMap(string key, bool required)
    {
        CjValue? value = Value(key, required);
        if (value is null)
        {
            return null;
        }

        if (value is not CjObject o)
        {
            Error(key, "must be an object");
            return null;
        }

        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        bool ok = true;
        foreach (KeyValuePair<string, CjValue> member in o.Members)
        {
            string path = $"{PathOf(key)}.{member.Key}";
            if (!CorpusGrammar.IsKey(member.Key))
            {
                errors.Add(new CorpusError(path, $"'{member.Key}' is not a valid key ([a-z][a-zA-Z0-9]*, at most 64 characters)"));
                ok = false;
            }
            else if (member.Value is not CjString s)
            {
                errors.Add(new CorpusError(path, "must be a string"));
                ok = false;
            }
            else
            {
                result.Add(member.Key, s.Value);
            }
        }

        return ok ? result : null;
    }

    /// <summary>Refuses a member that this context forbids.</summary>
    public void Absent(string key, string reason)
    {
        consumed.Add(key);
        if (obj.Members.ContainsKey(key))
        {
            Error(key, reason);
        }
    }

    /// <summary>Reports every member that was not read as unknown.</summary>
    public void Finish()
    {
        foreach (string key in obj.Members.Keys)
        {
            if (!consumed.Contains(key))
            {
                Error(key, "is not a known member");
            }
        }
    }
}
