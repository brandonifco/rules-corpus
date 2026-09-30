using System.Buffers;
using System.Globalization;
using System.Text.Unicode;

namespace RulesCorpus.Adapters.Text;

/// <summary>
/// The canonical bytes, and enough of the path from the input to map any canonical offset back
/// to the input byte it came from. Source spans cite the input artifact, not the canonical one,
/// so they have to survive the byte-order mark and CRLF being removed.
/// </summary>
internal sealed class CanonicalText
{
    public const string BomLoss = "byte-order mark removed";
    public const string NewlineLoss = "line endings normalized to LF";

    private static ReadOnlySpan<byte> Bom => [0xEF, 0xBB, 0xBF];

    private readonly int _bomLength;

    // Canonical offsets, ascending, of each LF that stands for a CRLF pair in the input. Each
    // one is the single place where the input runs a byte ahead of the canonical text. A lone
    // CR becomes an LF of the same width, so it needs no entry.
    private readonly int[] _collapsedPairs;

    private CanonicalText(byte[] bytes, int bomLength, int[] collapsedPairs, List<string> losses)
    {
        Bytes = bytes;
        _bomLength = bomLength;
        _collapsedPairs = collapsedPairs;
        Losses = losses;
    }

    public byte[] Bytes { get; }

    public List<string> Losses { get; }

    public static CanonicalText From(ReadOnlySpan<byte> input, TextOptions options)
    {
        RequireUtf8(input);

        var losses = new List<string>();
        int bomLength = 0;
        if (input.StartsWith(Bom))
        {
            if (!options.StripBom)
            {
                throw Refusal.Of(
                    "Input starts with a byte-order mark (EF BB BF at byte offset 0); set bom to 'strip' "
                    + "to remove it and record the loss.");
            }

            bomLength = Bom.Length;
            losses.Add(BomLoss);
        }

        ReadOnlySpan<byte> text = input[bomLength..];
        int firstCr = text.IndexOf((byte)'\r');
        if (firstCr < 0)
        {
            return new CanonicalText(text.ToArray(), bomLength, [], losses);
        }

        if (options.PreserveNewlines)
        {
            int offset = bomLength + firstCr;
            throw Refusal.Of(
                $"Input contains CR at byte offset {Format(offset)} (line {Format(LineAt(input, offset))}), "
                + "and newlines is 'preserve'.");
        }

        var output = new byte[text.Length];
        var collapsed = new List<int>();
        text[..firstCr].CopyTo(output);
        int written = firstCr;
        for (int i = firstCr; i < text.Length; i++)
        {
            byte b = text[i];
            if (b != (byte)'\r')
            {
                output[written++] = b;
                continue;
            }

            if (i + 1 < text.Length && text[i + 1] == (byte)'\n')
            {
                collapsed.Add(written);
                i++;
            }

            output[written++] = (byte)'\n';
        }

        losses.Add(NewlineLoss);
        return new CanonicalText(output.AsSpan(0, written).ToArray(), bomLength, [.. collapsed], losses);
    }

    /// <summary>
    /// The input offset where the canonical byte at <paramref name="canonicalOffset"/> began, or,
    /// for an offset one past a canonical byte, the input offset one past where that byte ended.
    /// </summary>
    public long ToInputOffset(int canonicalOffset)
    {
        int index = Array.BinarySearch(_collapsedPairs, canonicalOffset);
        int pairsBefore = index >= 0 ? index : ~index;
        return (long)canonicalOffset + _bomLength + pairsBefore;
    }

    internal static string Format(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static int LineAt(ReadOnlySpan<byte> bytes, int offset) => bytes[..offset].Count((byte)'\n') + 1;

    private static void RequireUtf8(ReadOnlySpan<byte> input)
    {
        // Utf8.ToUtf16 rather than a throwing Encoding: it reports exactly how far the valid
        // prefix runs, so the refusal can name the offending byte.
        char[] buffer = ArrayPool<char>.Shared.Rent(4096);
        try
        {
            int consumed = 0;
            while (consumed < input.Length)
            {
                OperationStatus status = Utf8.ToUtf16(
                    input[consumed..], buffer, out int read, out _, replaceInvalidSequences: false, isFinalBlock: true);
                consumed += read;
                if (status == OperationStatus.InvalidData)
                {
                    throw Refusal.Of(
                        $"Input is not valid UTF-8 at byte offset {Format(consumed)} "
                        + $"(line {Format(LineAt(input, consumed))}, byte 0x{input[consumed]:X2}).");
                }
            }
        }
        finally
        {
            ArrayPool<char>.Shared.Return(buffer);
        }
    }
}
