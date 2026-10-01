using System.Text;
using RulesCorpus.Adapters;
using RulesCorpus.Adapters.Xml;

namespace RulesCorpus.Adapters.Xml.Tests;

internal static class Harness
{
    public static readonly (string Key, string Value)[] Sections = [("segmentElement", "DIV8"), ("idAttribute", "N")];

    public static byte[] Fixture(string name) =>
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    public static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

    public static AdapterOutput Derive(byte[] bytes, params (string Key, string Value)[] parameters) =>
        Derive(bytes, CorpusLimits.Default, parameters);

    public static AdapterOutput Derive(byte[] bytes, CorpusLimits limits, params (string Key, string Value)[] parameters)
    {
        var map = parameters.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        return new XmlAdapter().Derive(new AdapterInput("input", bytes, map, limits));
    }

    public static CorpusAdapterException Refused(byte[] bytes, params (string Key, string Value)[] parameters) =>
        Assert.Throws<CorpusAdapterException>(() => Derive(bytes, parameters));

    public static string SegmentText(AdapterOutput output, AdapterSegment segment) =>
        Encoding.UTF8.GetString(output.Canonical.Span.Slice(checked((int)segment.Start), checked((int)segment.Length)));
}
