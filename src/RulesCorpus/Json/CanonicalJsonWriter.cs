namespace RulesCorpus.Json;

/// <summary>
/// Produces the canonical byte forms of docs/corpus-format.md, Canonical JSON: the compact form
/// every digest is taken over, and the indented form <c>corpus.json</c> is written in. The only
/// thing in the library that formats JSON (decision 0005).
/// </summary>
internal static class CanonicalJsonWriter
{
    /// <summary>2^53 − 1: the largest integer every JSON consumer represents exactly.</summary>
    public const long MaxNumber = (1L << 53) - 1;

    /// <summary>The compact form: no insignificant whitespace.</summary>
    /// <exception cref="ArgumentException">A string holds a lone surrogate or a number is out of range.</exception>
    public static byte[] ToCompact(CjValue value)
    {
        using var output = new MemoryStream();
        Write(output, value, depth: 0, indented: false);
        return output.ToArray();
    }

    /// <summary>
    /// The on-disk form: two-space indentation, <c>": "</c> after keys, one member or element
    /// per line, <c>{}</c> and <c>[]</c> for empty containers, and a final newline.
    /// </summary>
    /// <exception cref="ArgumentException">A string holds a lone surrogate or a number is out of range.</exception>
    public static byte[] ToIndented(CjValue value)
    {
        using var output = new MemoryStream();
        Write(output, value, depth: 0, indented: true);
        output.WriteByte((byte)'\n');
        return output.ToArray();
    }

    private static void Write(MemoryStream output, CjValue value, int depth, bool indented)
    {
        switch (value)
        {
            case CjObject obj:
                WriteContainer(output, '{', '}', obj.Members, depth, indented, (member, d) =>
                {
                    WriteString(output, member.Key);
                    output.WriteByte((byte)':');
                    if (indented)
                    {
                        output.WriteByte((byte)' ');
                    }

                    Write(output, member.Value, d, indented);
                });
                break;
            case CjArray array:
                WriteContainer(output, '[', ']', array.Items, depth, indented, (item, d) => Write(output, item, d, indented));
                break;
            case CjString str:
                WriteString(output, str.Value);
                break;
            case CjNumber number:
                WriteNumber(output, number.Value);
                break;
            case CjBool boolean:
                output.Write(boolean.Value ? "true"u8 : "false"u8);
                break;
            default:
                throw new ArgumentException($"Unsupported value type {value.GetType().Name}.", nameof(value));
        }
    }

    private static void WriteContainer<T>(
        MemoryStream output, char open, char close, IEnumerable<T> items, int depth, bool indented, Action<T, int> writeItem)
    {
        output.WriteByte((byte)open);
        bool any = false;
        foreach (T item in items)
        {
            if (any)
            {
                output.WriteByte((byte)',');
            }

            any = true;
            if (indented)
            {
                NewLine(output, depth + 1);
            }

            writeItem(item, depth + 1);
        }

        if (any && indented)
        {
            NewLine(output, depth);
        }

        output.WriteByte((byte)close);
    }

    private static void NewLine(MemoryStream output, int depth)
    {
        output.WriteByte((byte)'\n');
        for (int i = 0; i < depth * 2; i++)
        {
            output.WriteByte((byte)' ');
        }
    }

    private static void WriteNumber(MemoryStream output, long value)
    {
        if (value < 0 || value > MaxNumber)
        {
            throw new ArgumentException($"Canonical JSON numbers are integers from 0 to 2^53 - 1; got {value}.", nameof(value));
        }

        Span<byte> digits = stackalloc byte[20];
        int n = 0;
        do
        {
            digits[n++] = (byte)('0' + (int)(value % 10));
            value /= 10;
        }
        while (value > 0);

        for (int i = n - 1; i >= 0; i--)
        {
            output.WriteByte(digits[i]);
        }
    }

    private static void WriteString(MemoryStream output, string value)
    {
        const string hex = "0123456789abcdef";
        output.WriteByte((byte)'"');
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            switch (c)
            {
                case '"':
                    output.Write("\\\""u8);
                    continue;
                case '\\':
                    output.Write("\\\\"u8);
                    continue;
                case '\b':
                    output.Write("\\b"u8);
                    continue;
                case '\t':
                    output.Write("\\t"u8);
                    continue;
                case '\n':
                    output.Write("\\n"u8);
                    continue;
                case '\f':
                    output.Write("\\f"u8);
                    continue;
                case '\r':
                    output.Write("\\r"u8);
                    continue;
            }

            if (c < 0x20)
            {
                output.Write("\\u00"u8);
                output.WriteByte((byte)hex[c >> 4]);
                output.WriteByte((byte)hex[c & 0xF]);
            }
            else if (c < 0x80)
            {
                output.WriteByte((byte)c);
            }
            else if (c < 0x800)
            {
                output.WriteByte((byte)(0xC0 | (c >> 6)));
                output.WriteByte((byte)(0x80 | (c & 0x3F)));
            }
            else if (char.IsHighSurrogate(c))
            {
                if (i + 1 >= value.Length || !char.IsLowSurrogate(value[i + 1]))
                {
                    throw new ArgumentException($"A lone surrogate at index {i} cannot be written as UTF-8.", nameof(value));
                }

                int codePoint = char.ConvertToUtf32(c, value[++i]);
                output.WriteByte((byte)(0xF0 | (codePoint >> 18)));
                output.WriteByte((byte)(0x80 | ((codePoint >> 12) & 0x3F)));
                output.WriteByte((byte)(0x80 | ((codePoint >> 6) & 0x3F)));
                output.WriteByte((byte)(0x80 | (codePoint & 0x3F)));
            }
            else if (char.IsLowSurrogate(c))
            {
                throw new ArgumentException($"A lone surrogate at index {i} cannot be written as UTF-8.", nameof(value));
            }
            else
            {
                output.WriteByte((byte)(0xE0 | (c >> 12)));
                output.WriteByte((byte)(0x80 | ((c >> 6) & 0x3F)));
                output.WriteByte((byte)(0x80 | (c & 0x3F)));
            }
        }

        output.WriteByte((byte)'"');
    }

    /// <summary>True when the string holds an unpaired surrogate, which has no UTF-8 form.</summary>
    public static bool HasLoneSurrogate(string value)
    {
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (char.IsHighSurrogate(c))
            {
                if (i + 1 >= value.Length || !char.IsLowSurrogate(value[i + 1]))
                {
                    return true;
                }

                i++;
            }
            else if (char.IsLowSurrogate(c))
            {
                return true;
            }
        }

        return false;
    }
}
