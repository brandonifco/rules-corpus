using RulesCorpus.Adapters;
using static RulesCorpus.Adapters.Text.Tests.Harness;

namespace RulesCorpus.Adapters.Text.Tests;

public sealed class BehaviourTests
{
    [Fact]
    public void Lf_input_with_default_parameters_is_lossless_and_unchanged()
    {
        byte[] input = Utf8("alpha\n\nbeta\n");
        var output = Derive(input);

        Assert.Equal(DerivationFidelity.Lossless, output.Fidelity);
        Assert.Empty(output.Losses);
        Assert.Equal(input, output.Canonical.ToArray());
        var all = Assert.Single(output.Segments);
        Assert.Equal(("all", 0L, (long)input.Length), (all.Id, all.Start, all.Length));
    }

    [Fact]
    public void Stripped_bom_alone_is_the_only_loss_and_shifts_spans_by_three()
    {
        byte[] input = [0xEF, 0xBB, 0xBF, .. Utf8("one\n\ntwo")];
        var output = Derive(input, ("bom", "strip"), ("segmentation", "blocks"));

        Assert.Equal(DerivationFidelity.LossyTraceable, output.Fidelity);
        Assert.Equal(["byte-order mark removed"], output.Losses);
        Assert.Equal(Utf8("one\n\ntwo"), output.Canonical.ToArray());
        Assert.Equal([new ByteRange(3, 3), new ByteRange(8, 3)], output.Segments.Select(InputBytes));
        AssertSpansRoundTrip(input, output);
    }

    [Fact]
    public void Bom_strip_on_input_without_a_bom_records_nothing()
    {
        var output = Derive(Utf8("x"), ("bom", "strip"));
        Assert.Equal(DerivationFidelity.Lossless, output.Fidelity);
        Assert.Empty(output.Losses);
    }

    [Fact]
    public void Lone_cr_becomes_lf_of_the_same_width()
    {
        byte[] input = Utf8("a\rb\r\rc");
        var output = Derive(input, ("segmentation", "blocks"));

        Assert.Equal(Utf8("a\nb\n\nc"), output.Canonical.ToArray());
        Assert.Equal(["line endings normalized to LF"], output.Losses);
        Assert.Equal(["b1", "b2"], output.Segments.Select(s => s.Id));
        Assert.Equal([new ByteRange(0, 3), new ByteRange(5, 1)], output.Segments.Select(InputBytes));
        AssertSpansRoundTrip(input, output);
    }

    [Fact]
    public void Mixed_line_endings_map_back_exactly()
    {
        byte[] input = Utf8("é\r\n\r\nñ\rx\n\r\n  \t\r\nz\r\n");
        var output = Derive(input, ("segmentation", "blocks"));

        Assert.Equal(Utf8("é\n\nñ\nx\n\n  \t\nz\n"), output.Canonical.ToArray());
        Assert.Equal(["b1", "b2", "b3"], output.Segments.Select(s => s.Id));
        Assert.Equal(["é", "ñ\nx", "z"], output.Segments.Select(s => SegmentText(output, s)));
        AssertSpansRoundTrip(input, output);
    }

    [Fact]
    public void Blocks_keep_leading_and_trailing_spaces_of_their_lines()
    {
        var output = Derive(Utf8("  a  \n b\n \t \nc"), ("segmentation", "blocks"));
        Assert.Equal(["  a  \n b", "c"], output.Segments.Select(s => SegmentText(output, s)));
    }

    [Fact]
    public void Headings_exclude_trailing_newlines_but_not_trailing_blank_text()
    {
        var output = Derive(
            Utf8("intro\n# a\nbody\n\n\n# b\n  \n"),
            ("segmentation", "headings"), ("headingPattern", "^# (?<id>[a-z])$"));

        Assert.Equal(["a", "b"], output.Segments.Select(s => s.Id));
        Assert.Equal(["# a\nbody", "# b\n  "], output.Segments.Select(s => SegmentText(output, s)));
        Assert.Equal(["# a", "# b"], output.Segments.Select(s => s.Locator));
    }

    [Fact]
    public void Inline_markers_change_the_page_mid_line_and_do_not_break_blocks()
    {
        byte[] input = Utf8("{1}\nalpha {2} beta\n\ngamma\n");
        var output = Derive(input, ("segmentation", "blocks"), ("pageMarker", @"\{(\d+)\}"));

        Assert.Equal(["p1.b1", "p2.b1"], output.Segments.Select(s => s.Id));
        Assert.Equal(["p. 1", "p. 2"], output.Segments.Select(s => s.Locator));
        Assert.Equal([new PageRange(1, 2), new PageRange(2, 2)], output.Segments.Select(Pages));
        Assert.Equal("alpha {2} beta", SegmentText(output, output.Segments[0]));
    }

    [Fact]
    public void Inline_marker_position_is_a_byte_offset_after_multibyte_text()
    {
        // "ééé" is 6 bytes but 3 chars; a char offset would put page 2 inside the first block.
        var output = Derive(
            Utf8("{1}\nx\n\nééé {2}\n"),
            ("segmentation", "blocks"), ("pageMarker", @"\{(\d+)\}"));

        Assert.Equal([new PageRange(1, 1), new PageRange(1, 2)], output.Segments.Select(Pages));
    }

    [Fact]
    public void Block_numbering_restarts_on_each_page_and_marker_lines_split_blocks()
    {
        var output = Derive(
            Utf8("a\n{1}\nb\n\nc\n{2}\nd"),
            ("segmentation", "blocks"), ("pageMarker", @"^\{(\d+)\}$"), ("pagesContiguous", "true"));

        Assert.Equal(["b1", "p1.b1", "p1.b2", "p2.b1"], output.Segments.Select(s => s.Id));
        Assert.Equal(["a", "b", "c", "d"], output.Segments.Select(s => SegmentText(output, s)));
        Assert.Equal([null, "p. 1", "p. 1", "p. 2"], output.Segments.Select(s => s.Locator));
    }

    [Fact]
    public void Whole_text_starting_on_a_marker_spans_its_pages()
    {
        var output = Derive(Utf8("{1}\na\n{2}\nb"), ("pageMarker", @"^\{(\d+)\}$"));
        Assert.Equal(new PageRange(1, 2), Pages(Assert.Single(output.Segments)));
    }

    [Fact]
    public void Heading_segments_carry_pages_from_first_to_last_byte()
    {
        var output = Derive(
            Utf8("{1}\n# a\nx\n{2}\ny\n# b\nz"),
            ("segmentation", "headings"), ("headingPattern", "^# (?<id>[a-z])$"), ("pageMarker", @"^\{(\d+)\}$"));

        Assert.Equal([new PageRange(1, 2), new PageRange(2, 2)], output.Segments.Select(Pages));
    }

    [Fact]
    public void Segment_ids_accept_the_full_grammar()
    {
        var output = Derive(
            Utf8("H 1\nH a(b)\nH A._()/-z\nH " + new string('x', 256)),
            ("segmentation", "headings"), ("headingPattern", "^H (?<id>.+)$"));

        Assert.Equal(["1", "a(b)", "A._()/-z", new string('x', 256)], output.Segments.Select(s => s.Id));
    }

    [Fact]
    public void Segment_limit_equal_to_the_count_is_allowed()
    {
        var output = Derive(Utf8("a\n\nb\n\nc"), CorpusLimits.Default with { MaxSegments = 3 }, ("segmentation", "blocks"));
        Assert.Equal(3, output.Segments.Count);
    }

    [Fact]
    public void Artifact_limit_equal_to_the_size_is_allowed()
    {
        var output = Derive(Utf8("abcd"), CorpusLimits.Default with { MaxArtifactBytes = 4 });
        Assert.Single(output.Segments);
    }
}
