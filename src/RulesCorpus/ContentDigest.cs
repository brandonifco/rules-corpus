using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;

namespace RulesCorpus;

/// <summary>
/// A SHA-256 digest in the written form <c>sha256:</c> followed by 64 lowercase hexadecimal
/// digits (docs/corpus-format.md, Digests).
///
/// <para>
/// A sealed class rather than a struct: a <c>default</c> struct would be a digest of nothing
/// that no parser produced, and every place that accepted one would have to remember to reject
/// it. An instance of this type is always a well-formed digest.
/// </para>
///
/// <para>
/// Parsing is strict. Uppercase hex is refused rather than normalized, because a digest is
/// compared as written: a manifest that writes the same digest two ways would have two
/// canonical forms and two manifest digests.
/// </para>
/// </summary>
public sealed class ContentDigest : IEquatable<ContentDigest>
{
    private const string Prefix = "sha256:";
    private const int HexLength = 64;

    private ContentDigest(string hex)
    {
        Hex = hex;
    }

    /// <summary>
    /// The 64 lowercase hexadecimal digits without the algorithm prefix. This is the form
    /// rules-kernel's <c>SourceBaselineId.ContentHash</c> takes, so a consumer projects a
    /// baseline by copying it.
    /// </summary>
    public string Hex { get; }

    /// <summary>Digests exactly these bytes.</summary>
    public static ContentDigest Compute(ReadOnlySpan<byte> bytes)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(bytes, hash);
        return new ContentDigest(ToLowerHex(hash));
    }

    /// <summary>Parses the written form.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is null.</exception>
    /// <exception cref="FormatException">
    /// <paramref name="value"/> is not <c>sha256:</c> followed by exactly 64 lowercase
    /// hexadecimal digits.
    /// </exception>
    public static ContentDigest Parse(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!TryParse(value, out ContentDigest? digest))
        {
            throw new FormatException(
                $"A digest is 'sha256:' followed by 64 lowercase hexadecimal digits; got '{value}'.");
        }

        return digest;
    }

    /// <summary>Parses the written form, returning false rather than throwing.</summary>
    public static bool TryParse([NotNullWhen(true)] string? value, [NotNullWhen(true)] out ContentDigest? digest)
    {
        digest = null;
        if (value is null
            || value.Length != Prefix.Length + HexLength
            || !value.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        for (int i = Prefix.Length; i < value.Length; i++)
        {
            char c = value[i];
            if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')))
            {
                return false;
            }
        }

        digest = new ContentDigest(value[Prefix.Length..]);
        return true;
    }

    /// <summary>The written form, <c>sha256:</c> and the hex digits.</summary>
    public override string ToString() => Prefix + Hex;

    /// <inheritdoc />
    public bool Equals(ContentDigest? other) => other is not null && string.Equals(Hex, other.Hex, StringComparison.Ordinal);

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as ContentDigest);

    /// <inheritdoc />
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Hex);

    /// <summary>Value equality: two digests are equal when their bytes are.</summary>
    public static bool operator ==(ContentDigest? left, ContentDigest? right) =>
        left is null ? right is null : left.Equals(right);

    /// <summary>Value inequality.</summary>
    public static bool operator !=(ContentDigest? left, ContentDigest? right) => !(left == right);

    private static string ToLowerHex(ReadOnlySpan<byte> bytes)
    {
        const string digits = "0123456789abcdef";
        Span<char> chars = stackalloc char[bytes.Length * 2];
        for (int i = 0; i < bytes.Length; i++)
        {
            chars[2 * i] = digits[bytes[i] >> 4];
            chars[(2 * i) + 1] = digits[bytes[i] & 0xF];
        }

        return new string(chars);
    }
}
