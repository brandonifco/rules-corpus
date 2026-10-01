namespace RulesCorpus.Adapters.Xml;

internal static class Refusal
{
    public static CorpusAdapterException Of(string message) => new("xml adapter: " + message);
}
