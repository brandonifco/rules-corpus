using System.Globalization;
using System.Text;
using RulesCorpus.Adapters;
using RulesCorpus.Adapters.Text;

namespace RulesCorpus.Adapters.Text.Tests;

internal static class Harness
{
    public const string HeadingPattern = @"^§ (?<id>107\.\d+)";
    public const string BracePageMarker = @"^\{(\d+)\}$";

    public static byte[] Fixture(string name) =>
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    public static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

    public static AdapterOutput Derive(byte[] bytes, params (string Key, string Value)[] parameters) =>
        Derive(bytes, CorpusLimits.Default, parameters);

    public static AdapterOutput Derive(byte[] bytes, CorpusLimits limits, params (string Key, string Value)[] parameters)
    {
        var map = parameters.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        return new TextAdapter().Derive(new AdapterInput("input", bytes, map, limits));
    }

    public static CorpusAdapterException Refused(byte[] bytes, params (string Key, string Value)[] parameters) =>
        Assert.Throws<CorpusAdapterException>(() => Derive(bytes, parameters));

    public static string SegmentText(AdapterOutput output, AdapterSegment segment) =>
        Encoding.UTF8.GetString(output.Canonical.Span.Slice(checked((int)segment.Start), checked((int)segment.Length)));

    public static ByteRange InputBytes(AdapterSegment segment) => Assert.Single(segment.Sources).Bytes!;

    public static PageRange? Pages(AdapterSegment segment) => Assert.Single(segment.Sources).Pages;

    /// <summary>The adapter's newline rule, applied independently to a slice of the input.</summary>
    public static byte[] NormalizeNewlines(ReadOnlySpan<byte> bytes)
    {
        var output = new List<byte>(bytes.Length);
        for (int i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] == (byte)'\r')
            {
                output.Add((byte)'\n');
                if (i + 1 < bytes.Length && bytes[i + 1] == (byte)'\n')
                {
                    i++;
                }
            }
            else
            {
                output.Add(bytes[i]);
            }
        }

        return [.. output];
    }

    /// <summary>
    /// Every segment's input byte range, normalized the same way, is exactly its canonical
    /// bytes: the span cites what the segment came from, no more and no less.
    /// </summary>
    public static void AssertSpansRoundTrip(byte[] input, AdapterOutput output)
    {
        Assert.NotEmpty(output.Segments);
        foreach (var segment in output.Segments)
        {
            Assert.True(segment.Length >= 1, segment.Id);
            var range = InputBytes(segment);
            Assert.True(range.Start >= 0 && range.Length >= 1 && range.Start + range.Length <= input.Length, segment.Id);
            byte[] cited = NormalizeNewlines(input.AsSpan((int)range.Start, (int)range.Length));
            byte[] canonical = output.Canonical.Span.Slice((int)segment.Start, (int)segment.Length).ToArray();
            Assert.Equal(canonical, cited);
        }
    }

    /// <summary>A complete, order-preserving rendering of an output, for byte-level comparison.</summary>
    public static string Describe(AdapterOutput output)
    {
        var builder = new StringBuilder();
        builder.Append(Convert.ToHexString(output.Canonical.Span)).Append('\n');
        builder.Append(output.MediaType).Append('|').Append(output.Fidelity).Append('|')
            .AppendJoin(';', output.Losses).Append('\n');
        foreach (var s in output.Segments)
        {
            builder.Append(CultureInfo.InvariantCulture, $"{s.Id}|{s.Start}|{s.Length}|{s.Locator ?? "<null>"}");
            foreach (var source in s.Sources)
            {
                builder.Append(CultureInfo.InvariantCulture, $"|{source.Pages?.From}-{source.Pages?.To}|{source.Bytes?.Start}+{source.Bytes?.Length}");
            }

            builder.Append('\n');
        }

        return builder.ToString();
    }
}
