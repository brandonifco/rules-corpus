using System.Globalization;

namespace RulesCorpus;

/// <summary>
/// The identifier grammars of docs/corpus-format.md, Identifiers. Every reader and the builder
/// check against these. Internal: no adapter uses them (the core validates what an adapter
/// returns), so exposing them would be a compatibility promise nothing has validated
/// (docs/decisions/0012).
///
/// <para>
/// Written as character loops rather than regular expressions so they are allocation-free,
/// linear, and read the same as the grammar table.
/// </para>
/// </summary>
internal static class CorpusGrammar
{
    private const int MaxIdLength = 128;
    private const int MaxSegmentIdLength = 256;
    private const int MaxKeyLength = 64;
    private const int MaxMediaTypeLength = 128;

    /// <summary>
    /// A corpus, artifact, derivation or source id: <c>[a-z0-9]+([-.][a-z0-9]+)*</c>, at most
    /// 128 characters. A source id doubles as rules-kernel's source id, so this is the stricter
    /// of the two grammars: anything valid here is valid there.
    /// </summary>
    internal static bool IsId(string? value)
    {
        if (value is null || value.Length == 0 || value.Length > MaxIdLength)
        {
            return false;
        }

        bool previousWasSeparator = true;
        foreach (char c in value)
        {
            if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9'))
            {
                previousWasSeparator = false;
            }
            else if (c is '-' or '.')
            {
                if (previousWasSeparator)
                {
                    return false;
                }

                previousWasSeparator = true;
            }
            else
            {
                return false;
            }
        }

        return !previousWasSeparator;
    }

    /// <summary>
    /// A hash derivation: the same grammar as <see cref="IsId"/>, which is rules-kernel's
    /// <c>SourceBaselineId.HashDerivation</c> grammar. Named separately because the two could
    /// diverge, and a caller should say which one it means.
    /// </summary>
    internal static bool IsHashDerivation(string? value) => IsId(value);

    /// <summary>
    /// A segment id: <c>[A-Za-z0-9]([A-Za-z0-9._()/-]*[A-Za-z0-9)])?</c>, at most 256
    /// characters. Looser than <see cref="IsId"/> because segment ids echo the source's own
    /// numbering (<c>107.51</c>, <c>1.401(k)-1</c>), which consumers cite verbatim.
    /// </summary>
    internal static bool IsSegmentId(string? value)
    {
        if (value is null || value.Length == 0 || value.Length > MaxSegmentIdLength)
        {
            return false;
        }

        if (!IsAsciiLetterOrDigit(value[0]))
        {
            return false;
        }

        for (int i = 1; i < value.Length - 1; i++)
        {
            char c = value[i];
            if (!(IsAsciiLetterOrDigit(c) || c is '.' or '_' or '(' or ')' or '/' or '-'))
            {
                return false;
            }
        }

        char last = value[^1];
        return value.Length == 1 || IsAsciiLetterOrDigit(last) || last == ')';
    }

    /// <summary>A parameter or metadata key: <c>[a-z][a-zA-Z0-9]*</c>, at most 64 characters.</summary>
    internal static bool IsKey(string? value)
    {
        if (value is null || value.Length == 0 || value.Length > MaxKeyLength)
        {
            return false;
        }

        if (value[0] is < 'a' or > 'z')
        {
            return false;
        }

        foreach (char c in value)
        {
            if (!IsAsciiLetterOrDigit(c))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// A date: <c>YYYY-MM-DD</c> naming a real calendar date. <c>2026-02-30</c> is refused:
    /// a date that does not exist cannot be the moment a source was pinned.
    /// </summary>
    internal static bool IsDate(string? value) => TryParseDate(value, out _);

    /// <summary>
    /// A media type: <c>type/subtype</c> in lowercase ASCII, at most 128 characters, with no
    /// parameters. Each part is an RFC 6838 restricted name: a letter or digit, then letters,
    /// digits and <c>!#$&amp;^_.+-</c>.
    /// </summary>
    internal static bool IsMediaType(string? value)
    {
        if (value is null || value.Length == 0 || value.Length > MaxMediaTypeLength)
        {
            return false;
        }

        int slash = value.IndexOf('/', StringComparison.Ordinal);
        if (slash < 0 || value.IndexOf('/', slash + 1) >= 0)
        {
            return false;
        }

        return IsRestrictedName(value.AsSpan(0, slash)) && IsRestrictedName(value.AsSpan(slash + 1));
    }

    internal static bool TryParseDate(string? value, out DateOnly date)
    {
        date = default;
        if (value is null || value.Length != 10 || value[4] != '-' || value[7] != '-')
        {
            return false;
        }

        for (int i = 0; i < value.Length; i++)
        {
            if (i is 4 or 7)
            {
                continue;
            }

            if (value[i] is < '0' or > '9')
            {
                return false;
            }
        }

        return DateOnly.TryParseExact(
            value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
    }

    internal static string FormatDate(DateOnly date) =>
        date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static bool IsRestrictedName(ReadOnlySpan<char> part)
    {
        if (part.Length == 0 || !IsLowerLetterOrDigit(part[0]))
        {
            return false;
        }

        foreach (char c in part)
        {
            if (!(IsLowerLetterOrDigit(c) || c is '!' or '#' or '$' or '&' or '^' or '_' or '.' or '+' or '-'))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsLowerLetterOrDigit(char c) => (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9');

    private static bool IsAsciiLetterOrDigit(char c) =>
        (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9');
}
