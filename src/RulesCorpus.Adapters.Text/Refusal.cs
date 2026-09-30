namespace RulesCorpus.Adapters.Text;

internal static class Refusal
{
    public static CorpusAdapterException Of(string message) => new("text adapter: " + message);

    public static CorpusAdapterException Of(string message, Exception inner) => new("text adapter: " + message, inner);
}
