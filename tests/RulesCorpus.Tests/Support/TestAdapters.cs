using System.Text;
using RulesCorpus.Adapters;

namespace RulesCorpus.Tests.Support;

/// <summary>
/// A deterministic test adapter: the canonical bytes are the input unchanged, and every
/// non-empty line is a segment <c>{prefix}l{n}</c> mapped back to the same byte range of the
/// input. Refuses invalid UTF-8 and unknown parameters, as the contract requires.
/// </summary>
internal sealed class LinesAdapter(string version = "1") : ICorpusAdapter
{
    public string Id => "lines";

    public string Version { get; } = version;

    public bool IsDeterministic => true;

    public AdapterOutput Derive(AdapterInput input)
    {
        string prefix = string.Empty;
        foreach (KeyValuePair<string, string> p in input.Parameters)
        {
            if (p.Key == "prefix")
            {
                prefix = p.Value;
            }
            else
            {
                throw new CorpusAdapterException($"unknown parameter '{p.Key}'");
            }
        }

        byte[] bytes = input.Bytes.ToArray();
        try
        {
            _ = new UTF8Encoding(false, true).GetString(bytes);
        }
        catch (DecoderFallbackException e)
        {
            throw new CorpusAdapterException("input is not UTF-8", e);
        }

        var segments = new List<AdapterSegment>();
        int start = 0;
        int n = 0;
        for (int i = 0; i <= bytes.Length; i++)
        {
            if (i == bytes.Length || bytes[i] == (byte)'\n')
            {
                if (i > start)
                {
                    n++;
                    segments.Add(new AdapterSegment(
                        $"{prefix}l{n}", start, i - start, $"line {n}", [new AdapterSourceSpan(null, new ByteRange(start, i - start))]));
                }

                start = i + 1;
            }
        }

        return new AdapterOutput(bytes, "text/plain", DerivationFidelity.Lossless, [], segments);
    }
}

/// <summary>An adapter whose output is whatever the test says, to exercise the core's validation.</summary>
internal sealed class ScriptedAdapter(Func<AdapterInput, AdapterOutput> derive) : ICorpusAdapter
{
    public string Id { get; init; } = "lines";

    public string Version { get; init; } = "1";

    public bool IsDeterministic { get; init; } = true;

    public AdapterOutput Derive(AdapterInput input) => derive(input);
}
