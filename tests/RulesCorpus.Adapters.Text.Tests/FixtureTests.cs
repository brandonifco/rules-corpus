using System.Text;
using System.Text.RegularExpressions;
using RulesCorpus.Adapters;
using RulesCorpus.Adapters.Text;
using static RulesCorpus.Adapters.Text.Tests.Harness;

namespace RulesCorpus.Adapters.Text.Tests;

public sealed class FixtureTests
{
    private static readonly string[] RegulatoryIds = ["107.1", "107.2", "107.9", "107.11", "107.12"];

    private static readonly string[] RegulatoryHeadings =
    [
        "§ 107.1 Applicability.",
        "§ 107.2 Applicability of certification procedures for products and articles.",
        "§ 107.9 Safety event reporting.",
        "§ 107.11 Applicability.",
        "§ 107.12 Requirement for a remote pilot certificate with a small UAS rating.",
    ];

    [Fact]
    public void Adapter_identity_is_text_version_1_and_deterministic()
    {
        var adapter = new TextAdapter();
        Assert.Equal("text", adapter.Id);
        Assert.Equal("1", adapter.Version);
        Assert.True(adapter.IsDeterministic);
    }

    [Fact]
    public void Regulatory_headings_give_exact_ids_locators_and_heading_led_text()
    {
        byte[] input = Fixture("regulatory.txt");
        var output = Derive(input, ("segmentation", "headings"), ("headingPattern", HeadingPattern));

        Assert.Equal("text/plain", output.MediaType);
        Assert.Equal(DerivationFidelity.Lossless, output.Fidelity);
        Assert.Empty(output.Losses);
        Assert.Equal(input, output.Canonical.ToArray());

        Assert.Equal(RegulatoryIds, output.Segments.Select(s => s.Id));
        Assert.Equal(RegulatoryHeadings, output.Segments.Select(s => s.Locator));
        for (int i = 0; i < output.Segments.Count; i++)
        {
            var segment = output.Segments[i];
            string text = SegmentText(output, segment);
            Assert.StartsWith(RegulatoryHeadings[i] + "\n", text, StringComparison.Ordinal);
            Assert.False(text.EndsWith('\n'), segment.Id);
            Assert.Null(Pages(segment));

            // No BOM and no CR: the input span is the canonical span.
            Assert.Equal(new ByteRange(segment.Start, segment.Length), InputBytes(segment));
        }

        // The part heading before the first section is not segmented.
        Assert.Equal(Encoding.UTF8.GetByteCount("PART 107—SMALL UNMANNED AIRCRAFT SYSTEMS\n\n"), output.Segments[0].Start);

        // Each section runs to its last line, and the last section to the end of the text.
        Assert.EndsWith("[Amdt. 107-8, 86 FR 4381, Jan. 15, 2021]", SegmentText(output, output.Segments[0]), StringComparison.Ordinal);
        Assert.Equal(input.Length - 1, output.Segments[^1].Start + output.Segments[^1].Length);
        AssertSpansRoundTrip(input, output);
    }

    [Fact]
    public void Regulatory_locator_group_overrides_the_heading_line()
    {
        byte[] input = Fixture("regulatory.txt");
        var output = Derive(
            input, ("segmentation", "headings"), ("headingPattern", @"^(?<locator>§ (?<id>107\.\d+))"));

        Assert.Equal(RegulatoryIds, output.Segments.Select(s => s.Id));
        Assert.Equal(RegulatoryIds.Select(id => "§ " + id), output.Segments.Select(s => s.Locator));
    }

    [Fact]
    public void Rulebook_blocks_are_numbered_per_page_with_page_locators_and_spans()
    {
        byte[] input = Fixture("rulebook.txt");
        var output = Derive(input, ("segmentation", "blocks"), ("pageMarker", BracePageMarker));

        Assert.Equal(DerivationFidelity.Lossless, output.Fidelity);
        Assert.Empty(output.Losses);

        // Counted independently of the adapter: one block before the first marker, then
        // 13, 92 and 5 blocks on pages 191, 192 and 193.
        string[] expected =
        [
            "b1",
            .. Enumerable.Range(1, 13).Select(k => $"p191.b{k}"),
            .. Enumerable.Range(1, 92).Select(k => $"p192.b{k}"),
            .. Enumerable.Range(1, 5).Select(k => $"p193.b{k}"),
        ];
        Assert.Equal(expected, output.Segments.Select(s => s.Id));

        var first = output.Segments[0];
        Assert.Null(first.Locator);
        Assert.Null(Pages(first));
        Assert.StartsWith("Shove. The target", SegmentText(output, first), StringComparison.Ordinal);
        Assert.EndsWith("one size larger than you.", SegmentText(output, first), StringComparison.Ordinal);

        var opening = output.Segments[1];
        Assert.Equal("p191.b1", opening.Id);
        Assert.Equal("p. 191", opening.Locator);
        Assert.Equal(new PageRange(191, 191), Pages(opening));
        Assert.StartsWith("Unconscious [Condition]\n", SegmentText(output, opening), StringComparison.Ordinal);
        Assert.EndsWith("Unaware. You’re unaware of your surroundings.", SegmentText(output, opening), StringComparison.Ordinal);

        var pageFooter = output.Segments.Single(s => s.Id == "p191.b13");
        Assert.Equal("System Reference Document 5.2.1", SegmentText(output, pageFooter));

        var lastPage = output.Segments.Single(s => s.Id == "p193.b1");
        Assert.Equal("p. 193", lastPage.Locator);
        Assert.StartsWith("Intelligence or Wisdom.", SegmentText(output, lastPage), StringComparison.Ordinal);

        var marker = new Regex(BracePageMarker);
        foreach (var segment in output.Segments.Skip(1))
        {
            string page = segment.Id[1..segment.Id.IndexOf('.', StringComparison.Ordinal)];
            Assert.Equal("p. " + page, segment.Locator);
            Assert.Equal(new PageRange(int.Parse(page), int.Parse(page)), Pages(segment));
        }

        foreach (var segment in output.Segments)
        {
            string text = SegmentText(output, segment);
            Assert.DoesNotContain(text.Split('\n'), line => marker.IsMatch(line) || line.Trim(' ', '\t').Length == 0);
            Assert.False(text.EndsWith('\n'), segment.Id);
        }

        AssertSpansRoundTrip(input, output);
    }

    [Fact]
    public void Rulebook_whole_is_one_segment_over_everything()
    {
        byte[] input = Fixture("rulebook.txt");
        var output = Derive(input, ("pageMarker", BracePageMarker));

        var all = Assert.Single(output.Segments);
        Assert.Equal("all", all.Id);
        Assert.Equal(0, all.Start);
        Assert.Equal(input.Length, all.Length);
        Assert.Null(all.Locator);

        // The first byte precedes every marker, so the whole text is not on a page.
        Assert.Null(Pages(all));
        Assert.Equal(new ByteRange(0, input.Length), InputBytes(all));
    }

    [Fact]
    public void Rulebook_does_not_run_from_page_1_so_pagesContiguous_refuses_it()
    {
        var e = Refused(
            Fixture("rulebook.txt"),
            ("segmentation", "blocks"), ("pageMarker", BracePageMarker), ("pagesContiguous", "true"));
        Assert.Contains("names page 191", e.Message, StringComparison.Ordinal);
        Assert.Contains("requires page 1", e.Message, StringComparison.Ordinal);
    }

    public static TheoryData<string, string[]> Configurations() => new()
    {
        { "regulatory.txt", ["segmentation=headings", "headingPattern=" + HeadingPattern] },
        { "regulatory.txt", ["segmentation=blocks"] },
        { "regulatory.txt", [] },
        { "rulebook.txt", ["segmentation=blocks", "pageMarker=" + BracePageMarker] },
        { "rulebook.txt", ["segmentation=blocks"] },
        { "rulebook.txt", ["pageMarker=" + BracePageMarker] },
        { "rulebook.txt", ["segmentation=headings", "headingPattern=^(?<id>\\d): Choose"] },
    };

    [Theory]
    [MemberData(nameof(Configurations))]
    public void Same_bytes_and_parameters_give_identical_output(string fixture, string[] parameters)
    {
        var pairs = parameters.Select(p => (p[..p.IndexOf('=', StringComparison.Ordinal)], p[(p.IndexOf('=', StringComparison.Ordinal) + 1)..])).ToArray();
        byte[] input = Fixture(fixture);

        string first = Describe(Derive(input, pairs));
        string second = Describe(Derive((byte[])input.Clone(), pairs));

        Assert.Equal(first, second);
    }

    [Theory]
    [MemberData(nameof(Configurations))]
    public void Crlf_and_bom_variant_maps_spans_to_the_original_bytes(string fixture, string[] parameters)
    {
        var pairs = parameters.Select(p => (p[..p.IndexOf('=', StringComparison.Ordinal)], p[(p.IndexOf('=', StringComparison.Ordinal) + 1)..])).ToList();
        byte[] lf = Fixture(fixture);
        byte[] variant = [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(lf).Replace("\n", "\r\n", StringComparison.Ordinal))];

        var plain = Derive(lf, [.. pairs]);
        pairs.Add(("bom", "strip"));
        var output = Derive(variant, [.. pairs]);

        Assert.Equal(DerivationFidelity.LossyTraceable, output.Fidelity);
        Assert.Equal(["byte-order mark removed", "line endings normalized to LF"], output.Losses);
        Assert.Equal(lf, output.Canonical.ToArray());
        Assert.Equal(
            plain.Segments.Select(s => (s.Id, s.Start, s.Length, s.Locator, Pages(s))),
            output.Segments.Select(s => (s.Id, s.Start, s.Length, s.Locator, Pages(s))));

        foreach (var segment in output.Segments)
        {
            // Three BOM bytes, plus one CR for every LF before the segment and within it.
            int before = lf.AsSpan(0, (int)segment.Start).Count((byte)'\n');
            int within = lf.AsSpan((int)segment.Start, (int)segment.Length).Count((byte)'\n');
            Assert.Equal(new ByteRange(3 + segment.Start + before, segment.Length + within), InputBytes(segment));
        }

        AssertSpansRoundTrip(variant, output);
    }
}
