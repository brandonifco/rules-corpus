using System.Text;
using System.Text.Json;

namespace RulesCorpus.Json;

/// <summary>
/// Reads JSON into the canonical value model, refusing everything the canonical form cannot
/// hold: a byte-order mark, duplicate keys, null, numbers that are not non-negative integers up
/// to 2^53 − 1, lone surrogates, invalid UTF-8, comments and trailing commas. Whitespace is
/// accepted; the form a file happens to be formatted in is not what is hashed.
/// </summary>
internal static class CanonicalJsonReader
{
    private const int MaxDepth = 64;

    /// <summary>Parses one JSON value, or returns null and appends the error.</summary>
    public static CjValue? Parse(ReadOnlySpan<byte> utf8, List<CorpusError> errors)
    {
        if (utf8.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
        {
            errors.Add(new CorpusError("$", "starts with a UTF-8 byte-order mark; canonical JSON has none"));
            return null;
        }

        var reader = new Utf8JsonReader(utf8, new JsonReaderOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = MaxDepth,
        });

        var path = new List<object>();
        try
        {
            if (!reader.Read())
            {
                errors.Add(new CorpusError("$", "is empty; expected a JSON value"));
                return null;
            }

            CjValue value = ReadValue(ref reader, path);
            if (reader.Read())
            {
                throw new FormatException("unexpected content after the JSON value");
            }

            return value;
        }
        catch (JsonException e)
        {
            errors.Add(new CorpusError(FormatPath(path), "is not valid JSON: " + e.Message));
        }
        catch (FormatException e)
        {
            errors.Add(new CorpusError(FormatPath(path), e.Message));
        }

        return null;
    }

    /// <summary>Renders a path stack as <c>$.a[0].b</c>.</summary>
    public static string FormatPath(IEnumerable<object> path)
    {
        var builder = new StringBuilder("$");
        foreach (object part in path)
        {
            if (part is int index)
            {
                builder.Append('[').Append(index).Append(']');
            }
            else
            {
                builder.Append('.').Append((string)part);
            }
        }

        return builder.ToString();
    }

    private static CjValue ReadValue(ref Utf8JsonReader reader, List<object> path)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.StartObject:
                return ReadObject(ref reader, path);
            case JsonTokenType.StartArray:
                return ReadArray(ref reader, path);
            case JsonTokenType.String:
                return new CjString(ReadString(ref reader));
            case JsonTokenType.Number:
                return new CjNumber(ReadNumber(reader.ValueSpan));
            case JsonTokenType.True:
                return CjBool.True;
            case JsonTokenType.False:
                return CjBool.False;
            case JsonTokenType.Null:
                throw new FormatException("is null; an absent optional member is omitted, never null");
            default:
                throw new FormatException($"unexpected JSON token {reader.TokenType}");
        }
    }

    private static CjObject ReadObject(ref Utf8JsonReader reader, List<object> path)
    {
        var obj = new CjObject();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            string key = ReadString(ref reader);
            path.Add(key);
            if (obj.Members.ContainsKey(key))
            {
                throw new FormatException("is a duplicate key");
            }

            reader.Read();
            obj.Members.Add(key, ReadValue(ref reader, path));
            path.RemoveAt(path.Count - 1);
        }

        return obj;
    }

    private static CjArray ReadArray(ref Utf8JsonReader reader, List<object> path)
    {
        var array = new CjArray();
        int index = 0;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            path.Add(index++);
            array.Items.Add(ReadValue(ref reader, path));
            path.RemoveAt(path.Count - 1);
        }

        return array;
    }

    private static string ReadString(ref Utf8JsonReader reader)
    {
        string value;
        try
        {
            value = reader.GetString()!;
        }
        catch (InvalidOperationException e)
        {
            // Invalid UTF-8 and unpaired escaped surrogates both land here.
            throw new FormatException("is not a valid string: " + e.Message, e);
        }

        if (CanonicalJsonWriter.HasLoneSurrogate(value))
        {
            throw new FormatException("holds a lone surrogate, which has no UTF-8 form");
        }

        return value;
    }

    private static long ReadNumber(ReadOnlySpan<byte> raw)
    {
        const string rule = "canonical JSON numbers are integers from 0 to 2^53 - 1 with no sign, leading zero, fraction or exponent";
        if (raw.Length == 0 || raw.Length > 16 || (raw[0] == (byte)'0' && raw.Length > 1))
        {
            throw new FormatException($"is not allowed: {rule}");
        }

        long value = 0;
        foreach (byte b in raw)
        {
            if (b is < (byte)'0' or > (byte)'9')
            {
                throw new FormatException($"is not allowed: {rule}");
            }

            value = (value * 10) + (b - '0');
        }

        if (value > CanonicalJsonWriter.MaxNumber)
        {
            throw new FormatException($"is too large: {rule}");
        }

        return value;
    }
}
