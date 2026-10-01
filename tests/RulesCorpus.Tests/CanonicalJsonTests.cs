using System.Text;
using RulesCorpus.Json;

namespace RulesCorpus.Tests;

public class CanonicalJsonWriterTests
{
    private static byte[] Compact(CjValue v) => CanonicalJsonWriter.ToCompact(v);

    private static byte[] Indented(CjValue v) => CanonicalJsonWriter.ToIndented(v);

    [Fact]
    public void Empty_object_and_array_are_written_as_braces_and_brackets_in_both_forms()
    {
        Assert.Equal("{}"u8.ToArray(), Compact(new CjObject()));
        Assert.Equal("[]"u8.ToArray(), Compact(new CjArray()));
        Assert.Equal("{}\n"u8.ToArray(), Indented(new CjObject()));
        Assert.Equal("[]\n"u8.ToArray(), Indented(new CjArray()));
    }

    [Fact]
    public void Object_members_are_sorted_by_utf16_code_unit_regardless_of_insertion_order()
    {
        CjObject obj = new CjObject()
            .Add("b", CjValue.Of(1))
            .Add("aa", CjValue.Of(4))
            .Add("a", CjValue.Of(2))
            .Add("B", CjValue.Of(3));

        Assert.Equal("{\"B\":3,\"a\":2,\"aa\":4,\"b\":1}"u8.ToArray(), Compact(obj));
    }

    [Fact]
    public void Strings_escape_quote_backslash_and_the_five_short_controls_and_other_controls_as_lowercase_u00xx()
    {
        string value = "\"\\\b\t\n\f\r\u0000\u0001\u001f\u001a";

        byte[] expected = Encoding.ASCII.GetBytes("\"\\\"\\\\\\b\\t\\n\\f\\r\\u0000\\u0001\\u001f\\u001a\"");
        Assert.Equal(expected, Compact(CjValue.Of(value)));
    }

    [Fact]
    public void Everything_else_is_literal_utf8_including_del_slash_and_non_bmp()
    {
        // U+007F, '/', U+00E9, U+2028, U+1F600.
        string value = "\u007f/\u00e9\u2028\U0001F600";

        byte[] expected = [0x22, 0x7F, 0x2F, 0xC3, 0xA9, 0xE2, 0x80, 0xA8, 0xF0, 0x9F, 0x98, 0x80, 0x22];
        Assert.Equal(expected, Compact(CjValue.Of(value)));
    }

    [Fact]
    public void Nested_values_indent_two_spaces_with_colon_space_one_item_per_line_and_a_final_newline()
    {
        CjObject obj = new CjObject()
            .Add("c", new CjObject())
            .Add("a", new CjArray([CjValue.Of(1), new CjObject().Add("b", CjValue.Of(true))]))
            .Add("d", new CjArray())
            .Add("e", CjValue.Of(false));

        const string expected = "{\n  \"a\": [\n    1,\n    {\n      \"b\": true\n    }\n  ],\n  \"c\": {},\n  \"d\": [],\n  \"e\": false\n}\n";
        Assert.Equal(Encoding.ASCII.GetBytes(expected), Indented(obj));
        Assert.Equal("{\"a\":[1,{\"b\":true}],\"c\":{},\"d\":[],\"e\":false}"u8.ToArray(), Compact(obj));
    }

    [Fact]
    public void Numbers_are_plain_decimal_integers_up_to_two_to_the_fifty_three_minus_one()
    {
        Assert.Equal("0"u8.ToArray(), Compact(CjValue.Of(0L)));
        Assert.Equal("10"u8.ToArray(), Compact(CjValue.Of(10L)));
        Assert.Equal("9007199254740991"u8.ToArray(), Compact(CjValue.Of(9007199254740991L)));
        Assert.Throws<ArgumentException>(() => Compact(CjValue.Of(9007199254740992L)));
        Assert.Throws<ArgumentException>(() => Compact(CjValue.Of(-1L)));
    }

    // Built in code: a lone surrogate in attribute data does not survive test discovery.
    [Theory]
    [InlineData("", 0xD800, "")]
    [InlineData("a", 0xDC00, "")]
    [InlineData("", 0xD800, "x")]
    [InlineData("", 0xDBFF, "\u00e9")]
    public void A_lone_surrogate_cannot_be_written(string before, int unit, string after)
    {
        string value = before + (char)unit + after;

        Assert.Throws<ArgumentException>(() => Compact(CjValue.Of(value)));
    }
}

public class CanonicalJsonReaderTests
{
    private static (CjValue? Value, List<CorpusError> Errors) Read(string json) => Read(Encoding.UTF8.GetBytes(json));

    private static (CjValue? Value, List<CorpusError> Errors) Read(byte[] json)
    {
        var errors = new List<CorpusError>();
        CjValue? value = CanonicalJsonReader.Parse(json, errors);
        return (value, errors);
    }

    private static void AssertRefused(string json, string reasonFragment) => AssertRefused(Encoding.UTF8.GetBytes(json), reasonFragment);

    private static void AssertRefused(byte[] json, string reasonFragment)
    {
        (CjValue? value, List<CorpusError> errors) = Read(json);
        Assert.Null(value);
        CorpusError error = Assert.Single(errors);
        Assert.Contains(reasonFragment, error.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Any_whitespace_is_accepted_and_the_value_reads_back_to_the_same_canonical_bytes()
    {
        (CjValue? value, List<CorpusError> errors) = Read(" {\r\n\t\"b\" : [ 1 , 2 ] ,\n \"a\":\"x\" }\n\n");

        Assert.Empty(errors);
        Assert.Equal("{\"a\":\"x\",\"b\":[1,2]}"u8.ToArray(), CanonicalJsonWriter.ToCompact(value!));
    }

    [Fact]
    public void Escapes_are_decoded_so_differently_escaped_inputs_share_one_canonical_form()
    {
        (CjValue? value, _) = Read("\"\\u00e9\\/\\u0041\"");

        Assert.Equal("é/A", ((CjString)value!).Value);
    }

    [Fact]
    public void A_duplicate_key_is_refused_with_its_path()
    {
        (CjValue? value, List<CorpusError> errors) = Read("{\"a\":{\"k\":1,\"k\":2}}");

        Assert.Null(value);
        CorpusError error = Assert.Single(errors);
        Assert.Equal("$.a.k", error.Path);
        Assert.Contains("duplicate", error.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Null_is_refused_with_its_path()
    {
        (_, List<CorpusError> errors) = Read("{\"a\":[1,null]}");

        CorpusError error = Assert.Single(errors);
        Assert.Equal("$.a[1]", error.Path);
        Assert.Contains("null", error.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("-0")]
    [InlineData("1.0")]
    [InlineData("1e3")]
    [InlineData("1E3")]
    [InlineData("0.5")]
    public void A_negative_fractional_or_exponent_number_is_refused(string number)
    {
        AssertRefused("{\"n\":" + number + "}", "canonical JSON numbers");
    }

    [Fact]
    public void A_number_above_two_to_the_fifty_three_minus_one_is_refused()
    {
        AssertRefused("9007199254740992", "too large");
        AssertRefused("99999999999999999999", "canonical JSON numbers");
        Assert.Empty(Read("9007199254740991").Errors);
    }

    [Fact]
    public void A_leading_zero_is_refused()
    {
        (CjValue? value, List<CorpusError> errors) = Read("01");

        Assert.Null(value);
        Assert.Single(errors);
    }

    [Theory]
    [InlineData("\"\\ud800\"")]
    [InlineData("\"\\udc00\"")]
    [InlineData("\"\\ud800\\u0041\"")]
    [InlineData("{\"\\ud800\":1}")]
    public void An_escaped_lone_surrogate_is_refused(string json)
    {
        (CjValue? value, List<CorpusError> errors) = Read(json);

        Assert.Null(value);
        Assert.Single(errors);
    }

    [Fact]
    public void An_escaped_surrogate_pair_is_accepted()
    {
        (CjValue? value, _) = Read("\"\\ud83d\\ude00\"");

        Assert.Equal("\U0001F600", ((CjString)value!).Value);
    }

    [Fact]
    public void Invalid_utf8_is_refused()
    {
        (CjValue? value, List<CorpusError> errors) = Read([0x22, 0xFF, 0x22]);

        Assert.Null(value);
        Assert.Single(errors);
    }

    [Fact]
    public void Nesting_to_the_depth_limit_reads_and_one_deeper_is_refused_without_overflowing_the_stack()
    {
        static string Nested(int depth) => new string('[', depth) + new string(']', depth);

        (CjValue? atLimit, List<CorpusError> okErrors) = Read(Nested(64));
        Assert.NotNull(atLimit);
        Assert.Empty(okErrors);

        AssertRefused(Nested(65), "not valid JSON");
        AssertRefused(Nested(100_000), "not valid JSON");
    }

    [Fact]
    public void A_byte_order_mark_is_refused()
    {
        AssertRefused([0xEF, 0xBB, 0xBF, (byte)'{', (byte)'}'], "byte-order mark");
    }

    [Theory]
    [InlineData("{\"a\":1,}")]
    [InlineData("[1,]")]
    [InlineData("{\"a\":1} // c")]
    [InlineData("/* c */ {}")]
    [InlineData("{} {}")]
    [InlineData("{}x")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{'a':1}")]
    public void Trailing_commas_comments_trailing_content_and_empty_input_are_refused(string json)
    {
        (CjValue? value, List<CorpusError> errors) = Read(json);

        Assert.Null(value);
        Assert.Single(errors);
    }
}
