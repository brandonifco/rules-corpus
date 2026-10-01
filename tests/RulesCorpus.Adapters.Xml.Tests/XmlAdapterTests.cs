using System.Text;
using System.Xml.Linq;
using RulesCorpus.Adapters;
using RulesCorpus.Adapters.Xml;
using static RulesCorpus.Adapters.Xml.Tests.Harness;

namespace RulesCorpus.Adapters.Xml.Tests;

public sealed class XmlAdapterTests
{
    private static string[] Shape(AdapterOutput output) =>
        [.. output.Segments.Select(s => $"{s.Id} {s.Start} {s.Length} {s.Locator} {Assert.Single(s.Sources).Bytes}")];

    [Fact]
    public void Adapter_identity_is_xml_version_1_and_deterministic()
    {
        var adapter = new XmlAdapter();
        Assert.Equal("xml", adapter.Id);
        Assert.Equal("1", adapter.Version);
        Assert.True(adapter.IsDeterministic);
    }

    [Fact]
    public void The_excerpt_gives_one_segment_per_section_in_document_order()
    {
        byte[] input = Fixture("excerpt.xml");

        AdapterOutput output = Derive(input, Sections);

        Assert.Equal("application/xml", output.MediaType);
        Assert.Equal(DerivationFidelity.Lossless, output.Fidelity);
        Assert.Empty(output.Losses);
        Assert.Equal(input, output.Canonical.ToArray());
        Assert.Equal(["107.1", "107.2"], output.Segments.Select(s => s.Id));
        Assert.All(output.Segments, s => Assert.Null(s.Locator));
    }

    [Fact]
    public void Each_segment_is_exactly_its_element_from_open_tag_to_close_tag()
    {
        AdapterOutput output = Derive(Fixture("excerpt.xml"), Sections);

        foreach (AdapterSegment segment in output.Segments)
        {
            string text = SegmentText(output, segment);
            Assert.StartsWith("<DIV8 N=\"" + segment.Id + "\"", text, StringComparison.Ordinal);
            Assert.EndsWith("</DIV8>", text, StringComparison.Ordinal);
            XElement parsed = XElement.Parse(text);
            Assert.Equal(segment.Id, (string?)parsed.Attribute("N"));
        }
    }

    [Fact]
    public void Source_spans_are_the_same_byte_range_in_the_input_the_canonical_bytes_equal()
    {
        byte[] input = Fixture("excerpt.xml");
        AdapterOutput output = Derive(input, Sections);

        foreach (AdapterSegment segment in output.Segments)
        {
            ByteRange range = Assert.Single(segment.Sources).Bytes!;
            Assert.Null(Assert.Single(segment.Sources).Pages);
            Assert.Equal(segment.Start, range.Start);
            Assert.Equal(segment.Length, range.Length);
            Assert.Equal(
                input.AsSpan((int)range.Start, (int)range.Length).ToArray(),
                output.Canonical.Span.Slice((int)segment.Start, (int)segment.Length).ToArray());
        }
    }

    [Fact]
    public void Offsets_are_bytes_not_characters_across_multibyte_text_tabs_and_astral_characters()
    {
        string xml = "<?xml version=\"1.0\"?>\n<r>\n\t<s k=\"a\">§ — \U0001F600 one</s>\n  <s k=\"b\">two</s>\n</r>\n";

        AdapterOutput output = Derive(Utf8(xml), ("segmentElement", "s"), ("idAttribute", "k"));

        Assert.Equal(["a", "b"], output.Segments.Select(s => s.Id));
        Assert.Equal("<s k=\"a\">§ — \U0001F600 one</s>", SegmentText(output, output.Segments[0]));
        Assert.Equal("<s k=\"b\">two</s>", SegmentText(output, output.Segments[1]));
    }

    [Fact]
    public void Repeated_runs_give_identical_results()
    {
        byte[] input = Fixture("excerpt.xml");

        AdapterOutput first = Derive(input, Sections);
        AdapterOutput second = Derive(input, Sections);

        Assert.Equal(first.Canonical.ToArray(), second.Canonical.ToArray());
        Assert.Equal(Shape(first), Shape(second));
    }

    [Fact]
    public void Parameter_order_does_not_change_the_result()
    {
        byte[] input = Fixture("excerpt.xml");

        AdapterOutput forward = Derive(input, Sections);
        AdapterOutput reversed = Derive(input, [Sections[1], Sections[0]]);

        Assert.Equal(Shape(forward), Shape(reversed));
    }

    [Fact]
    public void A_segment_element_inside_another_is_refused()
    {
        CorpusAdapterException e = Refused(Utf8("<r><s k=\"a\"><s k=\"b\">x</s></s></r>"), ("segmentElement", "s"), ("idAttribute", "k"));

        Assert.Contains("nested", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_segment_element_without_the_id_attribute_is_refused_naming_its_line()
    {
        CorpusAdapterException e = Refused(Utf8("<r>\n<s k=\"a\">x</s>\n<s>y</s>\n</r>"), ("segmentElement", "s"), ("idAttribute", "k"));

        Assert.Contains("line 3", e.Message, StringComparison.Ordinal);
        Assert.Contains("'k'", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_repeated_segment_id_is_refused()
    {
        CorpusAdapterException e = Refused(Utf8("<r><s k=\"a\">x</s><s k=\"a\">y</s></r>"), ("segmentElement", "s"), ("idAttribute", "k"));

        Assert.Contains("'a'", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_element_that_matches_nothing_is_refused_rather_than_producing_no_segments()
    {
        CorpusAdapterException e = Refused(Fixture("excerpt.xml"), ("segmentElement", "NOPE"), ("idAttribute", "N"));

        Assert.Contains("no segments", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_self_closed_segment_element_is_refused()
    {
        CorpusAdapterException e = Refused(Utf8("<r><s k=\"a\"/></r>"), ("segmentElement", "s"), ("idAttribute", "k"));

        Assert.Contains("empty element", e.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("<r><s k=\"a\">x</r>")]
    [InlineData("<r><s k=\"a\">x</s>")]
    [InlineData("<r><s k=\"a\">x</s></r><r/>")]
    [InlineData("<r><s k=\"a\">&unknown;</s></r>")]
    [InlineData("<r><s k=\"a\">x</s></r")]
    [InlineData("not xml")]
    [InlineData("")]
    public void Malformed_input_is_refused_deterministically(string xml)
    {
        string first = Refused(Utf8(xml), ("segmentElement", "s"), ("idAttribute", "k")).Message;
        string second = Refused(Utf8(xml), ("segmentElement", "s"), ("idAttribute", "k")).Message;

        Assert.Equal(first, second);
        Assert.StartsWith("xml adapter:", first, StringComparison.Ordinal);
    }

    [Fact]
    public void A_doctype_is_refused_so_no_entity_or_external_content_is_ever_read()
    {
        CorpusAdapterException e = Refused(
            Utf8("<?xml version=\"1.0\"?><!DOCTYPE r [<!ENTITY e \"x\">]><r><s k=\"a\">&e;</s></r>"),
            ("segmentElement", "s"), ("idAttribute", "k"));

        Assert.Contains("DOCTYPE", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_byte_order_mark_invalid_utf8_a_carriage_return_or_another_declared_encoding_is_refused()
    {
        var p = new[] { ("segmentElement", "s"), ("idAttribute", "k") };

        Assert.Contains("byte-order mark", Refused([0xEF, 0xBB, 0xBF, .. Utf8("<s k=\"a\">x</s>")], p).Message, StringComparison.Ordinal);
        Assert.Contains("UTF-8", Refused([.. Utf8("<s k=\"a\">"), 0xFF, .. Utf8("</s>")], p).Message, StringComparison.Ordinal);
        Assert.Contains("carriage return", Refused(Utf8("<r>\r\n<s k=\"a\">x</s></r>"), p).Message, StringComparison.Ordinal);
        Assert.Contains("encoding", Refused(Utf8("<?xml version=\"1.0\" encoding=\"ISO-8859-1\"?><s k=\"a\">x</s>"), p).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_explicit_utf8_declaration_is_accepted()
    {
        AdapterOutput output = Derive(Utf8("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<s k=\"a\">x</s>"), ("segmentElement", "s"), ("idAttribute", "k"));

        Assert.Equal("a", Assert.Single(output.Segments).Id);
    }

    [Theory]
    [InlineData("segmentElement")]
    [InlineData("idAttribute")]
    public void A_missing_required_parameter_is_refused(string missing)
    {
        var p = Sections.Where(x => x.Key != missing).ToArray();

        Assert.Contains(missing, Refused(Fixture("excerpt.xml"), p).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unknown_parameter_is_refused()
    {
        CorpusAdapterException e = Refused(Fixture("excerpt.xml"), [.. Sections, ("segmentation", "headings")]);

        Assert.Contains("segmentation", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void More_segments_than_the_limit_allows_is_refused()
    {
        var limits = new CorpusLimits { MaxSegments = 1 };

        var e = Assert.Throws<CorpusAdapterException>(() => Derive(Fixture("excerpt.xml"), limits, Sections));

        Assert.Contains("MaxSegments", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_input_over_the_artifact_limit_is_refused()
    {
        var limits = new CorpusLimits { MaxArtifactBytes = 10 };

        var e = Assert.Throws<CorpusAdapterException>(() => Derive(Fixture("excerpt.xml"), limits, Sections));

        Assert.Contains("MaxArtifactBytes", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Refusals_name_the_input_artifact()
    {
        Assert.Contains("(input 'input')", Refused(Utf8("not xml"), Sections).Message, StringComparison.Ordinal);
    }
}
