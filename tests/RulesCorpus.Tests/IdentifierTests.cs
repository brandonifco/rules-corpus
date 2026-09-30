namespace RulesCorpus.Tests;

public class ContentDigestTests
{
    [Fact]
    public void Compute_matches_the_published_sha256_vectors()
    {
        Assert.Equal("sha256:e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", ContentDigest.Compute([]).ToString());
        Assert.Equal("sha256:ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", ContentDigest.Compute("abc"u8).ToString());
    }

    [Fact]
    public void Hex_is_the_bare_lowercase_digits_rules_kernel_takes_as_content_hash()
    {
        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", ContentDigest.Compute("abc"u8).Hex);
    }

    [Fact]
    public void Parse_round_trips_the_written_form_and_compares_by_value()
    {
        string written = "sha256:" + new string('0', 63) + "f";

        ContentDigest a = ContentDigest.Parse(written);
        ContentDigest b = ContentDigest.Parse(written);

        Assert.Equal(written, a.ToString());
        Assert.True(a == b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.False(a != b);
        Assert.False(a == ContentDigest.Compute([]));
    }

    [Theory]
    [InlineData("")]
    [InlineData("sha256:")]
    [InlineData("SHA256:e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855")]
    [InlineData("sha256:E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855")]
    [InlineData("sha256:e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b85")]
    [InlineData("sha256:e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b8555")]
    [InlineData("sha512:e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855")]
    [InlineData("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855")]
    [InlineData("sha256:g3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855")]
    [InlineData(" sha256:e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855")]
    public void Parse_refuses_anything_but_sha256_and_64_lowercase_hex(string value)
    {
        Assert.False(ContentDigest.TryParse(value, out _));
        Assert.Throws<FormatException>(() => ContentDigest.Parse(value));
    }

    [Fact]
    public void Null_is_refused()
    {
        Assert.False(ContentDigest.TryParse(null, out _));
        Assert.Throws<ArgumentNullException>(() => ContentDigest.Parse(null!));
    }
}

public class CorpusGrammarTests
{
    [Theory]
    [InlineData("a")]
    [InlineData("cfr-14-107")]
    [InlineData("srd-5.2.1")]
    [InlineData("0")]
    [InlineData("a.b-c.d")]
    public void Valid_ids(string value) => Assert.True(CorpusGrammar.IsId(value));

    [Theory]
    [InlineData("")]
    [InlineData("A")]
    [InlineData("-a")]
    [InlineData("a-")]
    [InlineData("a--b")]
    [InlineData("a.-b")]
    [InlineData(".a")]
    [InlineData("a_b")]
    [InlineData("a b")]
    [InlineData("é")]
    [InlineData(null)]
    public void Invalid_ids(string? value) => Assert.False(CorpusGrammar.IsId(value));

    [Fact]
    public void Ids_are_at_most_128_characters()
    {
        Assert.True(CorpusGrammar.IsId(new string('a', 128)));
        Assert.False(CorpusGrammar.IsId(new string('a', 129)));
        Assert.True(CorpusGrammar.IsHashDerivation("srd-5.2.1-pdftotext-24.02.0-page-marked"));
        Assert.False(CorpusGrammar.IsHashDerivation(new string('a', 129)));
    }

    [Theory]
    [InlineData("107.51")]
    [InlineData("p13.b2")]
    [InlineData("1.401(k)-1(b)(4)(ii)")]
    [InlineData("a")]
    [InlineData("Z9")]
    [InlineData("a/b_c")]
    public void Valid_segment_ids(string value) => Assert.True(CorpusGrammar.IsSegmentId(value));

    [Theory]
    [InlineData("")]
    [InlineData("(a)")]
    [InlineData("a.")]
    [InlineData("a-")]
    [InlineData("a b")]
    [InlineData("a#b")]
    [InlineData(".a")]
    [InlineData(null)]
    public void Invalid_segment_ids(string? value) => Assert.False(CorpusGrammar.IsSegmentId(value));

    [Fact]
    public void Segment_ids_are_at_most_256_characters()
    {
        Assert.True(CorpusGrammar.IsSegmentId(new string('a', 256)));
        Assert.False(CorpusGrammar.IsSegmentId(new string('a', 257)));
    }

    [Theory]
    [InlineData("pageMarker", true)]
    [InlineData("a", true)]
    [InlineData("a1B2", true)]
    [InlineData("PageMarker", false)]
    [InlineData("1a", false)]
    [InlineData("page-marker", false)]
    [InlineData("", false)]
    public void Keys(string value, bool valid) => Assert.Equal(valid, CorpusGrammar.IsKey(value));

    [Fact]
    public void Keys_are_at_most_64_characters()
    {
        Assert.True(CorpusGrammar.IsKey(new string('a', 64)));
        Assert.False(CorpusGrammar.IsKey(new string('a', 65)));
    }

    [Theory]
    [InlineData("2026-01-01", true)]
    [InlineData("2024-02-29", true)]
    [InlineData("2026-02-29", false)]
    [InlineData("2026-13-01", false)]
    [InlineData("2026-1-01", false)]
    [InlineData("26-01-01", false)]
    [InlineData("2026/01/01", false)]
    [InlineData("0000-01-01", false)]
    [InlineData("2026-01-01T00:00", false)]
    [InlineData("２０２６-01-01", false)]
    public void Dates_are_real_calendar_dates_in_yyyy_mm_dd(string value, bool valid) => Assert.Equal(valid, CorpusGrammar.IsDate(value));

    [Theory]
    [InlineData("text/plain", true)]
    [InlineData("application/pdf", true)]
    [InlineData("application/vnd.api+json", true)]
    [InlineData("Text/plain", false)]
    [InlineData("text/plain; charset=utf-8", false)]
    [InlineData("text", false)]
    [InlineData("text/", false)]
    [InlineData("/plain", false)]
    [InlineData("text/plain/x", false)]
    [InlineData("text/-plain", false)]
    public void Media_types_are_lowercase_type_slash_subtype_without_parameters(string value, bool valid) =>
        Assert.Equal(valid, CorpusGrammar.IsMediaType(value));

    [Fact]
    public void Media_types_are_at_most_128_characters()
    {
        Assert.True(CorpusGrammar.IsMediaType("a/" + new string('b', 126)));
        Assert.False(CorpusGrammar.IsMediaType("a/" + new string('b', 127)));
    }
}
