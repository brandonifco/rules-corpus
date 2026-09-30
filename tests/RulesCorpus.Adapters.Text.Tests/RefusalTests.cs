using RulesCorpus.Adapters;
using RulesCorpus.Adapters.Text;
using static RulesCorpus.Adapters.Text.Tests.Harness;

namespace RulesCorpus.Adapters.Text.Tests;

public sealed class RefusalTests
{
    private const string Id = @"^# (?<id>\S+)";

    private static void AssertRefused(byte[] input, string expected, params (string, string)[] parameters)
    {
        var e = Refused(input, parameters);
        Assert.Contains(expected, e.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(new byte[] { (byte)'a', (byte)'b', (byte)'\n', 0xC3, 0x28 }, 3, 2)] // bad continuation
    [InlineData(new byte[] { (byte)'a', 0xE2, 0x82 }, 1, 1)] // truncated at end
    [InlineData(new byte[] { 0xC0, 0xAF }, 0, 1)] // overlong
    [InlineData(new byte[] { (byte)'x', 0xED, 0xA0, 0x80 }, 1, 1)] // encoded surrogate
    [InlineData(new byte[] { (byte)'\n', (byte)'\n', 0xFF }, 2, 3)] // never valid
    public void Invalid_utf8_is_refused_at_its_byte_offset_and_line(byte[] input, int offset, int line)
    {
        var e = Refused(input);
        Assert.Contains($"not valid UTF-8 at byte offset {offset} (line {line}", e.Message, StringComparison.Ordinal);
        Assert.Contains("input 'input'", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Bom_is_refused_by_default_and_with_reject()
    {
        byte[] input = [0xEF, 0xBB, 0xBF, (byte)'a'];
        AssertRefused(input, "byte-order mark");
        AssertRefused(input, "byte-order mark", ("bom", "reject"));
    }

    [Fact]
    public void Cr_with_newlines_preserve_is_refused_at_its_offset()
    {
        AssertRefused(Utf8("a\nbc\r\n"), "CR at byte offset 4 (line 2)", ("newlines", "preserve"));
        AssertRefused([0xEF, 0xBB, 0xBF, (byte)'\r'], "CR at byte offset 3 (line 1)", ("bom", "strip"), ("newlines", "preserve"));
    }

    [Fact]
    public void Newlines_preserve_accepts_lf_only_input()
    {
        var output = Derive(Utf8("a\nb"), ("newlines", "preserve"));
        Assert.Empty(output.Losses);
    }

    [Theory]
    [InlineData("normalize")]
    [InlineData("Segmentation")]
    [InlineData("")]
    public void Unknown_parameter_is_refused_by_name(string key) =>
        AssertRefused(Utf8("a"), $"Unknown parameter '{key}'", (key, "x"));

    [Theory]
    [InlineData("segmentation", "paragraphs")]
    [InlineData("segmentation", "Blocks")]
    [InlineData("bom", "keep")]
    [InlineData("newlines", "crlf")]
    [InlineData("pagesContiguous", "yes")]
    public void Unknown_value_is_refused_by_parameter(string key, string value)
    {
        (string, string)[] parameters = key == "pagesContiguous"
            ? [(key, value), ("pageMarker", @"^\{(\d+)\}$")]
            : [(key, value)];
        AssertRefused(Utf8("a"), $"Parameter '{key}' is '{value}'", parameters);
    }

    [Fact]
    public void Heading_pattern_without_id_group_is_refused() =>
        AssertRefused(Utf8("# a"), "no named group 'id'", ("segmentation", "headings"), ("headingPattern", "^# (?<name>.+)"));

    [Fact]
    public void Headings_without_a_pattern_is_refused() =>
        AssertRefused(Utf8("# a"), "requires parameter 'headingPattern'", ("segmentation", "headings"));

    [Theory]
    [InlineData("whole")]
    [InlineData("blocks")]
    public void Heading_pattern_without_headings_segmentation_is_refused(string segmentation) =>
        AssertRefused(Utf8("# a"), "'headingPattern' is only meaningful", ("segmentation", segmentation), ("headingPattern", Id));

    [Fact]
    public void Pages_contiguous_without_a_page_marker_is_refused() =>
        AssertRefused(Utf8("a"), "'pagesContiguous' requires parameter 'pageMarker'", ("pagesContiguous", "false"));

    [Fact]
    public void Duplicate_heading_id_is_refused_with_its_line() =>
        AssertRefused(Utf8("# a\nx\n# b\n# a\n"), "Heading on line 4 repeats segment id 'a'", ("segmentation", "headings"), ("headingPattern", Id));

    [Theory]
    [InlineData("# 1.")] // may not end with '.'
    [InlineData("# -1")] // must start with a letter or digit
    [InlineData("# a:b")] // ':' is outside the grammar
    [InlineData("# aé")] // ASCII only
    public void Invalid_heading_id_is_refused(string line) =>
        AssertRefused(Utf8(line), "which is not a valid segment id", ("segmentation", "headings"), ("headingPattern", Id));

    [Fact]
    public void Heading_id_over_256_characters_is_refused() =>
        AssertRefused(Utf8("# " + new string('x', 257)), "not a valid segment id", ("segmentation", "headings"), ("headingPattern", Id));

    [Fact]
    public void Unmatched_optional_id_group_is_refused() =>
        AssertRefused(Utf8("# \n"), "gives segment id ''", ("segmentation", "headings"), ("headingPattern", @"^# (?<id>\w+)?"));

    [Fact]
    public void Empty_locator_group_is_refused() =>
        AssertRefused(Utf8("# a\n"), "empty or unmatched 'locator' group", ("segmentation", "headings"), ("headingPattern", @"^# (?<id>\w)(?<locator>x?)"));

    [Fact]
    public void Heading_pattern_that_matches_nothing_is_refused() =>
        AssertRefused(Utf8("a\nb"), "matched no line", ("segmentation", "headings"), ("headingPattern", Id));

    [Fact]
    public void Pages_contiguous_violation_is_refused_at_the_marker()
    {
        AssertRefused(
            Utf8("{1}\na\n{3}\nb"), "line 3 names page 3, but pagesContiguous requires page 2",
            ("segmentation", "blocks"), ("pageMarker", @"^\{(\d+)\}$"), ("pagesContiguous", "true"));
    }

    [Fact]
    public void A_page_number_recurring_after_another_page_giving_duplicate_block_ids_is_refused()
    {
        AssertRefused(
            Utf8("{1}\na\n{2}\nb\n{1}\nc"), "Segment id 'p1.b1' occurs twice",
            ("segmentation", "blocks"), ("pageMarker", @"^\{(\d+)\}$"));
    }

    [Fact]
    public void Page_range_running_backwards_is_refused()
    {
        AssertRefused(Utf8("{5}\na\n{3}\nb"), "starts on page 5 and ends on page 3", ("pageMarker", @"^\{(\d+)\}$"));
    }

    [Theory]
    [InlineData("headingPattern", @"^(?<id>a)\1")]
    [InlineData("headingPattern", @"^(?<id>a)(?=b)")]
    [InlineData("pageMarker", @"^\{(\d+)\}(?<=\})$")]
    [InlineData("pageMarker", @"^(?>\{(\d+)\})$")]
    public void Construct_unsupported_by_nonbacktracking_is_refused(string key, string pattern)
    {
        (string, string)[] parameters = key == "headingPattern"
            ? [("segmentation", "headings"), (key, pattern)]
            : [(key, pattern)];
        AssertRefused(Utf8("a"), $"Parameter '{key}' uses a construct that linear-time (NonBacktracking) matching does not support", parameters);
    }

    [Theory]
    [InlineData("(")]
    [InlineData("[a-")]
    [InlineData(@"\")]
    public void Unparseable_pattern_is_refused(string pattern) =>
        AssertRefused(Utf8("a"), "Parameter 'pageMarker' is not a valid regular expression", ("pageMarker", pattern));

    [Theory]
    [InlineData(@"^\{\d+\}$", 0)]
    [InlineData(@"^\{(\d)(\d*)\}$", 2)]
    [InlineData(@"^(?<p>\{(\d+)\})$", 2)]
    public void Page_marker_needs_exactly_one_capturing_group(string pattern, int groups) =>
        AssertRefused(Utf8("{1}"), $"'pageMarker' has {groups} capturing groups", ("pageMarker", pattern));

    [Theory]
    [InlineData("{x}")]
    [InlineData("{0}")]
    [InlineData("{01}")]
    [InlineData("{-1}")]
    [InlineData("{2147483648}")]
    [InlineData("{99999999999}")]
    [InlineData("{١}")] // ARABIC-INDIC DIGIT ONE: a digit, but not an ASCII one
    public void Page_marker_group_that_is_not_a_positive_integer_is_refused(string line) =>
        AssertRefused(Utf8("a\n" + line + "\nb"), "Page marker on line 2", ("pageMarker", @"^\{(.*)\}$"));

    [Fact]
    public void Page_marker_group_that_does_not_participate_is_refused() =>
        AssertRefused(Utf8("{}"), "captured ''", ("pageMarker", @"^\{(\d+)?\}$"));

    [Fact]
    public void Segment_limit_exceeded_is_refused() =>
        Assert.Contains(
            "more than MaxSegments (2)",
            Assert.Throws<CorpusAdapterException>(() => Derive(
                Utf8("a\n\nb\n\nc"), CorpusLimits.Default with { MaxSegments = 2 }, ("segmentation", "blocks"))).Message,
            StringComparison.Ordinal);

    [Fact]
    public void Artifact_limit_exceeded_is_refused() =>
        Assert.Contains(
            "over MaxArtifactBytes (3)",
            Assert.Throws<CorpusAdapterException>(() => Derive(
                Utf8("abcd"), CorpusLimits.Default with { MaxArtifactBytes = 3 })).Message,
            StringComparison.Ordinal);

    [Fact]
    public void Empty_input_has_no_whole_segment_and_is_refused() =>
        AssertRefused([], "Input is empty");

    [Fact]
    public void Blank_input_has_no_blocks_and_is_refused() =>
        AssertRefused(Utf8(" \n\t\n"), "found no block", ("segmentation", "blocks"));

    [Fact]
    public void Parameters_are_checked_before_the_input() =>
        AssertRefused([0xFF], "Unknown parameter 'x'", ("x", "y"));

    [Theory]
    [InlineData("x\U0001F600" + "1\n", @"[\uDC00-\uDFFF]([0-9]+)")] // starts on the low half
    [InlineData("1\U0001F600x\n", @"([0-9]+)[\uD800-\uDBFF]")] // ends after the high half
    [InlineData("a 1\U0001F600\n", @"[\uDC00-\uDFFF]|([0-9]+)[\uD800-\uDBFF]")]
    public void A_page_marker_match_that_splits_a_surrogate_pair_is_refused(string text, string pattern) =>
        AssertRefused(Utf8(text), "inside a surrogate pair", ("pageMarker", pattern));

    [Fact]
    public void A_heading_locator_that_captures_half_a_surrogate_pair_is_refused() =>
        AssertRefused(
            Utf8("a \U0001F600\n"),
            "inside a surrogate pair",
            ("segmentation", "headings"), ("headingPattern", "^(?<id>[a-z]+) (?<locator>.)"));

    [Fact]
    public void Null_input_is_an_argument_error_not_a_refusal() =>
        Assert.Throws<ArgumentNullException>(() => new TextAdapter().Derive(null!));
}
